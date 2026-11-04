using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
using Windows.Devices.Radios;

namespace SubZeroDev.WindowManager.Plugins.QuickSettings;

/// <summary>A device that is missing or refused a change, with the contract's code (<c>DeviceAbsent</c> or <c>DeviceRefused</c>).</summary>
internal sealed class DeviceException(string code, string message, bool retryable) : Exception(message)
{
    public const string Absent = "DeviceAbsent";
    public const string Refused = "DeviceRefused";

    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}

/// <summary>The machine's radios: Wi-Fi, Bluetooth and the airplane-mode switch that turns every radio off.</summary>
internal interface IRadioControl : IDisposable
{
    /// <summary>Raised when a radio changed, whoever changed it.</summary>
    event EventHandler? Changed;

    /// <summary>The radio's state (<c>wifi</c>, <c>bluetooth</c>, <c>airplane</c>); null when the machine has no such device.</summary>
    Task<bool?> ReadAsync(string id, CancellationToken ct);

    /// <summary>Asks for a state; throws <see cref="DeviceException"/> when the device is absent or refuses.</summary>
    Task SetAsync(string id, bool on, CancellationToken ct);
}

/// <summary>The display brightness, 0 to 1.</summary>
internal interface IBrightnessControl : IDisposable
{
    /// <summary>Raised when the brightness changed, whoever changed it.</summary>
    event EventHandler? Changed;

    /// <summary>The brightness; null when no display on this machine can be dimmed.</summary>
    Task<double?> ReadAsync(CancellationToken ct);

    /// <summary>Asks for a level; throws <see cref="DeviceException"/> when no display exists or one refuses.</summary>
    Task SetAsync(double level, CancellationToken ct);
}

/// <summary>
/// The real radios through <c>Windows.Devices.Radios</c>. An unpackaged process may be refused access; the radios are then
/// still read but a change is refused (design question 11, checked by hand in S27.4). Airplane mode is every radio off: the
/// API has no switch of its own.
/// </summary>
internal sealed class RealRadios : IRadioControl
{
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(1);

    private readonly Timer _timer;
    private RadioAccessStatus? _access;
    private string _last = string.Empty;
    private int _polling;

    public RealRadios() => _timer = new Timer(_ => _ = PollAsync(), null, Poll, Poll);

    public event EventHandler? Changed;

    public async Task<bool?> ReadAsync(string id, CancellationToken ct)
    {
        var radios = await RadiosAsync(ct).ConfigureAwait(false);
        return id switch
        {
            "wifi" => StateOf(radios, RadioKind.WiFi),
            "bluetooth" => StateOf(radios, RadioKind.Bluetooth),
            "airplane" => radios.Count == 0 ? null : radios.All(radio => radio.State != RadioState.On),
            _ => null,
        };
    }

    public async Task SetAsync(string id, bool on, CancellationToken ct)
    {
        var radios = await RadiosAsync(ct).ConfigureAwait(false);
        var targets = id switch
        {
            "wifi" => radios.Where(radio => radio.Kind == RadioKind.WiFi).ToList(),
            "bluetooth" => radios.Where(radio => radio.Kind == RadioKind.Bluetooth).ToList(),
            "airplane" => radios.ToList(),
            _ => [],
        };
        if (targets.Count == 0)
        {
            throw new DeviceException(DeviceException.Absent, "This machine has no such radio.", retryable: false);
        }

        var access = await AccessAsync(ct).ConfigureAwait(false);
        if (access != RadioAccessStatus.Allowed)
        {
            throw new DeviceException(DeviceException.Refused, "Windows did not allow this process to control radios.", retryable: true);
        }

        // Airplane mode off turns the radios on; on turns off every radio, a radio that is already off stays so.
        var wanted = id == "airplane" ? (on ? RadioState.Off : RadioState.On) : (on ? RadioState.On : RadioState.Off);
        foreach (var radio in targets)
        {
            RadioAccessStatus status;
            try
            {
                status = await radio.SetStateAsync(wanted).AsTask(ct).ConfigureAwait(false);
            }
            catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
            {
                throw new DeviceException(DeviceException.Refused, "The radio did not accept the change.", retryable: true);
            }

            if (status != RadioAccessStatus.Allowed)
            {
                throw new DeviceException(DeviceException.Refused, "The radio refused the change.", retryable: true);
            }
        }
    }

    public void Dispose() => _timer.Dispose();

    private static bool? StateOf(IReadOnlyList<Radio> radios, RadioKind kind)
    {
        var matching = radios.Where(radio => radio.Kind == kind).ToList();
        return matching.Count == 0 ? null : matching.Any(radio => radio.State == RadioState.On);
    }

    private async Task<IReadOnlyList<Radio>> RadiosAsync(CancellationToken ct)
    {
        try
        {
            var all = await Radio.GetRadiosAsync().AsTask(ct).ConfigureAwait(false);
            return all.Where(radio => radio.Kind is RadioKind.WiFi or RadioKind.Bluetooth or RadioKind.MobileBroadband).ToList();
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private async Task<RadioAccessStatus> AccessAsync(CancellationToken ct)
    {
        if (_access is { } known && known == RadioAccessStatus.Allowed)
        {
            return known;
        }

        try
        {
            _access = await Radio.RequestAccessAsync().AsTask(ct).ConfigureAwait(false);
        }
        catch (Exception e) when (e is COMException or InvalidOperationException or UnauthorizedAccessException)
        {
            _access = RadioAccessStatus.Unspecified;
        }

        return _access.Value;
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            var radios = await RadiosAsync(CancellationToken.None).ConfigureAwait(false);
            var signature = string.Join('|', radios.Select(radio => $"{radio.Kind}:{radio.State}"));
            if (signature != _last)
            {
                var first = _last.Length == 0 && radios.Count == 0;
                _last = signature;
                if (!first)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }
}

/// <summary>
/// The real brightness: the built-in panel through the video driver's <c>\\.\LCD</c> device, and external monitors through
/// DDC/CI (the monitor configuration API). An external monitor that does not answer DDC/CI is not dimmable here.
/// </summary>
internal sealed class RealBrightness : IBrightnessControl
{
    private const uint IoctlQuerySupported = 0x230496;
    private const uint IoctlQueryDisplay = 0x230498;
    private const uint IoctlSetDisplay = 0x23049C;
    private const uint GenericReadWrite = 0xC0000000;
    private const uint ShareReadWrite = 3;
    private const uint OpenExisting = 3;
    private const byte PolicyBoth = 3;
    private static readonly TimeSpan Poll = TimeSpan.FromSeconds(2);

    private readonly Timer _timer;
    private double? _last;
    private int _polling;

    public RealBrightness() => _timer = new Timer(_ => _ = PollAsync(), null, Poll, Poll);

    public event EventHandler? Changed;

    public Task<double?> ReadAsync(CancellationToken ct) => Task.Run(Read, ct);

    public Task SetAsync(double level, CancellationToken ct) => Task.Run(() => Set(Math.Clamp(level, 0, 1)), ct);

    public void Dispose() => _timer.Dispose();

    private static double? Read()
    {
        if (ReadPanel() is { } panel)
        {
            return panel;
        }

        double? external = null;
        ForEachMonitor((handle, _) =>
        {
            if (GetMonitorBrightness(handle, out var minimum, out var current, out var maximum) && maximum > minimum)
            {
                external ??= (current - minimum) / (double)(maximum - minimum);
            }
        });
        return external;
    }

    private static void Set(double level)
    {
        var changed = SetPanel(level);
        ForEachMonitor((handle, description) =>
        {
            if (GetMonitorBrightness(handle, out var minimum, out _, out var maximum) && maximum > minimum &&
                SetMonitorBrightness(handle, (uint)Math.Round(minimum + (level * (maximum - minimum)))))
            {
                changed = true;
            }
        });
        if (!changed)
        {
            throw Read() is null
                ? new DeviceException(DeviceException.Absent, "No display on this machine can be dimmed.", retryable: false)
                : new DeviceException(DeviceException.Refused, "The display did not accept the change.", retryable: true);
        }
    }

    private static double? ReadPanel()
    {
        using var panel = CreateFile(@"\\.\LCD", GenericReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (panel.IsInvalid)
        {
            return null;
        }

        var levels = new byte[256];
        if (!DeviceIoControl(panel, IoctlQuerySupported, null, 0, levels, (uint)levels.Length, out var count, IntPtr.Zero) || count == 0)
        {
            return null;
        }

        var current = new byte[3];
        if (!DeviceIoControl(panel, IoctlQueryDisplay, null, 0, current, (uint)current.Length, out _, IntPtr.Zero))
        {
            return null;
        }

        var supported = levels.Take((int)count).OrderBy(level => level).ToList();
        var now = current[1];
        var low = supported[0];
        var high = supported[^1];
        return high > low ? Math.Clamp((now - low) / (double)(high - low), 0, 1) : null;
    }

    private static bool SetPanel(double level)
    {
        using var panel = CreateFile(@"\\.\LCD", GenericReadWrite, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (panel.IsInvalid)
        {
            return false;
        }

        var levels = new byte[256];
        if (!DeviceIoControl(panel, IoctlQuerySupported, null, 0, levels, (uint)levels.Length, out var count, IntPtr.Zero) || count == 0)
        {
            return false;
        }

        var supported = levels.Take((int)count).OrderBy(value => value).ToList();
        var target = supported[0] + (level * (supported[^1] - supported[0]));
        var chosen = supported.OrderBy(value => Math.Abs(value - target)).First();
        var request = new[] { PolicyBoth, chosen, chosen };
        return DeviceIoControl(panel, IoctlSetDisplay, request, (uint)request.Length, null, 0, out _, IntPtr.Zero);
    }

    private static void ForEachMonitor(Action<IntPtr, string> action)
    {
        var handles = new List<IntPtr>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data) =>
        {
            handles.Add(monitor);
            return true;
        }, IntPtr.Zero);
        foreach (var monitor in handles)
        {
            if (!GetNumberOfPhysicalMonitorsFromHMONITOR(monitor, out var count) || count == 0)
            {
                continue;
            }

            var physical = new PhysicalMonitor[count];
            if (!GetPhysicalMonitorsFromHMONITOR(monitor, count, physical))
            {
                continue;
            }

            try
            {
                foreach (var item in physical)
                {
                    action(item.Handle, item.Description);
                }
            }
            finally
            {
                DestroyPhysicalMonitors(count, physical);
            }
        }
    }

    private async Task PollAsync()
    {
        if (Interlocked.Exchange(ref _polling, 1) == 1)
        {
            return;
        }

        try
        {
            var now = await ReadAsync(CancellationToken.None).ConfigureAwait(false);
            if (now is null != _last is null || (now is { } a && _last is { } b && Math.Abs(a - b) > 0.005))
            {
                var first = _last is null && now is null;
                _last = now;
                if (!first)
                {
                    Changed?.Invoke(this, EventArgs.Empty);
                }
            }
        }
        finally
        {
            Volatile.Write(ref _polling, 0);
        }
    }

    private delegate bool MonitorEnumProc(IntPtr monitor, IntPtr dc, IntPtr rect, IntPtr data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PhysicalMonitor
    {
        public IntPtr Handle;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle device, uint code, byte[]? input, uint inputSize, byte[]? output, uint outputSize, out uint returned, IntPtr overlapped);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorEnumProc callback, IntPtr data);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetNumberOfPhysicalMonitorsFromHMONITOR(IntPtr monitor, out uint count);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetPhysicalMonitorsFromHMONITOR(IntPtr monitor, uint count, [Out] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyPhysicalMonitors(uint count, [In] PhysicalMonitor[] monitors);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorBrightness(IntPtr monitor, out uint minimum, out uint current, out uint maximum);

    [DllImport("dxva2.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetMonitorBrightness(IntPtr monitor, uint level);
}

/// <summary>
/// The test hook's devices: one file, <c>quicksettings.json</c>, in the hook directory, read as the machine's state and
/// rewritten when a change is accepted (so the plugin reads back what the "device" holds, I34). Every accepted change is
/// appended to <c>desktop-actions.log</c>. The file's shape:
/// <c>{ "wifi": { "present": true, "value": true }, "bluetooth": …, "airplane": …, "brightness": { "present": true, "value": 0.5, "step": 0.1 }, "refuse": [ "wifi" ] }</c>.
/// A radio listed in <c>refuse</c> refuses every change; <c>step</c> rounds an accepted brightness to the nearest step, as a
/// monitor that has few levels does.
/// </summary>
internal sealed class FakeDevices : IRadioControl, IBrightnessControl
{
    private static readonly string[] Radios = ["wifi", "bluetooth"];

    private readonly string _path;
    private readonly string _log;
    private readonly Lock _gate = new();
    private readonly Timer _timer;
    private (long Ticks, long Length) _stamp;

    public FakeDevices(string hookDirectory)
    {
        _path = Path.Combine(hookDirectory, "quicksettings.json");
        _log = Path.Combine(hookDirectory, "desktop-actions.log");
        _stamp = Stamp();
        _timer = new Timer(_ => Poll(), null, TimeSpan.FromMilliseconds(100), TimeSpan.FromMilliseconds(100));
    }

    public event EventHandler? Changed;

    public Task<bool?> ReadAsync(string id, CancellationToken ct)
    {
        var state = Load();
        if (id == "airplane")
        {
            // Derived, as on the real machine: airplane mode is every present radio off.
            var present = Radios.Where(radio => Present(state, radio)).ToList();
            return Task.FromResult<bool?>(present.Count == 0 || !AirplanePresent(state) ? null : present.All(radio => !Value(state, radio)));
        }

        return Task.FromResult<bool?>(Present(state, id) ? Value(state, id) : null);
    }

    public Task SetAsync(string id, bool on, CancellationToken ct)
    {
        lock (_gate)
        {
            var state = Load();
            var airplane = id == "airplane";
            var targets = airplane ? Radios.Where(radio => Present(state, radio)).ToList() : [id];
            if (targets.Count == 0 || (airplane ? !AirplanePresent(state) : !Present(state, id)))
            {
                throw new DeviceException(DeviceException.Absent, "This machine has no such radio.", retryable: false);
            }

            if (targets.Any(radio => Refused(state, radio)) || Refused(state, id))
            {
                throw new DeviceException(DeviceException.Refused, "The radio refused the change.", retryable: true);
            }

            var wanted = airplane ? !on : on;
            foreach (var radio in targets)
            {
                Entry(state, radio)["value"] = wanted;
            }

            Save(state);
            Record($"quicksettings set {id} {(on ? "on" : "off")}");
        }

        return Task.CompletedTask;
    }

    Task<double?> IBrightnessControl.ReadAsync(CancellationToken ct)
    {
        var state = Load();
        return Task.FromResult<double?>(Present(state, "brightness") ? Number(state, "brightness", "value", 0) : null);
    }

    Task IBrightnessControl.SetAsync(double level, CancellationToken ct)
    {
        lock (_gate)
        {
            var state = Load();
            if (!Present(state, "brightness"))
            {
                throw new DeviceException(DeviceException.Absent, "No display on this machine can be dimmed.", retryable: false);
            }

            if (Refused(state, "brightness"))
            {
                throw new DeviceException(DeviceException.Refused, "The display refused the change.", retryable: true);
            }

            var step = Number(state, "brightness", "step", 0);
            var accepted = Math.Clamp(level, 0, 1);
            if (step > 0)
            {
                accepted = Math.Clamp(Math.Round(accepted / step) * step, 0, 1);
            }

            Entry(state, "brightness")["value"] = accepted;
            Save(state);
            Record($"quicksettings set brightness {accepted.ToString("0.##", CultureInfo.InvariantCulture)}");
        }

        return Task.CompletedTask;
    }

    public void Dispose() => _timer.Dispose();

    private static bool AirplanePresent(JsonObject state) =>
        state["airplane"] is not JsonObject entry || entry["present"] is not JsonValue flag || !flag.TryGetValue<bool>(out var on) || on;

    private static bool Present(JsonObject state, string id) =>
        state[id] is JsonObject entry && (entry["present"] is not JsonValue flag || !flag.TryGetValue<bool>(out var on) || on);

    private static bool Value(JsonObject state, string id) =>
        state[id] is JsonObject { } entry && entry["value"] is JsonValue value && value.TryGetValue<bool>(out var on) && on;

    private static double Number(JsonObject state, string id, string name, double fallback) =>
        state[id] is JsonObject entry && entry[name] is JsonValue value && value.TryGetValue<double>(out var number) ? number : fallback;

    private static bool Refused(JsonObject state, string id) =>
        state["refuse"] is JsonArray list && list.Any(item => item is JsonValue value && value.TryGetValue<string>(out var name) && name == id);

    private static JsonObject Entry(JsonObject state, string id)
    {
        if (state[id] is not JsonObject entry)
        {
            entry = new JsonObject();
            state[id] = entry;
        }

        return entry;
    }

    private JsonObject Load()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(_path, Encoding.UTF8)) as JsonObject ?? new JsonObject();
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
                if (e is FileNotFoundException or DirectoryNotFoundException)
                {
                    break;
                }

                Thread.Sleep(20);
            }
        }

        return new JsonObject();
    }

    private void Save(JsonObject state)
    {
        File.WriteAllText(_path, state.ToJsonString(), new UTF8Encoding(false));
        _stamp = Stamp();
    }

    private void Record(string line)
    {
        try
        {
            File.AppendAllText(_log, line + "\n", new UTF8Encoding(false));
        }
        catch (IOException)
        {
            // A log nobody can read is not worth failing a change for.
        }
    }

    private (long, long) Stamp()
    {
        var info = new FileInfo(_path);
        return info.Exists ? (info.LastWriteTimeUtc.Ticks, info.Length) : (0, 0);
    }

    private void Poll()
    {
        var now = Stamp();
        if (now != _stamp)
        {
            _stamp = now;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }
}

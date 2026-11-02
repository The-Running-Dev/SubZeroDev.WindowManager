using System.Net.NetworkInformation;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Plugins.Indicators;

internal sealed record VolumeReading(bool Present, double Level, bool Muted);

internal enum NetworkReading { Connected, Limited, Disconnected }

internal sealed record BatteryReading(bool Present, int Percent, bool Charging);

internal sealed record Readings(VolumeReading Volume, NetworkReading Network, BatteryReading Battery);

/// <summary>Where the indicators read from and write the volume to.</summary>
internal interface IIndicatorSource : IDisposable
{
    Readings Read();

    void SetVolume(double level);

    void SetMute(bool muted);
}

/// <summary>
/// The machine: the default output device's endpoint volume (Core Audio), the Network List Manager's
/// connectivity and <c>GetSystemPowerStatus</c>. The volume is read on every call; network and battery at
/// most once a second, or at once after an address change.
/// </summary>
internal sealed class SystemSource : IIndicatorSource
{
    private static readonly TimeSpan SlowEvery = TimeSpan.FromSeconds(1);

    private readonly Lock _gate = new();
    private IMMDeviceEnumerator? _devices;
    private string? _deviceId;
    private IAudioEndpointVolume? _endpoint;
    private NetworkReading _network = NetworkReading.Disconnected;
    private BatteryReading _battery = new(false, 0, false);
    private DateTime _slowReadAt = DateTime.MinValue;
    private volatile bool _addressChanged = true;

    public SystemSource() => NetworkChange.NetworkAddressChanged += OnAddressChanged;

    public Readings Read()
    {
        lock (_gate)
        {
            var volume = ReadVolume();
            if (_addressChanged || DateTime.UtcNow - _slowReadAt >= SlowEvery)
            {
                _addressChanged = false;
                _slowReadAt = DateTime.UtcNow;
                _network = ReadNetwork();
                _battery = ReadBattery();
            }

            return new Readings(volume, _network, _battery);
        }
    }

    public void SetVolume(double level)
    {
        lock (_gate)
        {
            var context = Guid.Empty;
            Endpoint()?.SetMasterVolumeLevelScalar((float)Math.Clamp(level, 0, 1), ref context);
        }
    }

    public void SetMute(bool muted)
    {
        lock (_gate)
        {
            var context = Guid.Empty;
            Endpoint()?.SetMute(muted, ref context);
        }
    }

    public void Dispose()
    {
        NetworkChange.NetworkAddressChanged -= OnAddressChanged;
        lock (_gate)
        {
            Release();
            if (_devices is not null)
            {
                Marshal.ReleaseComObject(_devices);
                _devices = null;
            }
        }
    }

    private void OnAddressChanged(object? sender, EventArgs e) => _addressChanged = true;

    private VolumeReading ReadVolume()
    {
        try
        {
            if (Endpoint() is not { } endpoint)
            {
                return new VolumeReading(false, 0, false);
            }

            endpoint.GetMasterVolumeLevelScalar(out var level);
            endpoint.GetMute(out var muted);
            return new VolumeReading(true, Math.Round(level, 2), muted);
        }
        catch (COMException)
        {
            // The device went away between the lookup and the read; the next read finds the new default.
            Release();
            return new VolumeReading(false, 0, false);
        }
    }

    /// <summary>The default output device's endpoint volume, following a change of default; null without one.</summary>
    private IAudioEndpointVolume? Endpoint()
    {
        _devices ??= (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice device;
        try
        {
            Marshal.ThrowExceptionForHR(_devices.GetDefaultAudioEndpoint(EDataFlow.Render, ERole.Multimedia, out device));
        }
        catch (Exception e) when (e is COMException or KeyNotFoundException)
        {
            Release();
            return null;
        }

        try
        {
            device.GetId(out var id);
            if (id != _deviceId || _endpoint is null)
            {
                Release();
                var iid = typeof(IAudioEndpointVolume).GUID;
                device.Activate(ref iid, ClsCtxAll, IntPtr.Zero, out var activated);
                _endpoint = (IAudioEndpointVolume)activated;
                _deviceId = id;
            }

            return _endpoint;
        }
        finally
        {
            Marshal.ReleaseComObject(device);
        }
    }

    private void Release()
    {
        if (_endpoint is not null)
        {
            Marshal.ReleaseComObject(_endpoint);
            _endpoint = null;
        }

        _deviceId = null;
    }

    private static NetworkReading ReadNetwork()
    {
        try
        {
            var manager = (INetworkListManager)new NetworkListManager();
            try
            {
                var connectivity = manager.GetConnectivity();
                return (connectivity & (NlmIpv4Internet | NlmIpv6Internet)) != 0 ? NetworkReading.Connected
                    : connectivity != 0 ? NetworkReading.Limited
                    : NetworkReading.Disconnected;
            }
            finally
            {
                Marshal.ReleaseComObject(manager);
            }
        }
        catch (COMException)
        {
            return NetworkInterface.GetIsNetworkAvailable() ? NetworkReading.Connected : NetworkReading.Disconnected;
        }
    }

    private static BatteryReading ReadBattery()
    {
        if (!GetSystemPowerStatus(out var status) || status.BatteryFlag is NoBattery or UnknownBattery)
        {
            return new BatteryReading(false, 0, false);
        }

        var percent = status.BatteryLifePercent <= 100 ? status.BatteryLifePercent : 0;
        return new BatteryReading(true, percent, status.ACLineStatus == 1);
    }

    private const int ClsCtxAll = 0x17;
    private const int NlmIpv4Internet = 0x40;
    private const int NlmIpv6Internet = 0x400;
    private const byte NoBattery = 128;
    private const byte UnknownBattery = 255;

    private enum EDataFlow { Render = 0 }

    private enum ERole { Multimedia = 1 }

    [StructLayout(LayoutKind.Sequential)]
    private struct SystemPowerStatus
    {
        public byte ACLineStatus;
        public byte BatteryFlag;
        public byte BatteryLifePercent;
        public byte SystemStatusFlag;
        public int BatteryLifeTime;
        public int BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
    private class MMDeviceEnumerator;

    [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDeviceEnumerator
    {
        [PreserveSig]
        int EnumAudioEndpoints(EDataFlow dataFlow, int stateMask, out IntPtr devices);

        [PreserveSig]
        int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice endpoint);
    }

    [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IMMDevice
    {
        void Activate(ref Guid iid, int clsCtx, IntPtr activationParams, [MarshalAs(UnmanagedType.IUnknown)] out object activated);

        void OpenPropertyStore(int access, out IntPtr properties);

        void GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
    }

    [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IAudioEndpointVolume
    {
        void RegisterControlChangeNotify(IntPtr notify);

        void UnregisterControlChangeNotify(IntPtr notify);

        void GetChannelCount(out uint count);

        void SetMasterVolumeLevel(float levelDb, ref Guid context);

        void SetMasterVolumeLevelScalar(float level, ref Guid context);

        void GetMasterVolumeLevel(out float levelDb);

        void GetMasterVolumeLevelScalar(out float level);

        void SetChannelVolumeLevel(uint channel, float levelDb, ref Guid context);

        void SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);

        void GetChannelVolumeLevel(uint channel, out float levelDb);

        void GetChannelVolumeLevelScalar(uint channel, out float level);

        void SetMute([MarshalAs(UnmanagedType.Bool)] bool muted, ref Guid context);

        void GetMute([MarshalAs(UnmanagedType.Bool)] out bool muted);
    }

    [ComImport, Guid("DCB00C01-570F-4A9B-8D69-199FDBA5723B")]
    private class NetworkListManager;

    [ComImport, Guid("DCB00000-570F-4A9B-8D69-199FDBA5723B"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface INetworkListManager
    {
        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworks(int flags);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetwork(Guid networkId);

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnections();

        [return: MarshalAs(UnmanagedType.Interface)]
        object GetNetworkConnection(Guid connectionId);

        bool IsConnectedToInternet { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

        bool IsConnected { [return: MarshalAs(UnmanagedType.VariantBool)] get; }

        int GetConnectivity();
    }
}

/// <summary>
/// The test hook's stand-in: <c>indicators.json</c> in the hook directory holds
/// <c>{ volume: { present, level, muted }, network: "connected"|"limited"|"disconnected",
/// battery: { present, percent, charging } }</c>. Volume changes are written back to it, so no test touches
/// the real devices. A missing or unreadable file reads as a present device at half volume, connected, no battery.
/// </summary>
internal sealed class FakeSource(string hookDirectory) : IIndicatorSource
{
    public const string FileName = "indicators.json";

    private readonly Lock _gate = new();
    private readonly string _path = Path.Combine(hookDirectory, FileName);

    public Readings Read()
    {
        lock (_gate)
        {
            var root = Load();
            var volume = root["volume"] as JsonObject;
            var battery = root["battery"] as JsonObject;
            return new Readings(
                new VolumeReading(Bool(volume?["present"], true), Math.Clamp(Number(volume?["level"], 0.5), 0, 1), Bool(volume?["muted"], false)),
                (root["network"] as JsonValue)?.TryGetValue<string>(out var network) == true
                    ? network switch { "limited" => NetworkReading.Limited, "disconnected" => NetworkReading.Disconnected, _ => NetworkReading.Connected }
                    : NetworkReading.Connected,
                new BatteryReading(Bool(battery?["present"], false), (int)Number(battery?["percent"], 0), Bool(battery?["charging"], false)));
        }
    }

    public void SetVolume(double level) => Update(volume => volume["level"] = Math.Round(Math.Clamp(level, 0, 1), 2));

    public void SetMute(bool muted) => Update(volume => volume["muted"] = muted);

    public void Dispose()
    {
    }

    private void Update(Action<JsonObject> change)
    {
        lock (_gate)
        {
            var root = Load();
            if (root["volume"] is not JsonObject volume)
            {
                volume = new JsonObject { ["present"] = true, ["level"] = 0.5, ["muted"] = false };
                root["volume"] = volume;
            }

            change(volume);
            Retry(() => File.WriteAllText(_path, root.ToJsonString(), new UTF8Encoding(false)));
        }
    }

    private JsonObject Load()
    {
        JsonObject? root = null;
        Retry(() => root = File.Exists(_path) ? JsonNode.Parse(File.ReadAllText(_path)) as JsonObject : null);
        return root ?? [];
    }

    /// <summary>A test may be writing the file at the same moment; a few short retries ride that out.</summary>
    private static void Retry(Action action)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                action();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException && attempt < 5)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static bool Bool(JsonNode? node, bool fallback) =>
        node is JsonValue value && value.TryGetValue<bool>(out var flag) ? flag : fallback;

    private static double Number(JsonNode? node, double fallback) =>
        node is JsonValue value && value.TryGetValue<double>(out var number) ? number : fallback;
}

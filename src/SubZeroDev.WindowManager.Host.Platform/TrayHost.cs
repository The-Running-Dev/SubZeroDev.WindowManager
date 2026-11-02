using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>What the tray's app-bar handling needs from the rest of the host.</summary>
/// <param name="Taskbar">The primary monitor's first host panel: its rect, edge and whether it auto-hides; null when there is none.</param>
public sealed record TrayAppBars(IMonitorService Monitors, WorkAreaAppBarService WorkArea, Func<(RectInt Rect, PanelEdge Edge, bool AutoHide)?> Taskbar);

/// <summary>
/// The notification area and app-bar host for replacement mode. A <c>Shell_TrayWnd</c> window on a thread of its
/// own hears <c>Shell_NotifyIcon</c> and <c>SHAppBarMessage</c>, which shell32 sends as <c>WM_COPYDATA</c>. Icons are
/// kept with a copy of their image; their owners hear clicks in the form their protocol version expects, after
/// being allowed to take the foreground (I18). Icons of a destroyed owner, and its app bars, go within a second.
/// <c>TaskbarCreated</c> is broadcast once the window exists so running apps add their icons again.
/// Under the test hook the window class and the broadcast message carry a hook-scoped suffix (the <c>tray-id</c>
/// file, else the process id), so the user's real Explorer and its tray clients never see this one, and
/// foreground grants are recorded in <c>desktop-actions.log</c>.
/// </summary>
public sealed unsafe class TrayHost : ITrayHost, IDisposable
{
    internal const uint Signature = 0x34753423;
    internal const int NotifyDataSize = 956;
    internal const int MinimumNotifySize = 8 + 152;
    internal const int AppBarDataSize = 64;

    private const string RealClass = "Shell_TrayWnd";
    private const string RealNotifyClass = "TrayNotifyWnd";
    private const string RealCreatedMessage = "TaskbarCreated";
    private const uint WmCopyData = 0x004A;
    private const uint WmTimer = 0x0113;
    private const uint WmContextMenu = 0x007B;
    private const uint WmLButtonDown = 0x0201;
    private const uint WmLButtonUp = 0x0202;
    private const uint WmLButtonDblClk = 0x0203;
    private const uint WmRButtonDown = 0x0204;
    private const uint WmRButtonUp = 0x0205;
    private const uint NinSelect = 0x0400;
    private const uint NinBalloonShow = 0x0402;
    private const uint NinBalloonTimeout = 0x0404;
    private const nuint SweepTimer = 1;
    private const uint SweepMs = 500;

    private static readonly ConcurrentDictionary<nint, TrayHost> Hosts = new();

    private readonly string? _hookDirectory;
    private readonly Action<string> _log;
    private readonly TrayAppBars? _appBars;
    private readonly Lock _gate = new();
    private readonly List<Entry> _icons = [];
    private readonly Dictionary<nint, uint> _bars = [];
    private Thread? _thread;
    private uint _threadId;
    private nint _notifyWindow;
    private bool _disposed;

    private TrayHost(string? hookDirectory, Action<string> log, TrayAppBars? appBars)
    {
        _hookDirectory = hookDirectory;
        _log = log;
        _appBars = appBars;
        var id = hookDirectory is null ? null : HookId(hookDirectory);
        ClassName = id is null ? RealClass : $"SubZeroDev.WindowManager.TrayWnd.{id}";
        CreatedMessageName = id is null ? RealCreatedMessage : $"SubZeroDev.WindowManager.TaskbarCreated.{id}";
    }

    public TrayState State { get; private set; } = TrayState.NotTried;

    /// <summary>The tray window's class: <c>Shell_TrayWnd</c>, or the hook-scoped name under the test hook.</summary>
    public string ClassName { get; }

    /// <summary>The registered message broadcast once the tray exists: <c>TaskbarCreated</c>, or the hook-scoped name.</summary>
    public string CreatedMessageName { get; }

    /// <summary>Whether the created message has been broadcast.</summary>
    public bool Announced { get; private set; }

    public nint Window { get; private set; }

    /// <summary>When the icons last changed, just before <see cref="IconsChanged"/> was raised.</summary>
    public DateTimeOffset? LastChanged { get; private set; }

    public IReadOnlyList<TrayIcon> Icons
    {
        get
        {
            lock (_gate)
            {
                return [.. _icons.Select(entry => entry.ToIcon())];
            }
        }
    }

    public event EventHandler? IconsChanged;

    /// <summary>Creates the tray window and announces it.</summary>
    public static TrayHost Start(string? hookDirectory, Action<string> log, TrayAppBars? appBars)
    {
        var host = new TrayHost(hookDirectory, log, appBars);
        using var ready = new ManualResetEventSlim();
        host._thread = new Thread(() => host.Loop(ready)) { IsBackground = true, Name = "Tray" };
        host._thread.Start();
        ready.Wait();
        return host;
    }

    public IconImage? Image(string key, int sizePx)
    {
        lock (_gate)
        {
            return Find(key) is { Icon: not 0 } entry ? IconService.FromIcon(entry.Icon, sizePx) : null;
        }
    }

    public Task<Outcome> InvokeAsync(string key, TrayAction action, int x, int y, CancellationToken ct)
    {
        Entry? entry;
        lock (_gate)
        {
            entry = Find(key);
        }

        if (entry is null)
        {
            return Task.FromResult(Outcome.Fail(CoreErrorCode.NotFound, $"No notification icon '{key}'."));
        }

        if (!Native.IsWindow(entry.Owner))
        {
            return Task.FromResult(Outcome.Fail(CoreErrorCode.WindowGone, $"The owner of '{key}' no longer exists."));
        }

        // The owner shows its menu or window next; without the right it would only flash on the taskbar.
        Native.AllowSetForegroundWindow(entry.OwnerPid);
        if (_hookDirectory is not null)
        {
            Record(string.Create(CultureInfo.InvariantCulture, $"tray-allow-foreground {entry.OwnerPid}"));
        }

        uint[] events = (action, entry.ProtocolVersion >= 3) switch
        {
            (TrayAction.Select, true) => [WmLButtonDown, WmLButtonUp, NinSelect],
            (TrayAction.Select, false) => [WmLButtonDown, WmLButtonUp],
            (TrayAction.ContextMenu, true) => [WmRButtonDown, WmRButtonUp, WmContextMenu],
            (TrayAction.ContextMenu, false) => [WmRButtonDown, WmRButtonUp],
            _ => [WmLButtonDblClk],
        };
        foreach (var code in events)
        {
            Callback(entry, code, x, y);
        }

        return Task.FromResult(Outcome.Success);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0);
        _thread?.Join(TimeSpan.FromSeconds(2));
        lock (_gate)
        {
            foreach (var entry in _icons)
            {
                entry.SetIcon(0);
            }

            _icons.Clear();
        }
    }

    /// <summary>Where the pointer is, in physical pixels: the anchor for a click that names none.</summary>
    public static (int X, int Y) CursorPosition() => Native.GetCursorPos(out var point) ? (point.X, point.Y) : (0, 0);

    /// <summary>The fields of a <c>Shell_NotifyIcon</c> message, or null with the reason when the payload is not one.</summary>
    internal static NotifyRequest? ParseNotify(ReadOnlySpan<byte> data, out string? error)
    {
        if (data.Length < MinimumNotifySize)
        {
            error = $"{data.Length} bytes is shorter than the smallest notify-icon message.";
            return null;
        }

        if (BitConverter.ToUInt32(data) != Signature)
        {
            error = "The notify-icon signature is wrong.";
            return null;
        }

        var message = BitConverter.ToUInt32(data[4..]);
        if (message > NotifyMessage.SetVersion)
        {
            error = $"Unknown notify-icon message {message}.";
            return null;
        }

        // Older senders pass a shorter structure; what they leave out reads as zero.
        Span<byte> nid = stackalloc byte[NotifyDataSize];
        nid.Clear();
        data[8..Math.Min(data.Length, 8 + NotifyDataSize)].CopyTo(nid);
        error = null;
        return new NotifyRequest(
            message,
            Handle(nid[4..]),
            BitConverter.ToUInt32(nid[8..]),
            BitConverter.ToUInt32(nid[12..]),
            BitConverter.ToUInt32(nid[16..]),
            Handle(nid[20..]),
            Text(nid.Slice(24, 256)),
            BitConverter.ToUInt32(nid[280..]),
            BitConverter.ToUInt32(nid[284..]),
            Text(nid.Slice(288, 512)),
            BitConverter.ToUInt32(nid[800..]),
            Text(nid.Slice(804, 128)),
            new Guid(nid.Slice(936, 16)));
    }

    /// <summary>A balloon's time on screen: the asked seconds within 1 to 30, or 10 when none is asked.</summary>
    internal static TimeSpan BalloonTimeout(uint timeoutMs) =>
        timeoutMs == 0 ? TimeSpan.FromSeconds(10) : TimeSpan.FromMilliseconds(Math.Clamp(timeoutMs, 1000u, 30000u));

    /// <summary>An icon's key: its GUID when it is identified by one, else its owner window and id.</summary>
    internal static string KeyOf(NotifyRequest request) =>
        (request.Flags & NotifyFlags.Guid) != 0 && request.Item != Guid.Empty
            ? request.Item.ToString("N")
            : string.Create(CultureInfo.InvariantCulture, $"{(long)request.Window}:{request.Uid}");

    private static string HookId(string hookDirectory)
    {
        var path = Path.Combine(hookDirectory, "tray-id");
        return File.Exists(path) && File.ReadAllText(path).Trim() is { Length: > 0 } id
            ? id
            : Environment.ProcessId.ToString(CultureInfo.InvariantCulture);
    }

    private static nint Handle(ReadOnlySpan<byte> field) => (nint)BitConverter.ToInt32(field);

    private static string Text(ReadOnlySpan<byte> field)
    {
        var chars = MemoryMarshal.Cast<byte, char>(field);
        var end = chars.IndexOf('\0');
        return new string(end < 0 ? chars : chars[..end]);
    }

    private void Loop(ManualResetEventSlim ready)
    {
        try
        {
            Native.PeekMessage(out _, 0, 0, 0, 0);
            _threadId = Native.GetCurrentThreadId();
            if (_hookDirectory is null && Native.FindWindow(RealClass, null) is var other and not 0)
            {
                Native.GetWindowThreadProcessId(other, out var owner);
                if (owner != Environment.ProcessId)
                {
                    State = TrayState.Taken;
                    _log($"TrayTaken: process {owner} holds {RealClass}; running without the notification area.");
                    return;
                }
            }

            var module = Native.GetModuleHandle(null);
            Register(ClassName, module);
            Window = Native.CreateWindowEx(Native.WsExToolWindow, ClassName, ClassName, Native.WsPopup, 0, 0, 0, 0, 0, 0, module, 0);
            if (Window == 0)
            {
                State = TrayState.Taken;
                _log($"TrayTaken: the tray window could not be created ({Marshal.GetLastPInvokeError()}).");
                return;
            }

            Hosts[Window] = this;
            if (_hookDirectory is null)
            {
                // Older apps look for the notification area's own child to place things next to it.
                Register(RealNotifyClass, module);
                _notifyWindow = Native.CreateWindowEx(0, RealNotifyClass, RealNotifyClass, Native.WsChild, 0, 0, 0, 0, Window, 0, module, 0);
            }

            // Apps at a lower integrity level must still reach the tray.
            Native.ChangeWindowMessageFilterEx(Window, WmCopyData, Native.MsgfltAllow, 0);
            Native.SetTimer(Window, SweepTimer, SweepMs, 0);
            State = TrayState.Held;
        }
        finally
        {
            ready.Set();
        }

        Announce();
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            Native.DispatchMessage(in message);
        }

        Native.KillTimer(Window, SweepTimer);
        Hosts.TryRemove(Window, out _);
        if (_notifyWindow != 0)
        {
            Native.DestroyWindow(_notifyWindow);
        }

        Native.DestroyWindow(Window);
    }

    /// <summary>Tells running apps a taskbar exists, so they add their icons again.</summary>
    private void Announce()
    {
        var message = Native.RegisterWindowMessage(CreatedMessageName);
        Native.SendNotifyMessage(Native.HwndBroadcast, message, 0, 0);
        Announced = true;
    }

    private nint CopyData(nint sender, CopyDataStruct* copy)
    {
        var data = new ReadOnlySpan<byte>((void*)copy->Data, (int)copy->Size);
        return copy->Kind switch
        {
            1 => Notify(data) ? 1 : 0,
            0 => AppBar(data),
            _ => 0,
        };
    }

    private bool Notify(ReadOnlySpan<byte> data)
    {
        if (ParseNotify(data, out var error) is not { } request)
        {
            _log($"TrayPayloadRejected: {error}");
            return false;
        }

        var key = KeyOf(request);
        bool changed;
        lock (_gate)
        {
            var entry = Find(key);
            switch (request.Message)
            {
                case NotifyMessage.Add:
                    if (entry is not null || !Native.IsWindow(request.Window))
                    {
                        return false;
                    }

                    Native.GetWindowThreadProcessId(request.Window, out var pid);
                    entry = new Entry(key, request.Window, request.Uid, (int)pid);
                    _icons.Add(entry);
                    Apply(entry, request);
                    changed = true;
                    break;
                case NotifyMessage.Modify:
                    if (entry is null)
                    {
                        return false;
                    }

                    Apply(entry, request);
                    changed = true;
                    break;
                case NotifyMessage.Delete:
                    if (entry is null)
                    {
                        return false;
                    }

                    _icons.Remove(entry);
                    entry.SetIcon(0);
                    changed = true;
                    break;
                case NotifyMessage.SetVersion:
                    if (entry is null)
                    {
                        return false;
                    }

                    entry.ProtocolVersion = (int)request.TimeoutOrVersion;
                    changed = false;
                    break;
                default:
                    return entry is not null;
            }
        }

        if (changed)
        {
            Changed();
        }

        return true;
    }

    private void Apply(Entry entry, NotifyRequest request)
    {
        if ((request.Flags & NotifyFlags.Message) != 0)
        {
            entry.CallbackMessage = request.CallbackMessage;
        }

        if ((request.Flags & NotifyFlags.Icon) != 0)
        {
            // The sender may destroy its handle as soon as the call returns, so the tray keeps a copy.
            entry.SetIcon(request.Icon == 0 ? 0 : Native.CopyIcon(request.Icon));
            entry.IconVersion++;
        }

        if ((request.Flags & NotifyFlags.Tip) != 0)
        {
            entry.Tooltip = request.Tip;
        }

        if ((request.Flags & NotifyFlags.State) != 0 && (request.StateMask & NotifyFlags.StateHidden) != 0)
        {
            entry.Hidden = (request.State & NotifyFlags.StateHidden) != 0;
        }

        if ((request.Flags & NotifyFlags.Info) != 0)
        {
            if (request.Info.Length == 0)
            {
                entry.Balloon = null;
            }
            else
            {
                var now = DateTimeOffset.UtcNow;
                entry.Balloon = new TrayBalloon(request.InfoTitle, request.Info, now, now + BalloonTimeout(request.TimeoutOrVersion));
                Callback(entry, NinBalloonShow, 0, 0);
            }
        }
    }

    /// <summary>Posts an event to an icon's owner in the form its protocol version expects.</summary>
    private static void Callback(Entry entry, uint code, int x, int y)
    {
        if (entry.CallbackMessage == 0)
        {
            return;
        }

        if (entry.ProtocolVersion >= 4)
        {
            var anchor = (nuint)(uint)((x & 0xFFFF) | ((y & 0xFFFF) << 16));
            Native.PostMessage(entry.Owner, entry.CallbackMessage, anchor, (nint)(int)((code & 0xFFFF) | ((entry.Uid & 0xFFFF) << 16)));
        }
        else
        {
            Native.PostMessage(entry.Owner, entry.CallbackMessage, entry.Uid, (nint)code);
        }
    }

    private nint AppBar(ReadOnlySpan<byte> data)
    {
        if (data.Length < AppBarDataSize)
        {
            _log($"TrayPayloadRejected: {data.Length} bytes is shorter than an app-bar message.");
            return 0;
        }

        var window = Handle(data[4..]);
        var callback = BitConverter.ToUInt32(data[8..]);
        var edge = BitConverter.ToUInt32(data[12..]);
        var rect = new RectInt(BitConverter.ToInt32(data[16..]), BitConverter.ToInt32(data[20..]), BitConverter.ToInt32(data[24..]), BitConverter.ToInt32(data[28..]));
        var message = BitConverter.ToUInt32(data[40..]);
        // A 64-bit field: a shared-memory handle above 0x7FFFFFFF must not be sign-extended as a window handle is.
        var shared = (nint)BitConverter.ToInt64(data[48..]);
        var sourcePid = BitConverter.ToUInt32(data[56..]);
        switch (message)
        {
            case AppBarMessage.New:
                lock (_gate)
                {
                    return _bars.TryAdd(window, callback) ? 1 : 0;
                }

            case AppBarMessage.Remove:
                lock (_gate)
                {
                    _bars.Remove(window);
                }

                _appBars?.WorkArea.RemoveAsync(window, CancellationToken.None).GetAwaiter().GetResult();
                PositionsChanged(window);
                return 1;
            case AppBarMessage.QueryPos or AppBarMessage.SetPos:
                if (_appBars is null || edge > 3 || Monitor(rect) is not { } monitor)
                {
                    return 0;
                }

                var panelEdge = ToPanelEdge(edge);
                var thickness = panelEdge is PanelEdge.Top or PanelEdge.Bottom ? rect.Height : rect.Width;
                RectInt? strip;
                if (message == AppBarMessage.QueryPos)
                {
                    strip = _appBars.WorkArea.Query(window, monitor.StableId, panelEdge, thickness);
                }
                else
                {
                    var set = _appBars.WorkArea.RegisterAsync(window, monitor.StableId, panelEdge, thickness, CancellationToken.None).GetAwaiter().GetResult();
                    strip = set.Error is null ? set.Value : null;
                    if (strip is not null)
                    {
                        PositionsChanged(window);
                    }
                }

                return strip is { } result && WriteBack(shared, sourcePid, edge, result) ? 1 : 0;
            case AppBarMessage.GetState:
                return _appBars?.Taskbar() is { AutoHide: true } ? AppBarState.AutoHide : AppBarState.AlwaysOnTop;
            case AppBarMessage.GetTaskbarPos:
                return _appBars?.Taskbar() is { } taskbar && WriteBack(shared, sourcePid, FromPanelEdge(taskbar.Edge), taskbar.Rect) ? 1 : 0;
            case AppBarMessage.GetAutoHideBar or AppBarMessage.SetAutoHideBar:
                return 0;
            default:
                return 1;
        }
    }

    /// <summary>Writes a rect and edge into the caller's shared copy of its <c>APPBARDATA</c>.</summary>
    private static bool WriteBack(nint shared, uint sourcePid, uint edge, RectInt rect)
    {
        var view = Native.SHLockShared(shared, sourcePid);
        if (view == 0)
        {
            return false;
        }

        try
        {
            var data = (int*)view;
            data[3] = (int)edge;
            data[4] = rect.Left;
            data[5] = rect.Top;
            data[6] = rect.Right;
            data[7] = rect.Bottom;
            return true;
        }
        finally
        {
            Native.SHUnlockShared(view);
        }
    }

    /// <summary>Tells the other app bars that positions changed, as Explorer does.</summary>
    private void PositionsChanged(nint except)
    {
        KeyValuePair<nint, uint>[] bars;
        lock (_gate)
        {
            bars = [.. _bars];
        }

        foreach (var (window, callback) in bars)
        {
            if (window != except && callback != 0)
            {
                Native.PostMessage(window, callback, AppBarNotify.PosChanged, 0);
            }
        }
    }

    /// <summary>The monitor a rect overlaps most, else the primary one.</summary>
    private MonitorInfo? Monitor(RectInt rect)
    {
        var monitors = _appBars!.Monitors.Monitors;
        var best = monitors
            .Select(m => (Monitor: m, Area: Overlap(m.Bounds, rect)))
            .Where(m => m.Area > 0)
            .OrderByDescending(m => m.Area)
            .Select(m => m.Monitor)
            .FirstOrDefault();
        return best ?? monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
    }

    private static long Overlap(RectInt a, RectInt b)
    {
        var width = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var height = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return width > 0 && height > 0 ? (long)width * height : 0;
    }

    private static PanelEdge ToPanelEdge(uint edge) => edge switch
    {
        0 => PanelEdge.Left,
        1 => PanelEdge.Top,
        2 => PanelEdge.Right,
        _ => PanelEdge.Bottom,
    };

    private static uint FromPanelEdge(PanelEdge edge) => edge switch
    {
        PanelEdge.Left => 0,
        PanelEdge.Top => 1,
        PanelEdge.Right => 2,
        _ => 3,
    };

    /// <summary>Drops what destroyed owners left behind and ends balloons whose time is up.</summary>
    private void Sweep()
    {
        var changed = false;
        var now = DateTimeOffset.UtcNow;
        nint[] goneBars;
        lock (_gate)
        {
            foreach (var entry in _icons.ToList())
            {
                if (!Native.IsWindow(entry.Owner))
                {
                    _icons.Remove(entry);
                    entry.SetIcon(0);
                    changed = true;
                }
                else if (entry.Balloon is { } balloon && balloon.Until <= now)
                {
                    entry.Balloon = null;
                    Callback(entry, NinBalloonTimeout, 0, 0);
                    changed = true;
                }
            }

            goneBars = [.. _bars.Keys.Where(window => !Native.IsWindow(window))];
            foreach (var window in goneBars)
            {
                _bars.Remove(window);
            }
        }

        foreach (var window in goneBars)
        {
            _appBars?.WorkArea.RemoveAsync(window, CancellationToken.None).GetAwaiter().GetResult();
        }

        if (changed)
        {
            Changed();
        }
    }

    private void Changed()
    {
        LastChanged = DateTimeOffset.UtcNow;
        IconsChanged?.Invoke(this, EventArgs.Empty);
    }

    private Entry? Find(string key) => _icons.Find(entry => entry.Key == key);

    private void Record(string line)
    {
        Directory.CreateDirectory(_hookDirectory!);
        File.AppendAllText(Path.Combine(_hookDirectory!, Recovery.Reverter.DesktopActionsLog), line + "\n", new UTF8Encoding(false));
    }

    private static void Register(string className, nint module)
    {
        fixed (char* name = className)
        {
            var windowClass = new WindowClassEx
            {
                Size = (uint)sizeof(WindowClassEx),
                WindowProc = &WindowProc,
                Instance = module,
                ClassName = name,
            };

            // Registering a class twice fails harmlessly; the first registration stands.
            Native.RegisterClassEx(&windowClass);
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if ((message == WmCopyData || message == WmTimer) && Hosts.TryGetValue(window, out var host))
        {
            try
            {
                if (message == WmTimer)
                {
                    host.Sweep();
                    return 0;
                }

                return host.CopyData((nint)wParam, (CopyDataStruct*)lParam);
            }
            catch (Exception e)
            {
                // An exception must never cross back into user32.
                host._log($"The tray failed to handle message 0x{message:X}: {e.Message}");
                return 0;
            }
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }

    internal sealed record NotifyRequest(uint Message, nint Window, uint Uid, uint Flags, uint CallbackMessage, nint Icon, string Tip,
        uint State, uint StateMask, string Info, uint TimeoutOrVersion, string InfoTitle, Guid Item);

    internal static class NotifyMessage
    {
        public const uint Add = 0;
        public const uint Modify = 1;
        public const uint Delete = 2;
        public const uint SetFocus = 3;
        public const uint SetVersion = 4;
    }

    internal static class NotifyFlags
    {
        public const uint Message = 0x01;
        public const uint Icon = 0x02;
        public const uint Tip = 0x04;
        public const uint State = 0x08;
        public const uint Info = 0x10;
        public const uint Guid = 0x20;
        public const uint StateHidden = 0x01;
    }

    private static class AppBarMessage
    {
        public const uint New = 0;
        public const uint Remove = 1;
        public const uint QueryPos = 2;
        public const uint SetPos = 3;
        public const uint GetState = 4;
        public const uint GetTaskbarPos = 5;
        public const uint GetAutoHideBar = 7;
        public const uint SetAutoHideBar = 8;
    }

    private static class AppBarState
    {
        public const nint AutoHide = 1;
        public const nint AlwaysOnTop = 2;
    }

    private static class AppBarNotify
    {
        public const nuint PosChanged = 1;
    }

    /// <summary>One icon, with the copy of its image the tray owns.</summary>
    private sealed class Entry(string key, nint owner, uint uid, int ownerPid)
    {
        public string Key { get; } = key;

        public nint Owner { get; } = owner;

        public uint Uid { get; } = uid;

        public int OwnerPid { get; } = ownerPid;

        public uint CallbackMessage { get; set; }

        public string Tooltip { get; set; } = "";

        public bool Hidden { get; set; }

        public nint Icon { get; private set; }

        public int IconVersion { get; set; }

        public int ProtocolVersion { get; set; }

        public TrayBalloon? Balloon { get; set; }

        public void SetIcon(nint icon)
        {
            if (Icon != 0)
            {
                Native.DestroyIcon(Icon);
            }

            Icon = icon;
        }

        public TrayIcon ToIcon() => new(Key, Owner, Uid, OwnerPid, Tooltip, Hidden, Balloon, IconVersion, ProtocolVersion);
    }
}

[StructLayout(LayoutKind.Sequential)]
internal struct CopyDataStruct
{
    public nuint Kind;
    public uint Size;
    public nint Data;
}

[StructLayout(LayoutKind.Sequential)]
internal struct Point
{
    public int X;
    public int Y;
}

internal static unsafe partial class Native
{
    public const nint HwndBroadcast = 0xFFFF;
    public const uint WsChild = 0x40000000;
    public const uint MsgfltAllow = 1;

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindWindow(string? className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "SendNotifyMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SendNotifyMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    public static partial nuint SetTimer(nint window, nuint id, uint elapseMs, nint timerProc);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool KillTimer(nint window, nuint id);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ChangeWindowMessageFilterEx(nint window, uint message, uint action, nint changeFilterStruct);

    [LibraryImport("user32.dll")]
    public static partial nint CopyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetCursorPos(out Point point);

    [LibraryImport("shlwapi.dll", EntryPoint = "#8")]
    public static partial nint SHLockShared(nint shared, uint processId);

    [LibraryImport("shlwapi.dll", EntryPoint = "#9")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SHUnlockShared(nint view);
}

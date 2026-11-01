using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using static SubZeroDev.WindowManager.Plugins.RunningWindows.Native;

namespace SubZeroDev.WindowManager.Plugins.RunningWindows;

/// <summary>One open application window as the feed carries it.</summary>
public sealed record RunningWindow(long Hwnd, string Title, string? Exe, string? MonitorId, bool Active, bool Minimized)
{
    public JsonObject ToJson() => new()
    {
        ["key"] = Hwnd.ToString(System.Globalization.CultureInfo.InvariantCulture),
        ["hwnd"] = Hwnd,
        ["title"] = Title,
        ["exe"] = Exe,
        ["aumid"] = null,
        ["monitorId"] = MonitorId,
        ["active"] = Active,
        ["minimized"] = Minimized,
    };
}

/// <summary>
/// Keeps the list of windows a taskbar shows: visible, not cloaked, titled, and either unowned and not a
/// tool or no-activate window, or marked as an app window. WinEvent hooks on a thread of its own mark the
/// list dirty; a rescan follows within 30 ms, and every 2 s regardless. Windows keep the order they first
/// appeared in. The active window is the foreground one; while a tool window (a panel) has the foreground,
/// the last active window stays active, so a click on the panel can tell which window was in front.
/// </summary>
public sealed unsafe class WindowTracker : IDisposable
{
    private const uint DebounceMs = 30;
    private const uint FallbackMs = 2000;
    private static WindowTracker? _current;

    private readonly Thread _thread;
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly List<nint> _order = [];
    private readonly Dictionary<uint, string?> _images = [];
    private readonly List<nint> _scan = [];
    private readonly Lock _gate = new();
    private IReadOnlyList<RunningWindow> _windows = [];
    private uint _threadId;
    private nuint _debounce;
    private nint _active;

    public WindowTracker()
    {
        _thread = new Thread(Run) { IsBackground = true, Name = "Window tracker" };
    }

    /// <summary>Raised on the tracker thread with the new list whenever it changes.</summary>
    public event EventHandler<IReadOnlyList<RunningWindow>>? Changed;

    public IReadOnlyList<RunningWindow> Windows
    {
        get
        {
            lock (_gate)
            {
                return _windows;
            }
        }
    }

    /// <summary>Starts the tracker thread and returns once the first scan is done.</summary>
    public Task StartAsync()
    {
        if (Interlocked.CompareExchange(ref _current, this, null) is not null)
        {
            throw new InvalidOperationException("Only one window tracker runs per process.");
        }

        _thread.Start();
        return _started.Task;
    }

    public void Dispose()
    {
        if (_threadId != 0)
        {
            PostThreadMessageW(_threadId, WM_QUIT, 0, 0);
            _thread.Join(TimeSpan.FromSeconds(2));
        }

        Interlocked.CompareExchange(ref _current, null, this);
    }

    private void Run()
    {
        _threadId = GetCurrentThreadId();
        delegate* unmanaged[Stdcall]<nint, uint, nint, int, int, uint, uint, void> callback = &OnWinEvent;
        (uint Min, uint Max)[] ranges =
        [
            (EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND),
            (EVENT_SYSTEM_MOVESIZEEND, EVENT_SYSTEM_MOVESIZEEND),
            (EVENT_SYSTEM_MINIMIZESTART, EVENT_SYSTEM_MINIMIZEEND),
            (EVENT_OBJECT_CREATE, EVENT_OBJECT_HIDE),
            (EVENT_OBJECT_NAMECHANGE, EVENT_OBJECT_NAMECHANGE),
            (EVENT_OBJECT_CLOAKED, EVENT_OBJECT_UNCLOAKED),
        ];
        var hooks = ranges.Select(range => SetWinEventHook(range.Min, range.Max, 0, callback, 0, 0, WINEVENT_OUTOFCONTEXT | WINEVENT_SKIPOWNPROCESS)).ToArray();
        var fallback = SetTimer(0, 0, FallbackMs, 0);
        try
        {
            Rescan();
            _started.TrySetResult();
            while (GetMessageW(out var msg, 0, 0, 0) > 0)
            {
                if (msg.Message != WM_TIMER)
                {
                    continue;
                }

                if (msg.WParam == _debounce)
                {
                    KillTimer(0, _debounce);
                    _debounce = 0;
                }

                Rescan();
            }
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            _started.TrySetException(exception);
            throw;
        }
        finally
        {
            KillTimer(0, fallback);
            foreach (var hook in hooks.Where(hook => hook != 0))
            {
                UnhookWinEvent(hook);
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(nint hook, uint kind, nint hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // Object events fire for every accessible object; only whole windows matter here.
        if (kind >= EVENT_OBJECT_CREATE && (idObject != 0 || idChild != 0))
        {
            return;
        }

        if (_current is { } tracker && tracker._debounce == 0)
        {
            tracker._debounce = SetTimer(0, 0, DebounceMs, 0);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int OnWindow(nint hwnd, nint lParam)
    {
        _current?._scan.Add(hwnd);
        return 1;
    }

    private void Rescan()
    {
        _scan.Clear();
        EnumWindows(&OnWindow, 0);
        var shell = GetShellWindow();
        var listed = new HashSet<nint>(_scan.Where(hwnd => hwnd != shell && IsListed(hwnd)));

        _order.RemoveAll(hwnd => !listed.Contains(hwnd));
        var known = new HashSet<nint>(_order);
        // EnumWindows walks top to bottom; append new windows bottom-most first so the newest is last.
        foreach (var hwnd in Enumerable.Reverse(_scan).Where(hwnd => listed.Contains(hwnd) && !known.Contains(hwnd)))
        {
            _order.Add(hwnd);
        }

        var foreground = GetForegroundWindow();
        if (listed.Contains(foreground))
        {
            _active = foreground;
        }
        else if ((foreground != 0 && (ExStyle(foreground) & WS_EX_TOOLWINDOW) == 0) || !listed.Contains(_active))
        {
            // No foreground is a moment between two activations; a tool window is a panel being clicked.
            _active = 0;
        }

        var windows = _order.Select(hwnd =>
        {
            GetWindowThreadProcessId(hwnd, out var processId);
            if (!_images.TryGetValue(processId, out var exe))
            {
                exe = _images[processId] = ProcessImage(processId);
            }

            return new RunningWindow(hwnd, Title(hwnd), exe, MonitorId(hwnd), hwnd == _active, IsIconic(hwnd));
        }).ToList();

        // Process ids are reused; forget images of processes that no longer own a listed window.
        if (_images.Count > 256)
        {
            _images.Clear();
        }

        bool changed;
        lock (_gate)
        {
            changed = !windows.SequenceEqual(_windows);
            if (changed)
            {
                _windows = windows;
            }
        }

        if (changed)
        {
            Changed?.Invoke(this, windows);
        }
    }

    private static bool IsListed(nint hwnd)
    {
        if (!IsWindowVisible(hwnd) || IsCloaked(hwnd) || GetWindowTextLengthW(hwnd) <= 0)
        {
            return false;
        }

        var exStyle = ExStyle(hwnd);
        if ((exStyle & WS_EX_APPWINDOW) != 0)
        {
            return true;
        }

        return GetWindow(hwnd, GW_OWNER) == 0 && (exStyle & (WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE)) == 0;
    }
}

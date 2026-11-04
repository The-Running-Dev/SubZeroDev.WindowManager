using System.Runtime.InteropServices;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Finds top-level windows and reports what changes: WinEvent hooks, the shell hook and a two-second rescan, all on a
/// thread of its own. It reports; <see cref="WindowModel"/> decides what a report means (I39, I40).
/// </summary>
public sealed unsafe class Win32WindowTracker(Win32WindowFacts facts) : IWindowTracker
{
    private const uint RescanMilliseconds = 2000;
    private const nuint RescanTimerId = 1;

    [ThreadStatic]
    private static Win32WindowTracker? _current;

    private Thread? _thread;
    private uint _threadId;
    private Action<WindowObservation>? _sink;
    private readonly HashSet<nint> _known = [];

    public Task<IReadOnlyList<nint>> EnumerateAsync(CancellationToken ct) =>
        Task.Run<IReadOnlyList<nint>>(() => EnumerateTopLevel(), ct);

    public WindowFingerprint? Capture(nint handle) => facts.Fingerprint(handle);

    public void Start(Action<WindowObservation> sink)
    {
        if (_thread is not null)
        {
            return;
        }

        _sink = sink;
        using var ready = new ManualResetEventSlim();
        _thread = new Thread(() => Loop(ready)) { IsBackground = true, Name = "WindowTracker" };
        _thread.Start();
        ready.Wait();
    }

    public void Stop()
    {
        if (_thread is null)
        {
            return;
        }

        Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _thread = null;
    }

    private static List<nint> EnumerateTopLevel()
    {
        var handles = new List<nint>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            Native.EnumWindows(&EnumCallback, GCHandle.ToIntPtr(gc));
        }
        finally
        {
            gc.Free();
        }

        return handles;
    }

    [UnmanagedCallersOnly]
    private static int EnumCallback(nint window, nint data)
    {
        try
        {
            ((List<nint>)GCHandle.FromIntPtr(data).Target!).Add(window);
        }
        catch (Exception)
        {
            // An exception must never cross back into user32.
        }

        return 1;
    }

    private void Loop(ManualResetEventSlim ready)
    {
        Native.PeekMessage(out _, 0, 0, 0, 0);
        _threadId = Native.GetCurrentThreadId();
        _current = this;
        var module = Native.GetModuleHandle(null);
        var shellHook = Native.CreateWindowEx(Native.WsExToolWindow, "Static", string.Empty, Native.WsPopup, 0, 0, 0, 0, 0, 0, module, 0);
        var shellHookMessage = Native.RegisterWindowMessage("SHELLHOOK");
        if (shellHook != 0)
        {
            Native.RegisterShellHookWindow(shellHook);
        }

        var hooks = new List<nint>();
        foreach (var (min, max) in new (uint, uint)[]
        {
            (Native.EventSystemForeground, Native.EventSystemForeground),
            (Native.EventSystemMoveSizeEnd, Native.EventSystemMoveSizeEnd),
            (Native.EventSystemMinimizeStart, Native.EventSystemMinimizeEnd),
            (Native.EventObjectCreate, Native.EventObjectHide),
            (Native.EventObjectNameChange, Native.EventObjectNameChange),
            (Native.EventObjectCloaked, Native.EventObjectUncloaked),
        })
        {
            var hook = Native.SetWinEventHook(min, max, 0, &HookCallback, 0, 0, Native.WinEventOutOfContext);
            if (hook != 0)
            {
                hooks.Add(hook);
            }
        }

        foreach (var handle in EnumerateTopLevel())
        {
            _known.Add(handle);
        }

        Native.SetTimer(0, RescanTimerId, RescanMilliseconds, 0);
        ready.Set();
        try
        {
            while (Native.GetMessage(out var message, 0, 0, 0) > 0)
            {
                if (shellHook != 0 && message.Window == shellHook && message.Message == shellHookMessage)
                {
                    OnShellHook((int)((uint)message.WParam & 0x7FFFFFFF), message.LParam);
                }
                else if (message.Window == 0 && message.Message == Native.WmTimer && message.WParam == RescanTimerId)
                {
                    Rescan();
                }
                else
                {
                    Native.DispatchMessage(in message);
                }
            }
        }
        finally
        {
            Native.KillTimer(0, RescanTimerId);
            foreach (var hook in hooks)
            {
                Native.UnhookWinEvent(hook);
            }

            if (shellHook != 0)
            {
                Native.DeregisterShellHookWindow(shellHook);
                Native.DestroyWindow(shellHook);
            }

            _current = null;
        }
    }

    [UnmanagedCallersOnly]
    private static void HookCallback(nint hook, uint kind, nint window, int objectId, int childId, uint thread, uint time)
    {
        try
        {
            if (window == 0 || (kind >= 0x8000 && (objectId != 0 || childId != 0)))
            {
                return;
            }

            _current?.OnWinEvent(kind, window);
        }
        catch (Exception)
        {
            // An exception must never cross back into user32.
        }
    }

    private void OnWinEvent(uint kind, nint window)
    {
        switch (kind)
        {
            case Native.EventSystemForeground:
                Emit(window, ObservationKind.Foreground);
                break;
            case Native.EventSystemMoveSizeEnd:
                Emit(window, ObservationKind.Shown);
                break;
            case Native.EventSystemMinimizeStart:
                Emit(window, ObservationKind.Minimised);
                break;
            case Native.EventSystemMinimizeEnd:
                Emit(window, ObservationKind.Restored);
                break;
            case Native.EventObjectCreate:
                _known.Add(window);
                Emit(window, ObservationKind.Created);
                break;
            case Native.EventObjectDestroy:
                _known.Remove(window);
                Emit(window, ObservationKind.Destroyed);
                break;
            case Native.EventObjectShow:
                Emit(window, ObservationKind.Shown);
                break;
            case Native.EventObjectHide:
                Emit(window, ObservationKind.Hidden);
                break;
            case Native.EventObjectNameChange:
                Emit(window, ObservationKind.TitleChanged);
                break;
            case Native.EventObjectCloaked:
                Emit(window, ObservationKind.Cloaked);
                break;
            case Native.EventObjectUncloaked:
                Emit(window, ObservationKind.Shown);
                break;
        }
    }

    private void OnShellHook(int code, nint window)
    {
        if (window == 0)
        {
            return;
        }

        switch ((uint)code)
        {
            case Native.HshellWindowCreated:
                _known.Add(window);
                Emit(window, ObservationKind.Created);
                break;
            case Native.HshellWindowDestroyed:
                _known.Remove(window);
                Emit(window, ObservationKind.Destroyed);
                break;
            case Native.HshellWindowActivated:
            case Native.HshellRudeAppActivated:
                Emit(window, ObservationKind.Foreground);
                break;
            case Native.HshellFlash:
                Emit(window, ObservationKind.Flashed);
                break;
            case Native.HshellRedraw:
                Emit(window, ObservationKind.Redrawn);
                break;
        }
    }

    /// <summary>The safety net under the event hooks: reports windows nobody announced, and windows that went without a word.</summary>
    private void Rescan()
    {
        var now = EnumerateTopLevel();
        var present = new HashSet<nint>(now);
        for (var i = now.Count - 1; i >= 0; i--)
        {
            if (_known.Add(now[i]))
            {
                Emit(now[i], ObservationKind.Created);
            }
        }

        foreach (var handle in _known.Where(h => !present.Contains(h)).ToList())
        {
            _known.Remove(handle);
            Emit(handle, ObservationKind.RescanMissing);
        }

        Emit(0, ObservationKind.Rescan);
    }

    private void Emit(nint window, ObservationKind kind)
    {
        try
        {
            _sink?.Invoke(new WindowObservation(window, kind));
        }
        catch (Exception)
        {
            // A faulty sink must not stop the tracker.
        }
    }
}

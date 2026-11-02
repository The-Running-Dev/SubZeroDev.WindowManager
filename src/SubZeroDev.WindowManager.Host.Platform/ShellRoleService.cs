using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Takes the roles Explorer plays as the session's shell: a desktop window registered with
/// <c>SetShellWindow</c>, and a shell hook window (<c>RegisterShellHookWindow</c>, and the task-manager window) that
/// hears windows come, go, activate and flash. Both live on a thread of their own. If another process already holds the shell window
/// the roles are left alone, <c>ShellRoleTaken</c> is logged and the host runs without them.
/// Under the test hook the desktop window is never registered: a <c>shell-owner</c> file in the hook folder
/// stands for another shell, and the desktop-ready signal and minimised-window setting are recorded in
/// <c>desktop-actions.log</c> instead of being made.
/// </summary>
public sealed unsafe class ShellRoleService : IShellRoleService, IDisposable
{
    private const string DesktopClass = "SubZeroDev.WindowManager.Desktop";
    private const string HookClass = "SubZeroDev.WindowManager.ShellHook";
    private const int MaxEvents = 64;

    private static readonly ConcurrentDictionary<nint, ShellRoleService> Hooks = new();
    private static readonly uint ShellHookMessage = Native.RegisterWindowMessage("SHELLHOOK");
    private static int _registered;

    private readonly string? _hookDirectory;
    private readonly Action<string> _log;
    private readonly nint _desktop;
    private readonly Lock _gate = new();
    private readonly List<ShellHookEvent> _events = [];
    private Thread? _thread;
    private uint _threadId;
    private int _signalled;
    private bool _disposed;

    private ShellRoleService(string? hookDirectory, Action<string> log, nint desktop)
    {
        _hookDirectory = hookDirectory;
        _log = log;
        _desktop = desktop;
    }

    public ShellRoleState State { get; private set; } = ShellRoleState.NotTried;

    public nint DesktopWindow { get; private set; }

    public nint HookWindow { get; private set; }

    public IReadOnlyList<ShellHookEvent> Events
    {
        get
        {
            lock (_gate)
            {
                return [.. _events];
            }
        }
    }

    /// <summary>A hook message arrived; raised on the roles' thread.</summary>
    public event EventHandler<ShellHookEvent>? HookMessage;

    /// <summary>Takes the roles for the session.</summary>
    public static ShellRoleService Start(string? hookDirectory, Action<string> log) => Start(hookDirectory, log, 0);

    /// <param name="desktop">A desktop for the roles' thread (<c>SetThreadDesktop</c>), so a test can take them on a desktop of its own; 0 for the process's.</param>
    internal static ShellRoleService Start(string? hookDirectory, Action<string> log, nint desktop)
    {
        var service = new ShellRoleService(hookDirectory, log, desktop);
        using var ready = new ManualResetEventSlim();
        service._thread = new Thread(() => service.Loop(ready)) { IsBackground = true, Name = "ShellRoles" };
        service._thread.Start();
        ready.Wait();
        return service;
    }

    public bool SignalDesktopReady()
    {
        if (State != ShellRoleState.Held || Interlocked.Exchange(ref _signalled, 1) != 0)
        {
            return false;
        }

        if (_hookDirectory is not null)
        {
            Record("desktop-ready");
            return true;
        }

        // Winlogon waits on this event before it takes down the welcome screen.
        var handle = Native.OpenEvent(Native.EventModifyState, false, "ShellDesktopSwitchEvent");
        if (handle != 0)
        {
            Native.SetEvent(handle);
            Native.CloseHandle(handle);
        }

        Native.PostMessage(DesktopWindow, Native.WmUser, 0, 0);
        return true;
    }

    public bool HideMinimizedWindows()
    {
        if (State != ShellRoleState.Held)
        {
            return false;
        }

        if (_hookDirectory is not null)
        {
            Record("minimized-hide");
            return true;
        }

        var metrics = new MinimizedMetrics { Size = (uint)sizeof(MinimizedMetrics) };
        if (!Native.SystemParametersInfo(Native.SpiGetMinimizedMetrics, metrics.Size, &metrics, 0))
        {
            _log($"SPI_GETMINIMIZEDMETRICS failed with {Marshal.GetLastPInvokeError()}.");
            return false;
        }

        // Only for this session: the setting is not written to the user's profile.
        metrics.Arrange |= Native.ArwHide;
        if (!Native.SystemParametersInfo(Native.SpiSetMinimizedMetrics, metrics.Size, &metrics, Native.SpifSendChange))
        {
            _log($"SPI_SETMINIMIZEDMETRICS failed with {Marshal.GetLastPInvokeError()}.");
            return false;
        }

        return true;
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
    }

    private void Loop(ManualResetEventSlim ready)
    {
        try
        {
            if (_desktop != 0 && !Native.SetThreadDesktop(_desktop))
            {
                _log($"SetThreadDesktop failed with {Marshal.GetLastPInvokeError()}.");
                return;
            }

            Native.PeekMessage(out _, 0, 0, 0, 0);
            _threadId = Native.GetCurrentThreadId();
            if (OtherShell() is { } owner)
            {
                State = ShellRoleState.Taken;
                _log($"ShellRoleTaken: process {owner} holds the shell window; running without the shell roles.");
                return;
            }

            Register();
            var module = Native.GetModuleHandle(null);
            DesktopWindow = Native.CreateWindowEx(Native.WsExToolWindow, DesktopClass, DesktopClass, Native.WsPopup, 0, 0, 0, 0, 0, 0, module, 0);
            HookWindow = Native.CreateWindowEx(Native.WsExToolWindow, HookClass, HookClass, Native.WsPopup, 0, 0, 0, 0, 0, 0, module, 0);
            if (DesktopWindow == 0 || HookWindow == 0)
            {
                State = ShellRoleState.Taken;
                _log($"ShellRoleTaken: the shell windows could not be created ({Marshal.GetLastPInvokeError()}).");
                return;
            }

            if (_hookDirectory is null && !Native.SetShellWindow(DesktopWindow))
            {
                State = ShellRoleState.Taken;
                _log($"ShellRoleTaken: SetShellWindow failed ({Marshal.GetLastPInvokeError()}); running without the shell roles.");
                return;
            }

            Hooks[HookWindow] = this;
            if (!Native.RegisterShellHookWindow(HookWindow))
            {
                _log($"RegisterShellHookWindow failed with {Marshal.GetLastPInvokeError()}.");
            }

            // As the task-manager window the hook hears flashes and rude activations with their high bit set.
            if (_hookDirectory is null && !Native.SetTaskmanWindow(HookWindow))
            {
                _log($"SetTaskmanWindow failed with {Marshal.GetLastPInvokeError()}.");
            }

            State = ShellRoleState.Held;
        }
        finally
        {
            ready.Set();
        }

        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            Native.DispatchMessage(in message);
        }

        Native.DeregisterShellHookWindow(HookWindow);
        Hooks.TryRemove(HookWindow, out _);
        Native.DestroyWindow(HookWindow);
        Native.DestroyWindow(DesktopWindow);
    }

    /// <summary>The process holding the shell window when it is not this one, else null.</summary>
    private int? OtherShell()
    {
        if (_hookDirectory is not null)
        {
            var path = Path.Combine(_hookDirectory, "shell-owner");
            return File.Exists(path) && int.TryParse(File.ReadAllText(path).Trim(), out var pid) ? pid : null;
        }

        var shell = Native.GetShellWindow();
        if (shell == 0)
        {
            return null;
        }

        Native.GetWindowThreadProcessId(shell, out var processId);
        return processId == Environment.ProcessId ? null : (int)processId;
    }

    private void Record(string line)
    {
        Directory.CreateDirectory(_hookDirectory!);
        File.AppendAllText(Path.Combine(_hookDirectory!, Recovery.Reverter.DesktopActionsLog), line + "\n", new UTF8Encoding(false));
    }

    private void Received(int code, nint window)
    {
        var hookEvent = new ShellHookEvent(code, window, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        lock (_gate)
        {
            if (_events.Count == MaxEvents)
            {
                _events.RemoveAt(0);
            }

            _events.Add(hookEvent);
        }

        HookMessage?.Invoke(this, hookEvent);
    }

    private static void Register()
    {
        if (Interlocked.Exchange(ref _registered, 1) != 0)
        {
            return;
        }

        var module = Native.GetModuleHandle(null);
        foreach (var className in new[] { DesktopClass, HookClass })
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
                Native.RegisterClassEx(&windowClass);
            }
        }
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (message == ShellHookMessage && Hooks.TryGetValue(window, out var service))
        {
            try
            {
                service.Received((int)(wParam & 0xFFFF), lParam);
            }
            catch (Exception)
            {
                // An exception must never cross back into user32.
            }

            return 0;
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }
}

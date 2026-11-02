using System.Collections.Concurrent;
using System.Runtime.InteropServices;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// A hidden top-level window on a thread of its own that hears what the shell and the display driver
/// broadcast: display and setting changes, and the <c>TaskbarCreated</c> message a (re)started shell sends.
/// It is top-level rather than message-only because broadcasts only reach top-level windows. Events are
/// raised on its thread.
/// </summary>
public sealed unsafe class ShellEvents : IDisposable
{
    private const string ClassName = "SubZeroDev.WindowManager.ShellEvents";

    private static readonly ConcurrentDictionary<nint, ShellEvents> Windows = new();
    private static readonly uint TaskbarCreatedMessage = Native.RegisterWindowMessage("TaskbarCreated");
    private static int _registered;

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private bool _disposed;

    public ShellEvents()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "ShellEvents" };
        _thread.Start();
        _ready.Wait();
    }

    /// <summary>The hidden window; tests post <c>TaskbarCreated</c> to it to simulate a shell restart.</summary>
    public nint Handle { get; private set; }

    /// <summary>The display configuration or a system setting changed (<c>WM_DISPLAYCHANGE</c>, <c>WM_SETTINGCHANGE</c>).</summary>
    public event EventHandler? DisplayChanged;

    /// <summary>The shell (re)created its taskbar, so every app bar it knew is gone.</summary>
    public event EventHandler? TaskbarCreated;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    private void Loop()
    {
        Native.PeekMessage(out _, 0, 0, 0, 0);
        _threadId = Native.GetCurrentThreadId();
        var module = Native.GetModuleHandle(null);
        if (Interlocked.Exchange(ref _registered, 1) == 0)
        {
            fixed (char* name = ClassName)
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

        Handle = Native.CreateWindowEx(0, ClassName, ClassName, Native.WsPopup, 0, 0, 0, 0, 0, 0, module, 0);
        if (Handle != 0)
        {
            Windows[Handle] = this;
        }

        _ready.Set();
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            Native.DispatchMessage(in message);
        }

        Windows.TryRemove(Handle, out _);
        Native.DestroyWindow(Handle);
    }

    [UnmanagedCallersOnly]
    private static nint WindowProc(nint window, uint message, nuint wParam, nint lParam)
    {
        if (Windows.TryGetValue(window, out var events))
        {
            try
            {
                if (message is Native.WmDisplayChange or Native.WmSettingChange)
                {
                    events.DisplayChanged?.Invoke(events, EventArgs.Empty);
                }
                else if (message == TaskbarCreatedMessage)
                {
                    events.TaskbarCreated?.Invoke(events, EventArgs.Empty);
                }
            }
            catch (Exception)
            {
                // An exception must never cross back into user32.
            }
        }

        return Native.DefWindowProc(window, message, wParam, lParam);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// The Windows key when the app is the shell: a low-level keyboard hook (<c>WH_KEYBOARD_LL</c>) on a thread
/// of its own, so a busy UI thread never holds a key back. <see cref="WindowsKeyTracker"/> decides each event;
/// a completed chord is raised on the thread pool. Under the test hook it acts only on events a test injected
/// with <see cref="TestMarker"/> and swallows every one of them, so nothing reaches Windows or another program,
/// and every other event passes untouched.
/// </summary>
public sealed unsafe class WindowsKeyHook : IWindowsKeyHook
{
    /// <summary>The <c>dwExtraInfo</c> a test puts on the keys it injects ("SZWK").</summary>
    public const nuint TestMarker = 0x535A574B;

    /// <summary>The <c>dwExtraInfo</c> on the keys the hook sends itself, which it lets through unseen ("SZMK").</summary>
    private const nuint MaskMarker = 0x535A4D4B;

    /// <summary>An unassigned virtual key: Windows sees it before the Windows key goes up, and opens no Start menu.</summary>
    private const ushort MaskKey = 0xE8;

    private static WindowsKeyHook? s_current;

    private readonly bool _testOnly;
    private readonly WindowsKeyTracker _tracker;
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private nint _hook;
    private int _error;
    private long _events;
    private long _slowestTicks;
    private bool _disposed;

    private WindowsKeyHook(bool testOnly, IReadOnlySet<uint> boundWithWin)
    {
        _testOnly = testOnly;
        _tracker = new WindowsKeyTracker(boundWithWin);
        _thread = new Thread(Loop) { IsBackground = true, Name = "WindowsKeyHook", Priority = ThreadPriority.AboveNormal };
    }

    public event EventHandler<KeyChord>? Pressed;

    public bool Installed => _hook != 0;

    /// <summary>Key events the tracker has seen.</summary>
    public long Events => Interlocked.Read(ref _events);

    /// <summary>The longest the hook took to answer one event.</summary>
    public TimeSpan SlowestAnswer => TimeSpan.FromTicks(Interlocked.Read(ref _slowestTicks));

    /// <summary>Installs the hook on its own thread; <paramref name="testOnly"/> under the test hook.</summary>
    public static WindowsKeyHook Start(bool testOnly, IReadOnlySet<uint> boundWithWin, Action<string> warn)
    {
        var hook = new WindowsKeyHook(testOnly, boundWithWin);
        s_current = hook;
        hook._thread.Start();
        hook._ready.Wait(TimeSpan.FromSeconds(5));
        if (!hook.Installed)
        {
            warn($"The Windows-key hook could not be installed (error {hook._error}).");
        }

        return hook;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Native.PostThreadMessage(_threadId, Native.WmQuit, 0, 0);
        _thread.Join(TimeSpan.FromSeconds(2));
        Interlocked.CompareExchange(ref s_current, null, this);
    }

    private void Loop()
    {
        Native.PeekMessage(out _, 0, 0, 0, 0);
        _threadId = Native.GetCurrentThreadId();
        _hook = Native.SetWindowsHookEx(Native.WhKeyboardLl, &Callback, Native.GetModuleHandle(null), 0);
        _error = _hook == 0 ? Marshal.GetLastPInvokeError() : 0;
        _ready.Set();
        if (_hook == 0)
        {
            return;
        }

        // Windows calls a low-level hook through this thread's message loop.
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            Native.DispatchMessage(in message);
        }

        Native.UnhookWindowsHookEx(_hook);
        _hook = 0;
    }

    [UnmanagedCallersOnly]
    private static nint Callback(int code, nuint wParam, nint lParam)
    {
        var hook = s_current;
        return code < 0 || hook is null
            ? Native.CallNextHookEx(0, code, wParam, lParam)
            : hook.OnEvent(code, wParam, (KeyboardLowLevel*)lParam);
    }

    private nint OnEvent(int code, nuint wParam, KeyboardLowLevel* key)
    {
        var marked = key->ExtraInfo == TestMarker;
        if (key->ExtraInfo == MaskMarker || (_testOnly && !marked))
        {
            return Native.CallNextHookEx(0, code, wParam, (nint)key);
        }

        var started = Stopwatch.GetTimestamp();
        var down = wParam is Native.WmKeyDown or Native.WmSysKeyDown;
        var verdict = _tracker.OnKey(key->VirtualKey, down);
        if (verdict.Chord is { } chord)
        {
            ThreadPool.UnsafeQueueUserWorkItem(_ => Pressed?.Invoke(this, chord), null);
        }

        var swallow = marked || verdict.Swallow;
        if (verdict.Mask && !_testOnly)
        {
            // This Windows key up is held back and sent again after a key of no meaning, so Windows sees the
            // Windows key used in a chord and opens no Start menu.
            SendMaskedUp((ushort)key->VirtualKey);
            swallow = true;
        }

        Interlocked.Increment(ref _events);
        var took = Stopwatch.GetElapsedTime(started).Ticks;
        for (var slowest = Interlocked.Read(ref _slowestTicks); took > slowest; slowest = Interlocked.Read(ref _slowestTicks))
        {
            if (Interlocked.CompareExchange(ref _slowestTicks, took, slowest) == slowest)
            {
                break;
            }
        }

        return swallow ? 1 : Native.CallNextHookEx(0, code, wParam, (nint)key);
    }

    private static void SendMaskedUp(ushort windowsKey)
    {
        var inputs = stackalloc KeyboardInput[3];
        inputs[0] = new KeyboardInput { Type = Native.InputKeyboard, VirtualKey = MaskKey, ExtraInfo = MaskMarker };
        inputs[1] = new KeyboardInput { Type = Native.InputKeyboard, VirtualKey = MaskKey, Flags = Native.KeyEventFKeyUp, ExtraInfo = MaskMarker };
        inputs[2] = new KeyboardInput { Type = Native.InputKeyboard, VirtualKey = windowsKey, Flags = Native.KeyEventFKeyUp | Native.KeyEventFExtendedKey, ExtraInfo = MaskMarker };
        Native.SendInput(3, inputs, sizeof(KeyboardInput));
    }
}

/// <summary>A <c>KBDLLHOOKSTRUCT</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardLowLevel
{
    public uint VirtualKey;
    public uint ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
}

internal static unsafe partial class Native
{
    public const int WhKeyboardLl = 13;
    public const nuint WmKeyDown = 0x0100;
    public const nuint WmSysKeyDown = 0x0104;
    public const uint KeyEventFExtendedKey = 1;

    [LibraryImport("user32.dll", EntryPoint = "SetWindowsHookExW", SetLastError = true)]
    public static partial nint SetWindowsHookEx(int kind, delegate* unmanaged<int, nuint, nint, nint> callback, nint module, uint threadId);

    [LibraryImport("user32.dll")]
    public static partial nint CallNextHookEx(nint hook, int code, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWindowsHookEx(nint hook);
}

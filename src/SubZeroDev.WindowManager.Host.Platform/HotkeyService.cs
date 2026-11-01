using System.Collections.Concurrent;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Global hotkeys through <c>RegisterHotKey</c> on a thread of its own: the thread that registers a
/// hotkey is the one <c>WM_HOTKEY</c> is posted to, so registering and unregistering run there too.
/// </summary>
public sealed class HotkeyService : IHotkeyService, IDisposable
{
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly ConcurrentDictionary<int, Action> _handlers = new();
    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new();
    private uint _threadId;
    private int _nextId;
    private bool _disposed;

    public HotkeyService()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "HotkeyService" };
        _thread.Start();
        _ready.Wait();
    }

    public Outcome<HotkeyRegistration> Register(KeyChord chord, Action onPressed)
    {
        ArgumentNullException.ThrowIfNull(onPressed);
        ObjectDisposedException.ThrowIf(_disposed, this);
        var id = Interlocked.Increment(ref _nextId);
        var modifiers = Native.ModNoRepeat
            | (chord.Modifiers.HasFlag(KeyModifiers.Alt) ? 1u : 0)
            | (chord.Modifiers.HasFlag(KeyModifiers.Ctrl) ? 2u : 0)
            | (chord.Modifiers.HasFlag(KeyModifiers.Shift) ? 4u : 0)
            | (chord.Modifiers.HasFlag(KeyModifiers.Win) ? 8u : 0);
        _handlers[id] = onPressed;
        var registered = Run(() => Native.RegisterHotKey(0, id, modifiers, chord.VirtualKey));
        if (!registered)
        {
            _handlers.TryRemove(id, out _);
            return Outcome<HotkeyRegistration>.Fail(CoreErrorCode.HotkeyInUse, $"{chord} is registered by another program.");
        }

        return Outcome<HotkeyRegistration>.Success(new HotkeyRegistration(chord, () =>
        {
            _handlers.TryRemove(id, out _);
            if (!_disposed)
            {
                Run(() => Native.UnregisterHotKey(0, id));
            }
        }));
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
        _ready.Dispose();
    }

    private bool Run(Func<bool> action)
    {
        if (Environment.CurrentManagedThreadId == _thread.ManagedThreadId)
        {
            return action();
        }

        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _work.Enqueue(() => done.TrySetResult(action()));
        Native.PostThreadMessage(_threadId, Native.WmApp, 0, 0);
        return done.Task.Wait(TimeSpan.FromSeconds(5)) && done.Task.Result;
    }

    private void Loop()
    {
        // Creates the thread's message queue before anything is posted to it.
        Native.PeekMessage(out _, 0, 0, 0, 0);
        _threadId = Native.GetCurrentThreadId();
        _ready.Set();
        while (Native.GetMessage(out var message, 0, 0, 0) > 0)
        {
            if (message.Message == Native.WmHotkey && _handlers.TryGetValue((int)message.WParam, out var handler))
            {
                ThreadPool.QueueUserWorkItem(_ => handler());
            }
            else if (message.Message == Native.WmApp)
            {
                while (_work.TryDequeue(out var work))
                {
                    work();
                }
            }
        }

        foreach (var id in _handlers.Keys)
        {
            Native.UnregisterHotKey(0, id);
        }
    }
}

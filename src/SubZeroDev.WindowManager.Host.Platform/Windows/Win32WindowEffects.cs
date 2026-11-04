using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>The few Win32 calls the effects thread makes, so the rules around them can be tested without real windows.</summary>
internal interface IWindowNative
{
    bool IsHung(nint window);

    /// <summary>Calls <c>SetWindowPos</c> with only <paramref name="flags"/>; returns 0, or the last error when it failed.</summary>
    int SetPosition(nint window, uint flags);

    bool IsVisible(nint window);

    /// <summary>Posts <c>WM_CLOSE</c>; returns 0, or the last error when it failed.</summary>
    int PostClose(nint window);

    /// <summary>A monotonic clock in milliseconds.</summary>
    long NowMs { get; }

    void Wait(int milliseconds);
}

/// <summary>The real <see cref="IWindowNative"/>.</summary>
internal sealed class Win32WindowNative : IWindowNative
{
    public bool IsHung(nint window) => DesktopNative.IsHungAppWindow(window);

    public int SetPosition(nint window, uint flags) =>
        DesktopNative.SetWindowPos(window, 0, 0, 0, 0, 0, flags) ? 0 : Math.Max(1, Marshal.GetLastPInvokeError());

    public bool IsVisible(nint window) => DesktopNative.IsWindowVisible(window);

    public int PostClose(nint window) =>
        DesktopNative.PostMessageW(window, DesktopNative.WmClose, 0, 0) ? 0 : Math.Max(1, Marshal.GetLastPInvokeError());

    public long NowMs => Environment.TickCount64;

    public void Wait(int milliseconds) => Thread.Sleep(milliseconds);
}

/// <summary>
/// Hides, shows and closes other applications' windows on one thread of its own (I27). Every call is non-blocking
/// (<c>SWP_ASYNCWINDOWPOS</c>, <c>PostMessage</c>), never activates, moves, sizes or restores, and is made only after
/// the window's fingerprint is checked again (I41). A window that will not hide is reported stuck and left visible.
/// </summary>
public sealed class Win32WindowEffects : IWindowEffects, IDisposable
{
    /// <summary>How long one plan waits, in all, for its hides to land before calling the rest stuck.</summary>
    internal const int VerifyBudgetMs = 150;

    private const int PollMs = 10;
    private const uint Hide = DesktopNative.SwpQuiet | DesktopNative.SwpHideWindow;
    private const uint Show = DesktopNative.SwpQuiet | DesktopNative.SwpShowWindow;

    private readonly Func<nint, WindowFingerprint?> _fingerprint;
    private readonly IWindowNative _native;
    private readonly BlockingCollection<Action> _queue = [];
    private readonly CancellationTokenSource _stopping = new();
    private readonly Thread _thread;

    // Touched only on the effects thread.
    private readonly Dictionary<nint, WindowFingerprint> _hidden = [];

    /// <param name="facts">Reads a window's fingerprint again before each call.</param>
    public Win32WindowEffects(IWindowFacts facts)
        : this(ReaderOf(facts), new Win32WindowNative())
    {
    }

    /// <param name="fingerprint">The fingerprint of a handle now, or null when it is not a window.</param>
    /// <param name="native">The Win32 calls.</param>
    internal Win32WindowEffects(Func<nint, WindowFingerprint?> fingerprint, IWindowNative native)
    {
        _fingerprint = fingerprint;
        _native = native;
        _thread = new Thread(Run) { IsBackground = true, Name = "SubZeroDev window effects" };
        _thread.Start();
    }

    public Task<Outcome<EffectsResult>> ApplyAsync(SwitchPlan plan, CancellationToken ct) =>
        Enqueue(() => ApplyCore(plan, ct), ct, (code, detail) => Outcome<EffectsResult>.Fail(code, detail));

    public Task<Outcome> ShowAllAsync(CancellationToken ct) =>
        Enqueue(() => ShowAllCore(ct), ct, (code, detail) => Outcome.Fail(code, detail));

    public Task<Outcome> CloseAsync(PlannedWindow window, CancellationToken ct) =>
        Enqueue(() => CloseCore(window), ct, (code, detail) => Outcome.Fail(code, detail));

    /// <summary>Stops the thread. Work still queued completes as cancelled; nothing hidden is shown.</summary>
    public void Dispose()
    {
        if (_stopping.IsCancellationRequested)
        {
            return;
        }

        _stopping.Cancel();
        _queue.CompleteAdding();
        if (Thread.CurrentThread != _thread)
        {
            _thread.Join(TimeSpan.FromSeconds(2));
        }
    }

    private static Func<nint, WindowFingerprint?> ReaderOf(IWindowFacts facts) =>
        facts is Win32WindowFacts win32 ? win32.Fingerprint : handle => facts.Read(handle)?.Fingerprint;

    private Task<T> Enqueue<T>(Func<T> work, CancellationToken ct, Func<CoreErrorCode, string, T> fail)
    {
        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Item()
        {
            if (_stopping.IsCancellationRequested || ct.IsCancellationRequested)
            {
                done.TrySetResult(fail(CoreErrorCode.Cancelled, "The window effects were cancelled before they started."));
                return;
            }

            try
            {
                done.TrySetResult(work());
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                done.TrySetResult(fail(CoreErrorCode.CommandFailed, e.Message));
            }
        }

        try
        {
            _queue.Add(Item);
        }
        catch (InvalidOperationException)
        {
            done.TrySetResult(fail(CoreErrorCode.Cancelled, "The window effects have stopped."));
        }

        return done.Task;
    }

    private void Run()
    {
        foreach (var item in _queue.GetConsumingEnumerable())
        {
            item();
        }
    }

    private Outcome<EffectsResult> ApplyCore(SwitchPlan plan, CancellationToken ct)
    {
        var stuck = new List<WindowKey>();
        var gone = new List<WindowKey>();
        var landing = new List<PlannedWindow>();
        var cancelled = false;

        foreach (var window in plan.Hide)
        {
            if (ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var handle = window.Key.Handle;
            if (!Matches(window))
            {
                gone.Add(window.Key);
            }
            else if (_native.IsHung(handle))
            {
                stuck.Add(window.Key);
            }
            else
            {
                switch (_native.SetPosition(handle, Hide))
                {
                    case 0:
                        _hidden[handle] = window.Fingerprint;
                        landing.Add(window);
                        break;
                    case DesktopNative.ErrorInvalidWindowHandle:
                        gone.Add(window.Key);
                        break;
                    default:
                        stuck.Add(window.Key);
                        break;
                }
            }
        }

        foreach (var window in plan.Show)
        {
            if (cancelled || ct.IsCancellationRequested)
            {
                cancelled = true;
                break;
            }

            var handle = window.Key.Handle;
            if (!Matches(window))
            {
                gone.Add(window.Key);
                Forget(window);
                continue;
            }

            switch (_native.SetPosition(handle, Show))
            {
                case 0:
                    Forget(window);
                    break;
                case DesktopNative.ErrorInvalidWindowHandle:
                    gone.Add(window.Key);
                    Forget(window);
                    break;
            }
        }

        // A hide that has not landed within the budget is undone, so the window stays where the user can reach it.
        foreach (var window in AwaitHidden(landing))
        {
            _native.SetPosition(window.Key.Handle, Show);
            Forget(window);
            stuck.Add(window.Key);
        }

        return cancelled
            ? Outcome<EffectsResult>.Fail(CoreErrorCode.Cancelled, "The switch was cancelled part way.")
            : Outcome<EffectsResult>.Success(new EffectsResult(stuck, gone));
    }

    /// <summary>The windows still visible once the budget is spent.</summary>
    private List<PlannedWindow> AwaitHidden(List<PlannedWindow> landing)
    {
        var deadline = _native.NowMs + VerifyBudgetMs;
        while (true)
        {
            landing.RemoveAll(w => !_native.IsVisible(w.Key.Handle));
            if (landing.Count == 0 || _native.NowMs >= deadline)
            {
                return landing;
            }

            _native.Wait((int)Math.Min(PollMs, Math.Max(1, deadline - _native.NowMs)));
        }
    }

    private Outcome ShowAllCore(CancellationToken ct)
    {
        foreach (var (handle, print) in _hidden.ToList())
        {
            if (ct.IsCancellationRequested)
            {
                return Outcome.Fail(CoreErrorCode.Cancelled, "Showing the hidden windows was cancelled part way.");
            }

            if (_fingerprint(handle) != print)
            {
                _hidden.Remove(handle);
                continue;
            }

            var error = _native.SetPosition(handle, Show);
            if (error is 0 or DesktopNative.ErrorInvalidWindowHandle)
            {
                _hidden.Remove(handle);
            }
        }

        return _hidden.Count == 0
            ? Outcome.Success
            : Outcome.Fail(CoreErrorCode.CommandFailed, $"{_hidden.Count} hidden windows refused to be shown.", retryable: true);
    }

    private Outcome CloseCore(PlannedWindow window)
    {
        if (!Matches(window))
        {
            return Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone.");
        }

        return _native.PostClose(window.Key.Handle) switch
        {
            0 => Outcome.Success,
            DesktopNative.ErrorInvalidWindowHandle => Outcome.Fail(CoreErrorCode.WindowGone, "The window is gone."),
            var error => Outcome.Fail(CoreErrorCode.CommandFailed, $"The window refused the close request (error {error})."),
        };
    }

    private bool Matches(PlannedWindow window) => window.Key.Handle != 0 && _fingerprint(window.Key.Handle) == window.Fingerprint;

    private void Forget(PlannedWindow window)
    {
        if (_hidden.TryGetValue(window.Key.Handle, out var print) && print == window.Fingerprint)
        {
            _hidden.Remove(window.Key.Handle);
        }
    }
}

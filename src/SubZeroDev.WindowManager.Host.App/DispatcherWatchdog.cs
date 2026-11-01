using Microsoft.UI.Dispatching;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// Checks I9: a background loop posts a probe to the UI thread every 25 ms and measures how long it
/// waits to run. A wait over <see cref="Threshold"/> is a stall; each is counted and logged. On in
/// debug builds and under the test hook.
/// </summary>
internal sealed class DispatcherWatchdog : IDisposable
{
    public static readonly TimeSpan Threshold = TimeSpan.FromMilliseconds(50);

    private static readonly TimeSpan Probe = TimeSpan.FromMilliseconds(25);

    private readonly DispatcherQueue _dispatcher;
    private readonly IHostLog _log;
    private readonly CancellationTokenSource _stop = new();
    private long _stalls;
    private long _longestMs;

    public DispatcherWatchdog(DispatcherQueue dispatcher, IHostLog log)
    {
        _dispatcher = dispatcher;
        _log = log;
        _ = Task.Run(RunAsync);
    }

    /// <summary>The number of stalls over the threshold so far.</summary>
    public long Stalls => Interlocked.Read(ref _stalls);

    /// <summary>The longest wait seen so far, in milliseconds.</summary>
    public long LongestMs => Interlocked.Read(ref _longestMs);

    public void Dispose()
    {
        _stop.Cancel();
        _stop.Dispose();
    }

    private async Task RunAsync()
    {
        var token = _stop.Token;
        try
        {
            while (!token.IsCancellationRequested)
            {
                var ran = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                var posted = Environment.TickCount64;
                if (!_dispatcher.TryEnqueue(DispatcherQueuePriority.Normal, () => ran.TrySetResult()))
                {
                    return;
                }

                // A probe that never runs is the longest stall of all, so it is measured as it waits.
                while (await Task.WhenAny(ran.Task, Task.Delay(Threshold * 20, token)).ConfigureAwait(false) != ran.Task)
                {
                    token.ThrowIfCancellationRequested();
                    _log.Write(LogLevel.Warning, "watchdog", $"The UI thread has not run for {Environment.TickCount64 - posted} ms.");
                }

                Record(Environment.TickCount64 - posted);
                await Task.Delay(Probe, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Record(long waitedMs)
    {
        long longest;
        do
        {
            longest = Interlocked.Read(ref _longestMs);
        }
        while (waitedMs > longest && Interlocked.CompareExchange(ref _longestMs, waitedMs, longest) != longest);

        if (waitedMs > Threshold.TotalMilliseconds)
        {
            Interlocked.Increment(ref _stalls);
            _log.Write(LogLevel.Warning, "watchdog", $"UI thread stalled for {waitedMs} ms.");
        }
    }
}

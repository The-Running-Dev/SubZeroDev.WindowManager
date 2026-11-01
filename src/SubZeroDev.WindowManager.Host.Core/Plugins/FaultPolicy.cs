namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// The supervisor's fault timings. <see cref="Default"/> holds the design values; tests shrink them.
/// </summary>
/// <param name="Backoffs">The wait before the 1st, 2nd and later restarts.</param>
/// <param name="PingInterval">How often a running worker is pinged.</param>
/// <param name="PingTimeout">How long without a ping reply before the worker is faulted.</param>
/// <param name="MessagesPerSecond">The message rate a worker may send; above it events are dropped and patches coalesced.</param>
/// <param name="FloodSustain">How long a worker may stay above the rate before it is faulted with Flood.</param>
public sealed record SupervisorTimings(IReadOnlyList<TimeSpan> Backoffs, TimeSpan PingInterval, TimeSpan PingTimeout,
    int MessagesPerSecond, TimeSpan FloodSustain)
{
    public static SupervisorTimings Default { get; } = new(
        [TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(30)],
        TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), 200, TimeSpan.FromSeconds(10));
}

/// <summary>
/// Counts one plugin's faults inside <c>windowSeconds</c> (I17). Each of the first
/// <c>maxRestarts</c> faults in the window earns a restart after its backoff; the next one spends
/// the budget and the plugin becomes Unavailable.
/// </summary>
public sealed class RestartBudget(RestartPolicy policy, IReadOnlyList<TimeSpan> backoffs)
{
    private readonly List<DateTimeOffset> _faults = [];

    /// <summary>The faults still inside the window, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> Faults => _faults;

    /// <summary>Records a fault; returns the backoff before the restart, or null once the budget is spent.</summary>
    public TimeSpan? RecordFault(DateTimeOffset now)
    {
        _faults.RemoveAll(fault => now - fault >= TimeSpan.FromSeconds(policy.WindowSeconds));
        _faults.Add(now);
        var restart = _faults.Count;
        return restart > policy.MaxRestarts ? null : backoffs[Math.Min(restart, backoffs.Count) - 1];
    }

    /// <summary>The faults inside the window that ends at <paramref name="now"/>, oldest first.</summary>
    public IReadOnlyList<DateTimeOffset> Within(DateTimeOffset now) =>
        _faults.Where(fault => now - fault < TimeSpan.FromSeconds(policy.WindowSeconds)).ToList();

    public void Reset() => _faults.Clear();
}

public enum FloodVerdict { Within, Over, Flood }

/// <summary>
/// Meters a channel's incoming messages in one-second windows. A message past the rate in its
/// window is <see cref="FloodVerdict.Over"/>; once every window has been over the rate for
/// <c>sustain</c>, it is <see cref="FloodVerdict.Flood"/>. A window within the rate starts over.
/// </summary>
public sealed class FloodMeter(int perSecond, TimeSpan sustain, Func<long>? clockMs = null)
{
    private readonly Func<long> _clock = clockMs ?? (() => Environment.TickCount64);
    private long _window = long.MinValue;
    private int _count;
    private long? _overSince;
    private bool _windowOver;

    public FloodVerdict Record()
    {
        var now = _clock();
        var window = now / 1000;
        if (window != _window)
        {
            // A window that stayed within the rate, or a gap with no messages, ends the run.
            if (!_windowOver || window != _window + 1)
            {
                _overSince = null;
            }

            _window = window;
            _count = 0;
            _windowOver = false;
        }

        if (++_count <= perSecond)
        {
            return FloodVerdict.Within;
        }

        // The run is timed from the message that first went over the rate, not from its window's start.
        _windowOver = true;
        _overSince ??= now;
        return now - _overSince.Value >= (long)sustain.TotalMilliseconds ? FloodVerdict.Flood : FloodVerdict.Over;
    }
}

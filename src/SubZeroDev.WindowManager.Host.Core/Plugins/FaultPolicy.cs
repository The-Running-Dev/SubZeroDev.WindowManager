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
/// Meters a channel's incoming messages over the trailing second. A message that makes more than
/// <c>perSecond</c> in the second before it is <see cref="FloodVerdict.Over"/>; once every message for
/// <c>sustain</c> has been over, it is <see cref="FloodVerdict.Flood"/>. A message within the rate ends the run.
/// </summary>
public sealed class FloodMeter(int perSecond, TimeSpan sustain, Func<long>? clockMs = null)
{
    private readonly Func<long> _clock = clockMs ?? (() => Environment.TickCount64);
    private readonly long[] _recent = new long[perSecond];
    private int _next;
    private int _count;
    private long? _overSince;

    public FloodVerdict Record()
    {
        var now = _clock();

        // _recent holds the last perSecond arrival times; _next is the oldest once it is full.
        var over = _count == perSecond && _recent[_next] > now - 1000;
        _recent[_next] = now;
        _next = (_next + 1) % perSecond;
        _count = Math.Min(_count + 1, perSecond);

        if (!over)
        {
            _overSince = null;
            return FloodVerdict.Within;
        }

        _overSince ??= now;
        return now - _overSince.Value >= (long)sustain.TotalMilliseconds ? FloodVerdict.Flood : FloodVerdict.Over;
    }
}

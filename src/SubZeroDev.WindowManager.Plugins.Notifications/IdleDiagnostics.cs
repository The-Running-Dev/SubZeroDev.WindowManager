using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace SubZeroDev.WindowManager.Plugins.Notifications;

/// <summary>
/// Opt-in, bounded activity trace. Records timings and counts only, never notification content.
/// Durations are elapsed time, not CPU attribution. No file I/O occurs until the worker stops.
/// </summary>
public sealed class IdleDiagnostics
{
    public const string Variable = "SUBZERODEV_WM_NOTIFICATION_DIAGNOSTICS";
    private const int Capacity = 8192;
    private readonly ConcurrentQueue<Activity> _activities = new();
    private int _count;
    private int _dropped;

    public long Started { get; } = Stopwatch.GetTimestamp();

    public void Record(string kind, long started)
    {
        var ended = Stopwatch.GetTimestamp();
        if (Interlocked.Increment(ref _count) <= Capacity)
        {
            _activities.Enqueue(new Activity(kind, started, ended));
        }
        else
        {
            Interlocked.Increment(ref _dropped);
        }
    }

    public void Write(string path) => File.WriteAllText(path, JsonSerializer.Serialize(new
    {
        provider = "fake", // This trace is enabled only with the test hook.
        frequency = Stopwatch.Frequency,
        started = Started,
        dropped = Volatile.Read(ref _dropped),
        runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
        os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
        activities = _activities.ToArray(),
    }, new JsonSerializerOptions { WriteIndented = true }));

    public sealed record Activity(string Kind, long Started, long Ended);
}

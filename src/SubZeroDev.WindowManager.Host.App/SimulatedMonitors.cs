using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// The real monitors plus any the test hook adds, so tests can exercise a second monitor or another
/// scale without touching the user's display configuration. A simulated monitor has no handle, is never
/// primary and has no taskbar (its work area is its bounds); the shell refuses app bars on it.
/// </summary>
public sealed class SimulatedMonitors : IMonitorService
{
    private readonly IMonitorService _real;
    private IReadOnlyList<MonitorInfo> _simulated = [];

    public SimulatedMonitors(IMonitorService real)
    {
        _real = real;
        _real.TopologyChanged += (_, _) => TopologyChanged?.Invoke(this, EventArgs.Empty);
    }

    public IReadOnlyList<MonitorInfo> Monitors => [.. _real.Monitors, .. Volatile.Read(ref _simulated)];

    public event EventHandler? TopologyChanged;

    /// <summary>Replaces the simulated monitors and raises <see cref="TopologyChanged"/> at once.</summary>
    public void Set(IEnumerable<MonitorInfo> monitors)
    {
        Volatile.Write(ref _simulated, [.. monitors.Select(m => m with { Handle = 0, IsPrimary = false, WorkArea = m.Bounds })]);
        TopologyChanged?.Invoke(this, EventArgs.Empty);
    }
}

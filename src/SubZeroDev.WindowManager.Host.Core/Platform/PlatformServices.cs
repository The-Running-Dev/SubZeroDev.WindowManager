using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

public sealed record MonitorInfo(string StableId, nint Handle, RectInt Bounds, RectInt WorkArea, uint Dpi, bool IsPrimary)
{
    public double Scale => Dpi / 96.0;
}

public readonly record struct RectInt(int Left, int Top, int Right, int Bottom)
{
    public int Width => Right - Left;

    public int Height => Bottom - Top;

    public override string ToString() => $"{Left},{Top},{Right},{Bottom}";
}

public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> Monitors { get; }

    event EventHandler TopologyChanged;   // debounced 250 ms
}

public interface IAppBarService
{
    Task<Outcome<RectInt>> RegisterAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct); // journals first

    Task<Outcome<RectInt>> UpdateAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct);

    Task<Outcome> RemoveAsync(nint hwnd, CancellationToken ct);   // reverts, then ends the journal entry

    event EventHandler<FullscreenChangedEventArgs> FullscreenChanged;

    event EventHandler ShellRestarted;    // "TaskbarCreated"
}

public sealed record FullscreenChangedEventArgs(string MonitorId, bool IsFullscreen);

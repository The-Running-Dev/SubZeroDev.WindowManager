using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Shell app-bar registration. Each call runs <c>SHAppBarMessage</c> on a background task (I9) and
/// returns the rectangle the shell granted; the caller moves its window there.
/// </summary>
public sealed class AppBarService : IAppBarService
{
    private static readonly uint CallbackMessage = Native.RegisterWindowMessage("SubZeroDev.WindowManager.AppBar");

    private readonly IMonitorService _monitors;
    private readonly HashSet<nint> _registered = [];
    private readonly Lock _gate = new();

    public AppBarService(IMonitorService monitors)
    {
        _monitors = monitors;
        // Last resort: a host that exits without closing its panels still gives the space back.
        AppDomain.CurrentDomain.ProcessExit += (_, _) => RemoveAll();
    }

    public event EventHandler<FullscreenChangedEventArgs> FullscreenChanged
    {
        add { }
        remove { }
    }

    public event EventHandler ShellRestarted
    {
        add { }
        remove { }
    }

    public Task<Outcome<RectInt>> RegisterAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct) =>
        Task.Run(() =>
        {
            var monitor = Find(monitorId);
            if (monitor is null)
            {
                return Outcome<RectInt>.Fail(CoreErrorCode.NotFound, $"Monitor '{monitorId}' is not connected.");
            }

            var data = NewData(hwnd);
            data.CallbackMessage = CallbackMessage;
            if (Native.SHAppBarMessage(Native.AbmNew, ref data) == 0)
            {
                return Outcome<RectInt>.Fail(CoreErrorCode.AppBarRefused, "The shell refused ABM_NEW.", retryable: true);
            }

            lock (_gate)
            {
                _registered.Add(hwnd);
            }

            return Outcome<RectInt>.Success(SetPosition(hwnd, monitor, edge, thicknessPx));
        }, ct);

    public Task<Outcome<RectInt>> UpdateAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct) =>
        Task.Run(() =>
        {
            var monitor = Find(monitorId);
            return monitor is null
                ? Outcome<RectInt>.Fail(CoreErrorCode.NotFound, $"Monitor '{monitorId}' is not connected.")
                : Outcome<RectInt>.Success(SetPosition(hwnd, monitor, edge, thicknessPx));
        }, ct);

    public Task<Outcome> RemoveAsync(nint hwnd, CancellationToken ct) =>
        Task.Run(() =>
        {
            lock (_gate)
            {
                if (!_registered.Remove(hwnd))
                {
                    return Outcome.Fail(CoreErrorCode.NotFound, "The window holds no app bar.");
                }
            }

            var data = NewData(hwnd);
            Native.SHAppBarMessage(Native.AbmRemove, ref data);
            return Outcome.Success;
        }, ct);

    public void RemoveNow(nint hwnd)
    {
        lock (_gate)
        {
            if (!_registered.Remove(hwnd))
            {
                return;
            }
        }

        var data = NewData(hwnd);
        Native.SHAppBarMessage(Native.AbmRemove, ref data);
    }

    private void RemoveAll()
    {
        nint[] windows;
        lock (_gate)
        {
            windows = [.. _registered];
        }

        foreach (var hwnd in windows)
        {
            RemoveNow(hwnd);
        }
    }

    /// <summary>Asks the shell for space on <paramref name="edge"/>, trims the answer to the thickness, and claims it.</summary>
    private static RectInt SetPosition(nint hwnd, MonitorInfo monitor, PanelEdge edge, int thickness)
    {
        var data = NewData(hwnd);
        data.Edge = edge switch { PanelEdge.Left => 0u, PanelEdge.Top => 1u, PanelEdge.Right => 2u, _ => 3u };
        data.Bounds = new Rect { Left = monitor.Bounds.Left, Top = monitor.Bounds.Top, Right = monitor.Bounds.Right, Bottom = monitor.Bounds.Bottom };
        Native.SHAppBarMessage(Native.AbmQueryPos, ref data);

        // QUERYPOS moves the inner edge clear of other bars; set the thickness from that edge.
        switch (edge)
        {
            case PanelEdge.Top:
                data.Bounds.Bottom = data.Bounds.Top + thickness;
                break;
            case PanelEdge.Bottom:
                data.Bounds.Top = data.Bounds.Bottom - thickness;
                break;
            case PanelEdge.Left:
                data.Bounds.Right = data.Bounds.Left + thickness;
                break;
            case PanelEdge.Right:
                data.Bounds.Left = data.Bounds.Right - thickness;
                break;
        }

        Native.SHAppBarMessage(Native.AbmSetPos, ref data);
        return MonitorService.ToRectInt(data.Bounds);
    }

    private MonitorInfo? Find(string monitorId) => _monitors.Monitors.FirstOrDefault(m => m.StableId == monitorId);

    private static unsafe AppBarData NewData(nint hwnd) => new() { Size = (uint)sizeof(AppBarData), Window = hwnd };
}

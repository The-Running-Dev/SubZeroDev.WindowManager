using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Monitors as Windows reports them; the stable id is the GDI device name. With <see cref="ShellEvents"/>
/// it raises <see cref="TopologyChanged"/> 250 ms after the last display change, and only when a monitor
/// came, went, moved, changed scale or became primary (work-area changes alone do not count).
/// </summary>
public sealed unsafe class MonitorService : IMonitorService, IDisposable
{
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(250);

    private readonly Timer? _debounce;
    private readonly Lock _gate = new();
    private string _shape = string.Empty;

    public MonitorService(ShellEvents? events = null)
    {
        _shape = Shape(Monitors);
        if (events is not null)
        {
            _debounce = new Timer(_ => Check());
            events.DisplayChanged += (_, _) => _debounce.Change(Debounce, Timeout.InfiniteTimeSpan);
        }
    }

    public IReadOnlyList<MonitorInfo> Monitors
    {
        get
        {
            var found = new List<MonitorInfo>();
            var handle = GCHandle.Alloc(found);
            try
            {
                Native.EnumDisplayMonitors(0, 0, &Collect, GCHandle.ToIntPtr(handle));
            }
            finally
            {
                handle.Free();
            }

            return found;
        }
    }

    public event EventHandler? TopologyChanged;

    public void Dispose() => _debounce?.Dispose();

    private void Check()
    {
        var shape = Shape(Monitors);
        lock (_gate)
        {
            if (shape == _shape)
            {
                return;
            }

            _shape = shape;
        }

        TopologyChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string Shape(IEnumerable<MonitorInfo> monitors) =>
        string.Join("|", monitors.OrderBy(m => m.StableId, StringComparer.Ordinal)
            .Select(m => FormattableString.Invariant($"{m.StableId} {m.Bounds} {m.Dpi} {m.IsPrimary}")));

    /// <summary>The primary monitor's work area as <c>SPI_GETWORKAREA</c> reports it.</summary>
    public static RectInt PrimaryWorkArea()
    {
        Rect rect;
        Native.SystemParametersInfo(Native.SpiGetWorkArea, 0, &rect, 0);
        return ToRectInt(rect);
    }

    internal static RectInt ToRectInt(Rect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    [UnmanagedCallersOnly]
    private static int Collect(nint monitor, nint hdc, Rect* clip, nint data)
    {
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        if (!Native.GetMonitorInfo(monitor, &info))
        {
            return 1;
        }

        var dpi = Native.GetDpiForMonitor(monitor, 0, out var dpiX, out _) == 0 ? dpiX : 96u;
        var device = new string(info.Device);
        var list = (List<MonitorInfo>)GCHandle.FromIntPtr(data).Target!;
        list.Add(new MonitorInfo(device.TrimEnd('\0'), monitor, ToRectInt(info.Monitor), ToRectInt(info.Work), dpi,
            (info.Flags & Native.MonitorInfoPrimary) != 0));
        return 1;
    }
}

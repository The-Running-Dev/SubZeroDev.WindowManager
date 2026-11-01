using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Monitors as Windows reports them. S1 reads the list once per call and raises no topology changes;
/// the stable id is the GDI device name until the multi-monitor slice replaces it.
/// </summary>
public sealed unsafe class MonitorService : IMonitorService
{
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

    public event EventHandler TopologyChanged
    {
        add { }
        remove { }
    }

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

using Microsoft.UI.Dispatching;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>The wallpaper the desktop draws: an image (null for none), its fit, and where it came from (<c>preset</c> or <c>system</c>).</summary>
public sealed record WallpaperChoice(string? Path, WallpaperFit Fit, string Source);

/// <summary>
/// The desktop floor in replacement mode: one <see cref="WallpaperWindow"/> per monitor, under everything,
/// with desktop panels kept just above it. Each monitor asks for its own wallpaper choice (S30.2). It is rebuilt
/// when the monitors change and redrawn when a monitor's choice changes. Everything here runs on the UI thread it was created on.
/// </summary>
public sealed class DesktopSurface
{
    private readonly IMonitorService _monitors;
    private readonly Func<string, WallpaperChoice> _resolve;
    private readonly Dictionary<string, WallpaperChoice> _choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHostLog _log;
    private readonly DispatcherQueue _dispatcher;
    private List<WallpaperWindow> _windows = [];
    private bool _closed;

    /// <param name="monitors">The monitors to cover.</param>
    /// <param name="resolve">Works out the wallpaper for the monitor with the given stable id.</param>
    /// <param name="log">The host log.</param>
    public DesktopSurface(IMonitorService monitors, Func<string, WallpaperChoice> resolve, IHostLog log)
    {
        _monitors = monitors;
        _resolve = resolve;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _monitors.TopologyChanged += (_, _) => _dispatcher.TryEnqueue(() =>
        {
            if (!_closed)
            {
                Apply();
            }
        });
    }

    public IReadOnlyList<WallpaperWindow> Windows => _windows;

    /// <summary>The first monitor's choice, or null before the floor is built.</summary>
    public WallpaperChoice? Choice => _windows.Count > 0 ? ChoiceOf(_windows[0].Monitor.StableId) : null;

    /// <summary>The choice the monitor with the given stable id is showing, or null for an unknown monitor.</summary>
    /// <param name="monitorId">The monitor's stable id.</param>
    public WallpaperChoice? ChoiceOf(string monitorId) => _choices.GetValueOrDefault(monitorId);

    /// <summary>Builds the floor afresh for the current monitors.</summary>
    public void Apply()
    {
        var old = _windows;
        _choices.Clear();
        var windows = new List<WallpaperWindow>();
        foreach (var monitor in _monitors.Monitors)
        {
            try
            {
                var choice = _resolve(monitor.StableId);
                _choices[monitor.StableId] = choice;
                var window = new WallpaperWindow(monitor);
                window.SetWallpaper(choice.Path, choice.Fit);
                window.Show();
                windows.Add(window);
            }
            catch (Exception e)
            {
                _log.Write(LogLevel.Error, "desktop", $"The desktop on monitor {monitor.StableId} could not be shown: {e}");
            }
        }

        _windows = windows;
        PanelWindow.Floors = [.. windows.Select(w => w.Handle)];
        foreach (var window in old)
        {
            window.Close();
        }

        foreach (var window in windows)
        {
            var choice = _choices[window.Monitor.StableId];
            _log.Write(LogLevel.Info, "desktop", $"Desktop shown on monitor {window.Monitor.StableId}, wallpaper {choice.Source}: {choice.Path ?? "none"} ({choice.Fit}).");
        }

        _log.Write(LogLevel.Info, "desktop", $"Desktop shown on {windows.Count} monitor(s).");
    }

    /// <summary>Works out each monitor's wallpaper again and redraws the ones that changed.</summary>
    public void Refresh()
    {
        if (_closed)
        {
            return;
        }

        foreach (var window in _windows)
        {
            var id = window.Monitor.StableId;
            var choice = _resolve(id);
            if (choice == ChoiceOf(id))
            {
                continue;
            }

            _choices[id] = choice;
            window.SetWallpaper(choice.Path, choice.Fit);
            _log.Write(LogLevel.Info, "desktop", $"Wallpaper on monitor {id} is now {choice.Source}: {choice.Path ?? "none"} ({choice.Fit}).");
        }
    }

    public void Close()
    {
        _closed = true;
        PanelWindow.Floors = [];
        foreach (var window in _windows)
        {
            window.Close();
        }

        _windows = [];
    }

    /// <summary>Every top-level window's place in the z-order, topmost first: for the test hook's status.</summary>
    public static IReadOnlyDictionary<nint, int> ZOrder()
    {
        var order = new Dictionary<nint, int>();
        for (var window = GetWindow(GetDesktopWindow(), GwChild); window != 0 && order.Count < 100_000; window = GetWindow(window, GwHwndNext))
        {
            order[window] = order.Count;
        }

        return order;
    }

    private const uint GwHwndNext = 2;
    private const uint GwChild = 5;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetDesktopWindow();

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);
}

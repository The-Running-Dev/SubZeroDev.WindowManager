using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Host.Surfaces;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Host.Theming;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// End-to-end test support, active only when <c>SUBZERODEV_WM_TEST_HOOK</c> names a directory. Every
/// 250 ms it writes <c>status.json</c> there (what the host shows, read from the live visual tree) and
/// exits normally once an <c>exit</c> file appears. A <c>retry</c> file holding a plugin id runs that
/// plugin's placeholder Retry command, and every pointer press on a panel is time-stamped.
/// </summary>
internal sealed class TestHook
{
    public const string Variable = "SUBZERODEV_WM_TEST_HOOK";

    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string _directory;
    private readonly App _app;
    private readonly HostOptions _options;
    private readonly Locations _locations;
    private readonly ThemeEngine _theme;
    private readonly SurfaceManager _surfaces;
    private readonly IPluginSupervisor _supervisor;
    private readonly WorkerLauncher _launcher;
    private readonly IPackageCatalog _catalog;
    private readonly DispatcherQueueTimer _timer;
    private readonly DispatcherWatchdog _watchdog;
    private readonly HashSet<WmPanel> _watched = [];
    private long _lastPointerPress;

    public TestHook(string directory, App app, HostOptions options, Locations locations, ThemeEngine theme, SurfaceManager surfaces,
        IPluginSupervisor supervisor, WorkerLauncher launcher, IPackageCatalog catalog, DispatcherQueue dispatcher,
        DispatcherWatchdog watchdog)
    {
        _watchdog = watchdog;
        _supervisor = supervisor;
        _launcher = launcher;
        _catalog = catalog;
        _directory = directory;
        _app = app;
        _options = options;
        _locations = locations;
        _theme = theme;
        _surfaces = surfaces;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => Tick();
    }

    public void Start()
    {
        Directory.CreateDirectory(_directory);
        Tick();
        _timer.Start();
    }

    public void Stop() => _timer.Stop();

    private void Tick()
    {
        if (File.Exists(Path.Combine(_directory, "exit")))
        {
            _timer.Stop();
            _ = _app.ShutdownAsync();
            return;
        }

        // The test reads status.json while this replaces it; a refused replace waits for the next tick.
        // An exception escaping a dispatcher callback ends a WinUI process without UnhandledException.
        try
        {
            WatchPointers();
            RunRetry();
            var temp = Path.Combine(_directory, "status.json.tmp");
            File.WriteAllText(temp, Snapshot().ToJsonString(new JsonSerializerOptions { WriteIndented = true }), Utf8NoBom);
            File.Move(temp, Path.Combine(_directory, "status.json"), overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    /// <summary>Time-stamps pointer presses on each panel, handled or not, as UTC milliseconds.</summary>
    private void WatchPointers()
    {
        foreach (var panel in _surfaces.Panels.Select(window => window.Panel).Where(_watched.Add))
        {
            panel.AddHandler(UIElement.PointerPressedEvent,
                new Microsoft.UI.Xaml.Input.PointerEventHandler((_, _) =>
                    Interlocked.Exchange(ref _lastPointerPress, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())),
                handledEventsToo: true);
        }
    }

    private void RunRetry()
    {
        var file = Path.Combine(_directory, "retry");
        if (!File.Exists(file))
        {
            return;
        }

        var pluginId = File.ReadAllText(file).Trim();
        File.Delete(file);
        var command = _surfaces.Views.Where(view => view.Instance.Plugin == pluginId)
            .Select(view => view.Placeholder?.RetryCommand).FirstOrDefault(command => command is not null);
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private JsonObject Snapshot()
    {
        var panels = new JsonArray();
        // Ready once every template has been applied, so tests never read a half-built tree.
        var ready = _surfaces.Panels.Count > 0;
        foreach (var window in _surfaces.Panels)
        {
            var placeholders = new JsonArray();
            foreach (var zone in new[] { window.Panel.Start, window.Panel.Center, window.Panel.End }.OfType<WmZone>())
            {
                foreach (var placeholder in zone.Items.OfType<WmContribution>().Select(c => c.Content).OfType<WmPlaceholder>())
                {
                    var state = CurrentState(placeholder, WmPlaceholder.ReasonStatesGroup);
                    // Reason names the visual state; until the state catches up the tree is half-built.
                    ready &= state is not null && state == placeholder.Reason;
                    placeholders.Add(new JsonObject
                    {
                        ["plugin"] = placeholder.PluginName,
                        ["reason"] = placeholder.Reason,
                        ["state"] = state,
                    });
                }
            }

            ready &= VisualTreeHelper.GetChildrenCount(window.Panel) > 0;
            panels.Add(new JsonObject
            {
                ["id"] = window.Definition.Id.ToString(),
                ["handle"] = (long)window.Handle,
                ["monitor"] = window.Monitor.StableId,
                ["scale"] = window.Monitor.Scale,
                ["edge"] = window.Panel.Edge,
                ["behaviour"] = window.Panel.Behaviour,
                ["thicknessPx"] = window.ThicknessPx,
                ["bounds"] = Rect(window.Bounds),
                ["appBarRect"] = window.AppBarRect is { } granted ? Rect(granted) : null,
                ["background"] = TemplateBackground(window.Panel),
                ["placeholders"] = placeholders,
            });
        }

        return new JsonObject
        {
            ["ready"] = ready,
            ["hostPid"] = Environment.ProcessId,
            ["args"] = new JsonArray(_options.Args.Select(a => (JsonNode?)a).ToArray()),
            ["guardianPid"] = _options.GuardianPid,
            ["guardianPipe"] = _options.GuardianPipe,
            ["dataRoot"] = _locations.DataRoot,
            ["contentRoot"] = _locations.ContentRoot,
            ["workArea"] = Rect(MonitorService.PrimaryWorkArea()),
            ["theme"] = new JsonObject
            {
                ["id"] = _theme.Current.ThemeId,
                ["source"] = _theme.CurrentSource.ToString(),
                ["backdrop"] = _theme.CurrentBackdrop.ToString(),
                ["errors"] = new JsonArray(_app.InitialThemeErrors.Select(e => (JsonNode?)new JsonObject
                {
                    ["code"] = e.Code.ToString(),
                    ["file"] = e.File,
                    ["key"] = e.Key,
                    ["detail"] = e.Detail,
                }).ToArray()),
            },
            ["panels"] = panels,
            ["contributions"] = new JsonArray(_surfaces.Views.Select(view => (JsonNode?)new JsonObject
            {
                ["instanceId"] = view.Instance.Id.ToString("D"),
                ["plugin"] = view.Instance.Plugin,
                ["reason"] = view.Reason,
                ["hasRetry"] = view.Placeholder?.RetryCommand is not null,
                ["texts"] = new JsonArray(view.Texts().Select(text => (JsonNode?)text).ToArray()),
                ["bounds"] = ScreenBounds(view),
            }).ToArray()),
            ["plugins"] = new JsonArray(_supervisor.All.Select(status => (JsonNode?)new JsonObject
            {
                ["id"] = status.PluginId,
                ["state"] = status.State.ToString(),
                ["pid"] = status.ProcessId,
                ["lastFault"] = status.LastFault?.ToString(),
                ["recentFaults"] = status.RecentFaults.Count,
                ["problem"] = status.Problems.FirstOrDefault()?.Detail,
            }).ToArray()),
            ["watchdog"] = new JsonObject
            {
                ["stalls"] = _watchdog.Stalls,
                ["longestMs"] = _watchdog.LongestMs,
            },
            ["lastPointerPress"] = Interlocked.Read(ref _lastPointerPress),
            ["nowUtcMs"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            ["jobPids"] = new JsonArray(_launcher.JobProcessIds().Select(pid => (JsonNode?)pid).ToArray()),
            ["catalog"] = new JsonArray(_catalog.All(PackageKind.Plugin).Select(record => (JsonNode?)new JsonObject
            {
                ["id"] = record.Id,
                ["source"] = record.Source.ToString(),
                ["health"] = record.Health.ToString(),
            }).ToArray()),
        };
    }

    /// <summary>The view's bounds in physical screen pixels, or null before it is laid out.</summary>
    private JsonObject? ScreenBounds(ContributionView view)
    {
        var window = _surfaces.Panels.FirstOrDefault(panel => IsDescendant(view.Root, panel.Panel));
        if (window is null || view.Root.ActualWidth <= 0 || view.Root.XamlRoot is not { } root)
        {
            return null;
        }

        var origin = view.Root.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = root.RasterizationScale;
        var left = window.Bounds.Left + (int)Math.Round(origin.X * scale);
        var top = window.Bounds.Top + (int)Math.Round(origin.Y * scale);
        return Rect(new RectInt(left, top, left + (int)Math.Round(view.Root.ActualWidth * scale), top + (int)Math.Round(view.Root.ActualHeight * scale)));
    }

    private static bool IsDescendant(DependencyObject element, DependencyObject ancestor)
    {
        for (var current = element; current is not null; current = VisualTreeHelper.GetParent(current))
        {
            if (ReferenceEquals(current, ancestor))
            {
                return true;
            }
        }

        return false;
    }

    private static JsonObject Rect(RectInt r) => new() { ["left"] = r.Left, ["top"] = r.Top, ["right"] = r.Right, ["bottom"] = r.Bottom };

    /// <summary>The background of the control template's root element, as <c>#AARRGGBB</c>.</summary>
    private static string? TemplateBackground(Control control)
    {
        if (VisualTreeHelper.GetChildrenCount(control) == 0)
        {
            return null;
        }

        var brush = VisualTreeHelper.GetChild(control, 0) switch
        {
            Border border => border.Background,
            Panel panel => panel.Background,
            Control inner => inner.Background,
            _ => null,
        };
        return brush is SolidColorBrush solid
            ? $"#{solid.Color.A:X2}{solid.Color.R:X2}{solid.Color.G:X2}{solid.Color.B:X2}"
            : null;
    }

    private static string? CurrentState(Control control, string group)
    {
        if (VisualTreeHelper.GetChildrenCount(control) == 0 || VisualTreeHelper.GetChild(control, 0) is not FrameworkElement root)
        {
            return null;
        }

        return VisualStateManager.GetVisualStateGroups(root).FirstOrDefault(g => g.Name == group)?.CurrentState?.Name;
    }
}

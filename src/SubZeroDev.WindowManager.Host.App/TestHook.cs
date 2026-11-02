using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Host.Surfaces;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// End-to-end test support, active only when <c>SUBZERODEV_WM_TEST_HOOK</c> names a directory. Every
/// 250 ms it writes <c>status.json</c> there (what the host shows, read from the live visual tree) and
/// exits normally once an <c>exit</c> file appears. A <c>retry</c> file holding a plugin id runs that
/// plugin's placeholder Retry command, every pointer press on a panel is time-stamped, and a <c>hang</c>
/// file blocks the UI thread for two minutes so the guardian sees the host stop answering. A
/// <c>preset</c> file holding a preset id runs <c>host.preset.apply</c> and records the outcome. An
/// <c>invoke</c> file (command id, then optionally a source and a JSON args line) invokes a command
/// through the router and records the outcome; a <c>focus-panel</c> file holding a panel id focuses
/// that panel as the keyboard shortcut will.
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
    private readonly GuardianLink? _guardian;
    private readonly CommandRouter _router;
    private readonly HotkeyBinder _hotkeys;
    private readonly FeedBroker _feeds;
    private readonly SimulatedMonitors _monitors;
    private readonly ShellEvents _shellEvents;
    private JsonObject? _lastInvoke;
    private int _settingsUiRuns;
    private string? _lastSettingsUi;
    private readonly HashSet<WmPanel> _watched = [];
    private long _lastPointerPress;
    private int _writing;
    private int _reading;
    private readonly DispatcherQueue _dispatcher;
    private JsonObject? _lastPresetApply;

    public TestHook(string directory, App app, HostOptions options, Locations locations, ThemeEngine theme, SurfaceManager surfaces,
        IPluginSupervisor supervisor, WorkerLauncher launcher, IPackageCatalog catalog, DispatcherQueue dispatcher,
        DispatcherWatchdog watchdog, GuardianLink? guardian, CommandRouter router, HotkeyBinder hotkeys,
        FeedBroker feeds, SimulatedMonitors monitors, ShellEvents shellEvents)
    {
        _feeds = feeds;
        _monitors = monitors;
        _shellEvents = shellEvents;
        _router = router;
        _hotkeys = hotkeys;
        _watchdog = watchdog;
        _guardian = guardian;
        _supervisor = supervisor;
        _launcher = launcher;
        _catalog = catalog;
        _directory = directory;
        _app = app;
        _options = options;
        _locations = locations;
        _theme = theme;
        _surfaces = surfaces;
        _dispatcher = dispatcher;
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

    /// <summary>
    /// A desktop-change checkpoint for tests: when <c>hold-&lt;name&gt;</c> exists the host writes
    /// <c>held-&lt;name&gt;</c> and waits (up to a minute) until the test deletes the hold file.
    /// </summary>
    public static Func<string, Task> Checkpoint(string directory) => async name =>
    {
        var hold = Path.Combine(directory, "hold-" + name);
        if (!File.Exists(hold))
        {
            return;
        }

        await File.WriteAllTextAsync(Path.Combine(directory, "held-" + name), string.Empty);
        var deadline = DateTime.UtcNow.AddMinutes(1);
        while (File.Exists(hold) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(50);
        }
    };

    private void Tick()
    {
        // Request files are read off the UI thread: a file the test has just written may be held by an
        // antivirus scan, and that wait must not count as a UI stall. Only the action runs on the UI thread.
        if (Interlocked.Exchange(ref _reading, 1) == 0)
        {
            _ = Task.Run(ReadRequests);
        }

        WatchPointers();

        // The snapshot reads UI objects here; the file is written off the UI thread so a slow disk
        // (an antivirus scan of the temp folder) is not counted as a UI stall. A tick that finds the
        // previous write still running skips its own.
        if (Interlocked.Exchange(ref _writing, 1) == 1)
        {
            return;
        }

        var json = Snapshot().ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        _ = Task.Run(() =>
        {
            // The test reads status.json while this replaces it; a refused replace waits for the next tick.
            try
            {
                var temp = Path.Combine(_directory, "status.json.tmp");
                File.WriteAllText(temp, json, Utf8NoBom);
                File.Move(temp, Path.Combine(_directory, "status.json"), overwrite: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
            finally
            {
                Volatile.Write(ref _writing, 0);
            }
        });
    }

    private void ReadRequests()
    {
        try
        {
            if (File.Exists(Path.Combine(_directory, "exit")))
            {
                Post(() =>
                {
                    _timer.Stop();
                    _ = _app.ShutdownAsync();
                });
                return;
            }

            if (Take("hang") is not null)
            {
                Post(() => Thread.Sleep(TimeSpan.FromMinutes(2)));
            }

            if (Take("retry") is { Length: > 0 } retry)
            {
                Post(() => RunRetry(retry[0].Trim()));
            }

            if (Take("preset") is { Length: > 0 } preset)
            {
                Post(() => _ = ApplyPresetAsync(preset[0].Trim()));
            }

            if (Take("invoke") is { Length: > 0 } invoke)
            {
                Post(() => RunInvoke(invoke));
            }

            if (Take("monitors") is { } monitors)
            {
                Post(() => SetMonitors(string.Join('\n', monitors)));
            }

            if (Take("focus-panel") is { Length: > 0 } focus)
            {
                Post(() => _surfaces.FocusPanel(focus[0].Trim()));
            }

            if (Take("settings-ui") is { Length: > 0 } settingsUi)
            {
                Post(() => RunSettingsUi(settingsUi));
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }

    /// <summary>Replaces the simulated monitors with <c>{ "monitors": [{ id, left, top, right, bottom, dpi }] }</c>.</summary>
    private void SetMonitors(string json)
    {
        var monitors = JsonNode.Parse(json)?["monitors"]?.AsArray() ?? [];
        _monitors.Set(monitors.OfType<JsonObject>().Select(m =>
        {
            var bounds = new RectInt((int)m["left"]!, (int)m["top"]!, (int)m["right"]!, (int)m["bottom"]!);
            return new MonitorInfo((string)m["id"]!, 0, bounds, bounds, (uint)(int)m["dpi"]!, false);
        }));
    }

    /// <summary>Reads and deletes a request file, or returns null when there is none.</summary>
    private string[]? Take(string name)
    {
        var file = Path.Combine(_directory, name);
        if (!File.Exists(file))
        {
            return null;
        }

        var lines = File.ReadAllLines(file);
        File.Delete(file);
        return lines;
    }

    // An exception escaping a dispatcher callback ends a WinUI process without UnhandledException.
    private void Post(Action action) => _dispatcher.TryEnqueue(() =>
    {
        try
        {
            action();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    });

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

    private void RunRetry(string pluginId)
    {
        var command = _surfaces.Views.Where(view => view.Instance.Plugin == pluginId)
            .Select(view => view.Placeholder?.RetryCommand).FirstOrDefault(command => command is not null);
        if (command?.CanExecute(null) == true)
        {
            command.Execute(null);
        }
    }

    private void RunInvoke(string[] lines)
    {
        var commandId = lines[0].Trim();
        var source = lines.Length > 1 && Enum.TryParse<InvocationSource>(lines[1].Trim(), ignoreCase: true, out var parsed)
            ? parsed
            : InvocationSource.Keyboard;
        var args = lines.Length > 2 && !string.IsNullOrWhiteSpace(lines[2]) ? JsonNode.Parse(lines[2]) as JsonObject : null;
        _ = InvokeAsync(commandId, args, source);
    }

    /// <summary>One JSON request per line for the settings window, which opens first when it is not yet created.</summary>
    private void RunSettingsUi(string[] lines)
    {
        var window = _app.Settings ?? _app.OpenSettings(null, null);
        var results = new List<string>();
        foreach (var line in lines.Where(line => !string.IsNullOrWhiteSpace(line)))
        {
            try
            {
                results.Add(window.Drive(JsonNode.Parse(line) as JsonObject ?? []) ?? "ok");
            }
            catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException or FormatException)
            {
                results.Add($"{e.GetType().Name}: {e.Message}");
            }
        }

        _lastSettingsUi = string.Join(" | ", results);
        _settingsUiRuns++;
    }

    private async Task InvokeAsync(string commandId, JsonObject? args, InvocationSource source)
    {
        try
        {
            var outcome = await _router.InvokeAsync(commandId, args, source, null, CancellationToken.None);
            _lastInvoke = new JsonObject { ["command"] = commandId, ["ok"] = outcome.Ok, ["code"] = outcome.Error?.Code.ToString(), ["detail"] = outcome.Error?.Detail,
                ["retryable"] = outcome.Error?.Retryable };
        }
        catch (Exception e)
        {
            _lastInvoke = new JsonObject { ["command"] = commandId, ["ok"] = false, ["code"] = e.GetType().Name };
        }
    }

    private async Task ApplyPresetAsync(string presetId)
    {
        try
        {
            var outcome = await _app.ApplyPresetAsync(presetId, CancellationToken.None);
            _lastPresetApply = new JsonObject { ["preset"] = presetId, ["ok"] = outcome.Ok, ["code"] = outcome.Error?.Code.ToString() };
        }
        catch (Exception e)
        {
            _lastPresetApply = new JsonObject { ["preset"] = presetId, ["ok"] = false, ["code"] = e.GetType().Name };
        }
    }

    private JsonObject Snapshot()
    {
        var panels = new JsonArray();
        // Ready once every template has been applied, so tests never read a half-built tree.
        var ready = _surfaces.Panels.Count > 0 || _surfaces.LayoutApplied;
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
                ["focused"] = window.Panel.XamlRoot is { } xamlRoot
                    && FocusManager.GetFocusedElement(xamlRoot) is FrameworkElement focused && IsDescendant(focused, window.Panel)
                    ? focused.Name
                    : null,
                ["id"] = window.Definition.Id.ToString(),
                ["handle"] = (long)window.Handle,
                ["monitor"] = window.Monitor.StableId,
                ["primary"] = window.Monitor.IsPrimary,
                ["topmost"] = window.IsTopmost,
                ["wantsTopmost"] = window.WantsTopmost,
                ["collapsed"] = window.Collapsed,
                ["scale"] = window.Monitor.Scale,
                ["edge"] = window.Panel.Edge,
                ["behaviour"] = window.Panel.Behaviour,
                ["thicknessPx"] = window.ThicknessPx,
                ["bounds"] = Rect(window.Bounds),
                ["appBarRect"] = window.AppBarRect is { } granted ? Rect(granted) : null,
                ["reserveError"] = window.ReserveError,
                ["background"] = TemplateBackground(window.Panel),
                ["placeholders"] = placeholders,
            });
        }

        return new JsonObject
        {
            ["ready"] = ready,
            ["hostPid"] = Environment.ProcessId,
            ["args"] = new JsonArray(_options.Args.Select(a => (JsonNode?)a).ToArray()),
            ["guardianPid"] = _guardian?.GuardianPid ?? _options.GuardianPid,
            ["guardianPipe"] = _guardian?.PipeName ?? _options.GuardianPipe,
            ["dataRoot"] = _locations.DataRoot,
            ["contentRoot"] = _locations.ContentRoot,
            ["workArea"] = Rect(MonitorService.PrimaryWorkArea()),
            ["shellEventsWindow"] = (long)_shellEvents.Handle,
            ["monitors"] = new JsonArray(_monitors.Monitors.Select(monitor => (JsonNode?)new JsonObject
            {
                ["id"] = monitor.StableId,
                ["primary"] = monitor.IsPrimary,
                ["dpi"] = monitor.Dpi,
                ["bounds"] = Rect(monitor.Bounds),
                ["workArea"] = Rect(monitor.WorkArea),
            }).ToArray()),
            ["theme"] = new JsonObject
            {
                ["id"] = _theme.Current.ThemeId,
                ["source"] = _theme.CurrentSource.ToString(),
                ["backdrop"] = _theme.CurrentBackdrop.ToString(),
                ["palette"] = _theme.CurrentPaletteId,
                ["appliedAtUtcMs"] = _theme.AppliedAtUtcMs,
                ["errors"] = Errors(_app.InitialThemeErrors),
                ["lastApply"] = _theme.LastApply is { } last ? new JsonObject { ["applied"] = last.Applied, ["errors"] = Errors(last.Errors) } : null,
                ["assetPacks"] = new JsonArray(_theme.IconChain.Packs.Select(pack => (JsonNode?)new JsonObject
                {
                    ["id"] = pack.Id,
                    ["label"] = pack.Label,
                    ["health"] = pack.Health.ToString(),
                    ["problems"] = Errors(pack.Problems),
                }).ToArray()),
            },
            ["settings"] = new JsonObject
            {
                ["health"] = _app.SettingsHealth.ToString(),
                ["activePreset"] = _app.ActivePresetId,
            },
            ["lastPresetApply"] = _lastPresetApply?.DeepClone(),
            ["lastInvoke"] = _lastInvoke?.DeepClone(),
            ["dispatches"] = new JsonArray(_router.Dispatches.Select(line => (JsonNode?)line).ToArray()),
            ["commands"] = new JsonArray(_router.Commands.Select(command => (JsonNode?)new JsonObject
            {
                ["id"] = command.CommandId,
                ["provider"] = command.Provider,
                ["available"] = command.Available,
            }).ToArray()),
            ["hotkeys"] = new JsonArray(_hotkeys.Bindings.Select(binding => (JsonNode?)new JsonObject
            {
                ["command"] = binding.CommandId,
                ["chord"] = binding.Chord,
                ["state"] = binding.State,
            }).ToArray()),
            ["feeds"] = new JsonArray(_feeds.Feeds.Select(feed => (JsonNode?)new JsonObject
            {
                ["id"] = feed.FeedId,
                ["provider"] = feed.Provider,
                ["available"] = feed.Available,
            }).ToArray()),
            ["workerMessages"] = WorkerChannel.MessagesSent,
            ["settingsWindow"] = _app.Settings?.Snapshot(),
            ["lastSettingsUi"] = _lastSettingsUi,
            ["settingsUiRuns"] = _settingsUiRuns,
            ["panels"] = panels,
            ["fullscreenMonitors"] = new JsonArray(_surfaces.FullscreenMonitors.Order(StringComparer.Ordinal).Select(id => (JsonNode?)id).ToArray()),
            ["contributions"] = new JsonArray(_surfaces.Views.Select(view => (JsonNode?)new JsonObject
            {
                ["instanceId"] = view.Instance.Id.ToString("D"),
                ["plugin"] = view.Instance.Plugin,
                ["reason"] = view.Reason,
                ["hasRetry"] = view.Placeholder?.RetryCommand is not null,
                ["texts"] = new JsonArray(view.Texts().Select(text => (JsonNode?)text).ToArray()),
                ["textItems"] = new JsonArray(view.TextElements().Select(item => (JsonNode?)new JsonObject
                {
                    ["text"] = item.Element.Text,
                    ["style"] = item.StyleKey,
                    ["foreground"] = TemplateForeground(item.Element),
                    ["fontSize"] = FirstTextBlock(item.Element)?.FontSize,
                }).ToArray()),
                ["bounds"] = ScreenBounds(view.Root),
                ["interactive"] = new JsonArray(view.InteractiveElements().Select(element => (JsonNode?)new JsonObject
                {
                    ["id"] = element.Name,
                    ["kind"] = element is WmToggle ? "toggle" : "button",
                    ["label"] = element switch { WmButton b => b.Label, WmToggle t => t.Label, _ => null },
                    ["checked"] = (element as WmToggle)?.Checked,
                    ["active"] = (element as WmButton)?.Active,
                    ["activeState"] = element is WmButton ? CurrentState(element, "ActiveStates") : null,
                    ["itemKey"] = view.InfoOf(element)?.ItemKey,
                    ["iconSource"] = view.InfoOf(element)?.IconSource,
                    ["hasIcon"] = element switch { WmButton b => b.Icon is not null, WmToggle t => t.Icon is not null, _ => false },
                    ["bounds"] = ScreenBounds(element),
                }).ToArray()),
                ["notices"] = new JsonArray(view.Notices().Select(notice => (JsonNode?)new JsonObject
                {
                    ["element"] = notice.ElementId,
                    ["code"] = notice.Code.ToString(),
                    ["message"] = notice.Message,
                }).ToArray()),
            }).ToArray()),
            ["popups"] = new JsonArray(_surfaces.Popups.Select(popup => (JsonNode?)new JsonObject
            {
                ["plugin"] = popup.PluginId,
                ["popup"] = popup.PopupId,
                ["open"] = popup.IsOpen,
                ["hwnd"] = (long)popup.Handle,
                ["bounds"] = popup.IsOpen ? Rect(popup.Bounds) : null,
                ["readyAtUtcMs"] = popup.ReadyAtUtcMs,
                ["lastCloseReason"] = popup.LastCloseReason?.ToString(),
                ["focusedInput"] = popup.Frame.XamlRoot is { } root && FocusManager.GetFocusedElement(root) is WmTextInput focused
                    ? focused.Name
                    : null,
                ["inputs"] = new JsonArray(popup.View.TextInputs().Select(input => (JsonNode?)new JsonObject
                {
                    ["id"] = input.Name,
                    ["text"] = input.Text,
                    ["editedAtUtcMs"] = input.EditedAtUtcMs,
                }).ToArray()),
                ["texts"] = new JsonArray(popup.View.Texts().Select(text => (JsonNode?)text).ToArray()),
                ["selectedKey"] = popup.View.SelectedKey("results"),
                ["items"] = new JsonArray(popup.View.InteractiveElements().Select(element => (JsonNode?)new JsonObject
                {
                    ["id"] = element.Name,
                    ["label"] = (element as WmButton)?.Label,
                    ["itemKey"] = popup.View.InfoOf(element)?.ItemKey,
                    ["selected"] = (element as WmButton)?.Selected,
                    ["bounds"] = ScreenBounds(element),
                }).ToArray()),
                ["notices"] = new JsonArray(popup.View.Notices().Select(notice => (JsonNode?)new JsonObject
                {
                    ["element"] = notice.ElementId,
                    ["code"] = notice.Code.ToString(),
                    ["message"] = notice.Message,
                }).ToArray()),
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

    /// <summary>The element's bounds in physical screen pixels, or null before it is laid out.</summary>
    private JsonObject? ScreenBounds(FrameworkElement element)
    {
        RectInt? bounds = _surfaces.Panels.FirstOrDefault(panel => IsDescendant(element, panel.Panel))?.Bounds
            ?? _surfaces.Popups.FirstOrDefault(popup => popup.IsOpen && IsDescendant(element, popup.Frame))?.Bounds;
        if (bounds is not { } window || element.ActualWidth <= 0 || element.XamlRoot is not { } root)
        {
            return null;
        }

        var origin = element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
        var scale = root.RasterizationScale;
        var left = window.Left + (int)Math.Round(origin.X * scale);
        var top = window.Top + (int)Math.Round(origin.Y * scale);
        return Rect(new RectInt(left, top, left + (int)Math.Round(element.ActualWidth * scale), top + (int)Math.Round(element.ActualHeight * scale)));
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

    private static JsonArray Errors(IEnumerable<ThemeError> errors) => new(errors.Select(e => (JsonNode?)new JsonObject
    {
        ["code"] = e.Code.ToString(),
        ["file"] = e.File,
        ["key"] = e.Key,
        ["detail"] = e.Detail,
    }).ToArray());

    /// <summary>The colour a Text element is drawn in, read from its template's TextBlock, as <c>#AARRGGBB</c>.</summary>
    private static string? TemplateForeground(Control control) =>
        FirstTextBlock(control)?.Foreground is SolidColorBrush solid ? Hex(solid.Color) : null;

    private static TextBlock? FirstTextBlock(DependencyObject parent)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as TextBlock ?? FirstTextBlock(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    private static string Hex(Windows.UI.Color color) => $"#{color.A:X2}{color.R:X2}{color.G:X2}{color.B:X2}";

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
        return brush is SolidColorBrush solid ? Hex(solid.Color) : null;
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

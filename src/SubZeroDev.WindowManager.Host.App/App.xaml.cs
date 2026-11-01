using System.Diagnostics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Host.Surfaces;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Recovery;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.App;

public sealed partial class App : Application
{
    private readonly HostOptions _options;
    private Locations? _locations;
    private HostLog? _log;
    private ThemeEngine? _theme;
    private SurfaceManager? _surfaces;
    private ConfigStore? _config;
    private PackageCatalog? _catalog;
    private PluginLogs? _pluginLogs;
    private WorkerLauncher? _launcher;
    private PluginSupervisor? _supervisor;
    private ContributionHost? _contributions;
    private string _layoutId = HostSettingsDocument.DefaultPreset;
    private TestHook? _testHook;
    private DispatcherWatchdog? _watchdog;
    private GuardianLink? _guardian;
    private RecoveryJournal? _journal;
    private int _exiting;

    public App(HostOptions options)
    {
        _options = options;
        InitializeComponent();

        // A layout edit closes every panel before opening the new ones; the host ends only through Exit().
        DispatcherShutdownMode = DispatcherShutdownMode.OnExplicitShutdown;
        UnhandledException += (_, e) =>
        {
            _log?.WriteNow(LogLevel.Error, "host", $"Unhandled: {e.Exception}");
            e.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            _log?.WriteNow(LogLevel.Error, "host", $"Unhandled off the UI thread: {e.ExceptionObject}");
    }

    public static int ExitCode { get; private set; } = ExitCodes.Normal;

    /// <summary>Theme errors from the preset's own theme, before any fallback.</summary>
    public IReadOnlyList<ThemeError> InitialThemeErrors { get; private set; } = [];

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            await StartAsync();
        }
        catch (Exception e)
        {
            _log?.Write(LogLevel.Error, "host", $"Startup failed: {e}");
            ExitCode = ExitCodes.StartupFailed;
            _testHook?.Stop();
            try
            {
                await StopAsync();
            }
            catch (Exception cleanup)
            {
                _log?.Write(LogLevel.Error, "host", $"Cleanup after the failed start failed: {cleanup}");
            }

            if (_log is not null)
            {
                await _log.DisposeAsync();
            }

            Exit();
        }
    }

    private async Task StartAsync()
    {
        _locations = Locations.Resolve(AppContext.BaseDirectory);
        _log = new HostLog(_locations.LogsDirectory);
        _log.Write(LogLevel.Info, "host", $"Starting pid {Environment.ProcessId}, guardian {_options.GuardianPid?.ToString() ?? "none"}.");

        Directory.CreateDirectory(Path.Combine(_locations.DataRoot, "layouts"));
        Directory.CreateDirectory(Path.Combine(_locations.DataRoot, "plugins"));
        var config = _config = new ConfigStore(_locations);
        var settings = await LoadSettingsAsync(config, _log);
        var preset = LoadPreset(config, settings.ActivePreset, _log);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _guardian = GuardianLink.Start(_options, dispatcher, _log, settings.RestartPolicy);
        var log = _log;
        using var self = Process.GetCurrentProcess();
        Recoverer.Recover(_locations.DataRoot, self.SessionId, "startup", hostExited: false, exitCode: null,
            line => log.Write(LogLevel.Warning, "recovery", line));
        _journal = new RecoveryJournal(Journal.PathForSession(_locations.DataRoot, self.SessionId), Environment.ProcessId,
            new DateTimeOffset(self.StartTime));
        var started = await _journal.StartAsync(CancellationToken.None);
        if (!started.Ok)
        {
            _log.Write(LogLevel.Error, "recovery", $"Could not start the recovery journal: {started.Error}");
        }

        var hookDirectory = Environment.GetEnvironmentVariable(TestHook.Variable);
        _theme = new ThemeEngine(_locations, _log, dispatcher);
        var appearance = new AppearanceSelection(preset.Theme,
            new PaletteSelection(preset.Palette, preset.PaletteLight, preset.PaletteDark), preset.AssetPacks);
        var applied = await _theme.ApplyAsync(appearance, CancellationToken.None);
        if (!applied.Applied)
        {
            InitialThemeErrors = applied.Errors;
            foreach (var error in applied.Errors)
            {
                _log.Write(LogLevel.Error, "theme", error.ToString());
            }

            await _theme.ApplyFallbackAsync(CancellationToken.None);
        }

        _catalog = new PackageCatalog(_locations, _log);
        _pluginLogs = new PluginLogs(_locations.LogsDirectory);
        _launcher = new WorkerLauncher();
        var hostVersion = typeof(App).Assembly.GetName().Version is { } version ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.1.0";
        _supervisor = new PluginSupervisor(_catalog, _launcher, _pluginLogs, _log, _locations, HostMode.Coexistence, hostVersion,
            settings.RestartPolicy);
        _contributions = new ContributionHost(_supervisor, _catalog, _pluginLogs, _log);

        var monitors = new MonitorService();
        var checkpoint = string.IsNullOrWhiteSpace(hookDirectory) ? null : TestHook.Checkpoint(hookDirectory);
        _surfaces = new SurfaceManager(config, monitors, new AppBarService(monitors, _journal, checkpoint), _theme, _contributions, _log, Retry);
        _layoutId = preset.Layout;
        await ApplyLayoutAsync();

        // A hand edit to the active layout is applied as soon as the file settles.
        config.Changed += (_, e) =>
        {
            if (e.Origin == ChangeOrigin.External && e.Id == new DocumentId(DocumentKind.Layout, _layoutId))
            {
                dispatcher.TryEnqueue(() => _ = ReapplyLayoutAsync());
            }
        };
        config.StartWatching();

        var debug = false;
#if DEBUG
        debug = true;
#endif
        if (debug || !string.IsNullOrWhiteSpace(hookDirectory))
        {
            _watchdog = new DispatcherWatchdog(dispatcher, _log);
        }

        if (!string.IsNullOrWhiteSpace(hookDirectory))
        {
            _testHook = new TestHook(hookDirectory, this, _options, _locations, _theme, _surfaces, _supervisor, _launcher, _catalog, dispatcher,
                _watchdog!, _guardian);
            _testHook.Start();
        }
    }

    /// <summary>A placeholder's Retry: runs on the UI thread, so the restart itself goes to the background.</summary>
    private void Retry(string pluginId)
    {
        if (_supervisor is not { } supervisor)
        {
            return;
        }

        _log?.Write(LogLevel.Info, "plugins", $"Retry requested for {pluginId}.");
        _ = Task.Run(async () =>
        {
            try
            {
                var outcome = await supervisor.RetryAsync(pluginId, CancellationToken.None);
                if (!outcome.Ok)
                {
                    _log?.Write(LogLevel.Warning, "plugins", $"Retrying {pluginId} failed: {outcome.Error}");
                }
            }
            catch (Exception e)
            {
                _log?.Write(LogLevel.Error, "plugins", $"Retrying {pluginId} failed: {e}");
            }
        });
    }

    private async Task ApplyLayoutAsync()
    {
        await _surfaces!.ApplyLayoutAsync(LoadLayout(_config!, _layoutId, _log!), CancellationToken.None);
        foreach (var panel in _surfaces.Panels)
        {
            var exit = new MenuFlyoutItem { Text = "Exit SubZeroDev.WindowManager" };
            exit.Click += (_, _) => _ = ShutdownAsync();
            var menu = new MenuFlyout();
            menu.Items.Add(exit);
            panel.Panel.ContextFlyout = menu;
        }
    }

    private async Task ReapplyLayoutAsync()
    {
        if (Volatile.Read(ref _exiting) == 1)
        {
            return;
        }

        try
        {
            _log?.Write(LogLevel.Info, "host", $"Layout '{_layoutId}' changed on disk; applying it.");
            await ApplyLayoutAsync();
        }
        catch (Exception e)
        {
            _log?.Write(LogLevel.Error, "host", $"Applying the changed layout failed: {e}");
        }
    }

    /// <summary>Stops the workers first so none outlives the surfaces it drew on, then closes the panels.</summary>
    private async Task StopAsync()
    {
        _config?.Dispose();
        _catalog?.Dispose();
        if (_contributions is not null)
        {
            await _contributions.DisposeAsync();
        }

        if (_supervisor is not null)
        {
            await _supervisor.StopAllAsync(CancellationToken.None);
        }

        if (_surfaces is not null)
        {
            await _surfaces.ShutdownAsync(CancellationToken.None);
        }

        if (_pluginLogs is not null)
        {
            await _pluginLogs.DisposeAsync();
        }

        _launcher?.Dispose();
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1)
        {
            return;
        }

        _testHook?.Stop();
        _watchdog?.Dispose();
        await StopAsync();
        if (_guardian is not null)
        {
            await _guardian.DisposeAsync();
        }

        if (_journal is not null && (await _journal.MarkCleanExitAsync(CancellationToken.None)).Error is { } journalError)
        {
            _log?.Write(LogLevel.Error, "recovery", $"Could not mark the clean exit: {journalError}");
        }

        _log?.Write(LogLevel.Info, "host", "Stopped.");
        if (_log is not null)
        {
            await _log.DisposeAsync();
        }

        Exit();
    }

    /// <summary>A missing file is created with defaults; an invalid one is used as defaults and left alone (I14).</summary>
    private static async Task<HostSettingsDocument> LoadSettingsAsync(ConfigStore config, IHostLog log)
    {
        var state = config.Get(DocumentId.HostSettings);
        switch (state.Health)
        {
            case DocumentHealth.Missing:
                var written = await config.WriteAsync(DocumentId.HostSettings, null, HostSettingsDocument.Default.ToJson(), CancellationToken.None);
                if (!written.Ok)
                {
                    log.Write(LogLevel.Warning, "config", $"Could not write default settings: {written.Error}");
                }

                return HostSettingsDocument.Default;
            case DocumentHealth.Valid:
                var problems = new List<string>();
                var parsed = Documents.ParseHostSettings(state.Content!, problems);
                if (parsed is not null)
                {
                    return parsed;
                }

                log.Write(LogLevel.Error, "config", $"settings.json: {string.Join("; ", problems)}");
                return HostSettingsDocument.Default;
            default:
                log.Write(LogLevel.Error, "config", $"settings.json is {state.Health}: {state.LastError?.Detail}");
                return HostSettingsDocument.Default;
        }
    }

    private static PresetDocument LoadPreset(ConfigStore config, string presetId, IHostLog log)
    {
        foreach (var id in presetId == HostSettingsDocument.DefaultPreset ? [presetId] : new[] { presetId, HostSettingsDocument.DefaultPreset })
        {
            var state = config.Get(new DocumentId(DocumentKind.Preset, id));
            var problems = new List<string>();
            var preset = state is { Health: DocumentHealth.Valid, Content: not null } ? Documents.ParsePreset(state.Content, id, problems) : null;
            if (preset is not null)
            {
                return preset;
            }

            log.Write(LogLevel.Error, "config", $"Preset '{id}' is {state.Health}: {state.LastError?.Detail} {string.Join("; ", problems)}");
        }

        return new PresetDocument(HostSettingsDocument.DefaultPreset, "SubZeroDev default", HostSettingsDocument.DefaultPreset,
            ThemePackageReader.DefaultThemeId, null, null, null, []);
    }

    private static LayoutDocument LoadLayout(ConfigStore config, string layoutId, IHostLog log)
    {
        var state = config.Get(new DocumentId(DocumentKind.Layout, layoutId));
        var problems = new List<string>();
        var layout = state is { Health: DocumentHealth.Valid, Content: not null } ? Documents.ParseLayout(state.Content, layoutId, problems) : null;
        if (layout is not null)
        {
            return layout;
        }

        log.Write(LogLevel.Error, "config", $"Layout '{layoutId}' is {state.Health}: {state.LastError?.Detail} {string.Join("; ", problems)}");
        var empty = new Dictionary<string, IReadOnlyList<ContributionInstance>>();
        return new LayoutDocument(layoutId, "Fallback", [
            new PanelDefinition(Guid.Empty, MonitorSelector.Primary, new PanelPlacement(PanelEdge.Bottom, null, "start", null), 40,
                PanelBehaviour.Reserve, empty),
        ]);
    }
}

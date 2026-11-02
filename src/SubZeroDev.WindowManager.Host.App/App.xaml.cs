using System.Diagnostics;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Host.Settings;
using SubZeroDev.WindowManager.Host.Surfaces;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Recovery;
using CommandOutcome = SubZeroDev.WindowManager.Host.Core.Outcome<SubZeroDev.WindowManager.Protocol.CommandResult>;
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
    private SettingsService? _settingsService;
    private WorkerLauncher? _launcher;
    private PluginSupervisor? _supervisor;
    private ContributionHost? _contributions;
    private FeedBroker? _feeds;
    private CommandRouter? _router;
    private HotkeyService? _hotkeyService;
    private HotkeyBinder? _hotkeys;
    private string _layoutId = HostSettingsDocument.DefaultPreset;
    private TestHook? _testHook;
    private SettingsWindow? _settingsWindow;
    private DispatcherWatchdog? _watchdog;
    private GuardianLink? _guardian;
    private RecoveryJournal? _journal;
    private ShellEvents? _shellEvents;
    private MonitorService? _monitorService;
    private IAppBarService? _appBarService;
    private ShellRoleService? _shellRoles;
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

        foreach (var folder in new[] { "layouts", "plugins", "themes", "palettes", "assetpacks" })
        {
            Directory.CreateDirectory(Path.Combine(_locations.DataRoot, folder));
        }

        var config = _config = new ConfigStore(_locations);
        var settings = _settings = await LoadSettingsAsync(config, _log);
        var preset = _preset = LoadPreset(config, settings.ActivePreset, _log);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
        _guardian = GuardianLink.Start(_options, dispatcher, _log, settings.RestartPolicy);
        var log = _log;
        var hookDirectory = Environment.GetEnvironmentVariable(TestHook.Variable);
        var hooked = string.IsNullOrWhiteSpace(hookDirectory) ? null : hookDirectory;
        using var self = Process.GetCurrentProcess();
        Recoverer.Recover(_locations.DataRoot, self.SessionId, "startup", hostExited: false, exitCode: null,
            line => log.Write(LogLevel.Warning, "recovery", line), Reverter.ForHook(hooked));
        _journal = new RecoveryJournal(Journal.PathForSession(_locations.DataRoot, self.SessionId), Environment.ProcessId,
            new DateTimeOffset(self.StartTime));
        var started = await _journal.StartAsync(CancellationToken.None);
        if (!started.Ok)
        {
            _log.Write(LogLevel.Error, "recovery", $"Could not start the recovery journal: {started.Error}");
        }

        var appMode = _appMode = CreateAppModeWatcher(hookDirectory);
        _theme = new ThemeEngine(_locations, _log, dispatcher, () => appMode.IsDark);
        var applied = await _theme.ApplyAsync(AppearanceOf(preset), CancellationToken.None);
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
        _supervisor = new PluginSupervisor(_catalog, _launcher, _pluginLogs, _log, _locations, _options.Mode, hostVersion,
            settings.RestartPolicy);
        _catalog.StopWorkerAsync = _supervisor.StopAsync;
        var pluginSettings = new PluginSettingsStore(config, _locations, _pluginLogs);
        _supervisor.PluginSettings = pluginSettings;
        _supervisor.Enablement = new PluginEnablement(config, _catalog);
        var settingsService = _settingsService = new SettingsService(_catalog, _supervisor, config, pluginSettings, () => _layoutId);
        _feeds = new FeedBroker(_supervisor, _catalog, _pluginLogs);
        _supervisor.WorkerNotifications = _feeds.OnNotification;
        var foreground = new ForegroundService();
        _contributions = new ContributionHost(_supervisor, _catalog, _pluginLogs, _log, foreground);
        _router = new CommandRouter(_catalog, _supervisor, foreground, config, _log);
        _supervisor.WorkerRequests = (pluginId, request) => request.Method switch
        {
            Methods.PopupOpen or Methods.PopupClose => _surfaces!.ServeWorkerAsync(pluginId, request),
            Methods.SettingsSave => settingsService.ServeWorkerAsync(pluginId, request),
            _ => _router.ServeWorkerAsync(pluginId, request),
        };
        AddBuiltIns(_router, foreground, dispatcher);
        _supervisor.StatusChanged += (_, _) => dispatcher.TryEnqueue(RefreshSettingsWindow);
        _catalog.Changed += (_, _) => dispatcher.TryEnqueue(RefreshSettingsWindow);

        _shellEvents = new ShellEvents();
        _monitorService = new MonitorService(_shellEvents);
        // Tests add simulated monitors through the hook; the real display configuration is never touched.
        var simulated = string.IsNullOrWhiteSpace(hookDirectory) ? null : new SimulatedMonitors(_monitorService);
        IMonitorService monitors = simulated is null ? _monitorService : simulated;
        var checkpoint = string.IsNullOrWhiteSpace(hookDirectory) ? null : TestHook.Checkpoint(hookDirectory);
        var replacement = _options.Mode == HostMode.Replacement;
        if (replacement)
        {
            // As the shell there is no taskbar to negotiate with: the host owns the work area itself.
            _shellRoles = ShellRoleService.Start(hooked, line => log.Write(LogLevel.Warning, "shell", line));
            _appBarService = new WorkAreaAppBarService(monitors, _journal, WorkAreaAppBarService.Applier(hooked), checkpoint, _shellEvents);
        }
        else
        {
            _appBarService = new AppBarService(monitors, _journal, checkpoint, _shellEvents);
        }

        _surfaces = new SurfaceManager(config, monitors, _appBarService, _theme, _contributions, _log, Retry,
            new ElementActions(_router, _contributions), foreground, new IconResolver(new IconService(), () => _theme!.IconChain));
        _surfaces.PanelCreated += (_, panel) => panel.Panel.ContextFlyout = PanelMenu();
        _layoutId = preset.Layout;
        await ApplyLayoutAsync();

        if (replacement)
        {
            // The panels are up: the welcome screen can go, and start-up programs run once per session.
            _shellRoles!.SignalDesktopReady();
            _shellRoles.HideMinimizedWindows();
            Action<string> startupLog = line => log.Write(LogLevel.Info, "startup", line);
            _ = Task.Run(() =>
            {
                try
                {
                    new StartupRunner(StartupSources.FromEnvironment(hooked), StartupRunner.Launcher(hooked, startupLog), startupLog).Run();
                }
                catch (Exception e)
                {
                    log.Write(LogLevel.Error, "startup", $"Start-up programs failed: {e}");
                }
            });
        }

        _hotkeyService = new HotkeyService();
        _hotkeys = new HotkeyBinder(_router, _hotkeyService, config, _log);
        _hotkeys.Refresh();

        // Settings, the active preset and the active layout apply as soon as an edit settles.
        config.Changed += (_, e) => dispatcher.TryEnqueue(() => OnConfigChanged(e));
        config.StartWatching();
        StartAppearanceWatching(dispatcher);

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
                _watchdog!, _guardian, _router, _hotkeys, _feeds, simulated!, _shellEvents) { ShellRoles = _shellRoles };
            _testHook.Start();
        }
    }

    /// <summary>The host's own commands that S6 serves; the others arrive with the slices that need them.</summary>
    private void AddBuiltIns(CommandRouter router, ForegroundService foreground, DispatcherQueue dispatcher)
    {
        router.AddBuiltIn("host.exit", "Exit", (_, _) =>
        {
            dispatcher.TryEnqueue(() => _ = ShutdownAsync());
            return Task.FromResult(CommandOutcome.Success(new CommandResult(null)));
        });
        router.AddBuiltIn("host.shell.restoreExplorer", "Restore Explorer", (args, ct) =>
        {
            if (_options.Mode != HostMode.Replacement)
            {
                return Task.FromResult(CommandOutcome.Fail(CoreErrorCode.NotInReplacementMode,
                    "host.shell.restoreExplorer only works when the app is the shell."));
            }

            // The guardian reads this after the normal exit: it unregisters if asked, then starts Explorer.
            var unregister = args?["unregister"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
            try
            {
                var marker = Path.Combine(_locations!.DataRoot, GuardianNames.RestoreExplorerFile);
                File.WriteAllText(marker, new JsonObject { ["unregister"] = unregister }.ToJsonString() + "\n");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return Task.FromResult(CommandOutcome.Fail(CoreErrorCode.IoFailure, $"Could not ask the guardian to restore Explorer: {e.Message}"));
            }

            dispatcher.TryEnqueue(() => _ = ShutdownAsync());
            return Task.FromResult(CommandOutcome.Success(new CommandResult(null)));
        });
        router.AddBuiltIn("host.files.open", "Open folder", (args, _) =>
        {
            if (args?["path"] is not JsonValue value || !value.TryGetValue<string>(out var path) || path.Length == 0)
            {
                return Task.FromResult(CommandOutcome.Fail(CoreErrorCode.CommandFailed, "host.files.open needs { path: string }."));
            }

            return Task.FromResult(OpenFolder(path));
        });
        router.AddBuiltIn("host.window.activate", "Activate window", (args, ct) => WindowCommandAsync(args, foreground.ActivateWindowAsync, ct));
        router.AddBuiltIn("host.window.minimize", "Minimise window", (args, ct) => WindowCommandAsync(args, foreground.MinimizeWindowAsync, ct));
        router.AddBuiltIn("host.preset.apply", "Apply preset", async (args, ct) =>
        {
            if (args?["id"] is not JsonValue value || !value.TryGetValue<string>(out var id))
            {
                return CommandOutcome.Fail(CoreErrorCode.CommandFailed, "host.preset.apply needs { id: string }.");
            }

            var applied = await ApplyPresetAsync(id, ct);
            return applied.Ok ? CommandOutcome.Success(new CommandResult(null)) : CommandOutcome.Fail(applied.Error!);
        });
        router.AddBuiltIn("host.settings.open", "Open settings", (args, _) =>
        {
            var page = args?["page"] is JsonValue p && p.TryGetValue<string>(out var text) ? text : null;
            var pluginId = args?["pluginId"] is JsonValue v && v.TryGetValue<string>(out var id) ? id : null;
            return Task.FromResult(dispatcher.TryEnqueue(() => OpenSettings(page, pluginId))
                ? CommandOutcome.Success(new CommandResult(null))
                : CommandOutcome.Fail(CoreErrorCode.CommandFailed, "The host is shutting down."));
        });
    }

    /// <summary>
    /// Opens a folder in a file manager window. Explorer started with a path opens just that window and never
    /// takes the shell roles, so this works when the app is the shell. Under the test hook it is recorded in
    /// <c>desktop-actions.log</c> instead.
    /// </summary>
    private static CommandOutcome OpenFolder(string path)
    {
        if (Environment.GetEnvironmentVariable(TestHook.Variable) is { Length: > 0 } hook)
        {
            Directory.CreateDirectory(hook);
            File.AppendAllText(Path.Combine(hook, Reverter.DesktopActionsLog), $"files-open {path}\n", new System.Text.UTF8Encoding(false));
            return CommandOutcome.Success(new CommandResult(null));
        }

        var expanded = Environment.ExpandEnvironmentVariables(path);
        if (!Directory.Exists(expanded))
        {
            return CommandOutcome.Fail(CoreErrorCode.CommandFailed, $"The folder '{path}' does not exist.");
        }

        try
        {
            using var process = Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { expanded }, UseShellExecute = false });
            return CommandOutcome.Success(new CommandResult(null));
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return CommandOutcome.Fail(CoreErrorCode.CommandFailed, $"Could not open '{path}': {e.Message}");
        }
    }

    /// <summary>Opens the settings window, creating it the first time. Call on the UI thread.</summary>
    public SettingsWindow OpenSettings(string? page, string? pluginId)
    {
        _settingsWindow ??= new SettingsWindow(new SettingsServices(_catalog!, _supervisor!, _pluginLogs!, _settingsService!,
            PresetNames, () => ActivePresetId, ApplyPresetAsync, ReapplyLayoutAsync, Path.Combine(_locations!.DataRoot, "plugin-data"),
            ShellRegistered, SetShellAsync));
        _settingsWindow.Open(page, pluginId);
        return _settingsWindow;
    }

    public SettingsWindow? Settings => _settingsWindow;

    private void RefreshSettingsWindow()
    {
        if (_settingsWindow is { IsOpen: true } window && Volatile.Read(ref _exiting) == 0)
        {
            window.Refresh();
        }
    }

    /// <summary>Every preset that parses, by id, with its display name.</summary>
    private IReadOnlyList<(string Id, string Name)> PresetNames() =>
        [.. _config!.List(DocumentKind.Preset).Select(id => _config.Get(id)).Where(state => state is { Health: DocumentHealth.Valid, Content: not null })
            .Select(state => Documents.ParsePreset(state.Content!, state.Id.Id, []))
            .OfType<PresetDocument>()
            .OrderBy(preset => preset.Id, StringComparer.Ordinal)
            .Select(preset => (preset.Id, preset.DisplayName))];

    private static async Task<CommandOutcome> WindowCommandAsync(JsonObject? args, Func<nint, CancellationToken, Task<Core.Outcome>> act,
        CancellationToken ct)
    {
        if (args?["hwnd"] is not JsonValue value || !value.TryGetValue<long>(out var hwnd))
        {
            return CommandOutcome.Fail(CoreErrorCode.CommandFailed, "Needs { hwnd: number }.");
        }

        var outcome = await act((nint)hwnd, ct).ConfigureAwait(false);
        return outcome.Ok ? CommandOutcome.Success(new CommandResult(null)) : CommandOutcome.Fail(outcome.Error!);
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

    private Task ApplyLayoutAsync() =>
        _surfaces!.ApplyLayoutAsync(LoadLayout(_config!, _layoutId, _log!), CancellationToken.None);

    private MenuFlyout PanelMenu()
    {
        var settings = new MenuFlyoutItem { Text = "Settings" };
        settings.Click += (_, _) => OpenSettings(null, null);
        var exit = new MenuFlyoutItem { Text = "Exit SubZeroDev.WindowManager" };
        exit.Click += (_, _) => _ = ShutdownAsync();
        var menu = new MenuFlyout();
        menu.Items.Add(settings);
        menu.Items.Add(exit);
        return menu;
    }

    /// <summary>Whether <c>shell.json</c>, which the guardian's <c>--shell</c> writes, says the app is registered as the shell.</summary>
    private bool ShellRegistered()
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(Path.Combine(_locations!.DataRoot, "shell.json")));
            return json?["registered"] is JsonValue value && value.TryGetValue<bool>(out var registered) && registered;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

    /// <summary>The settings switch: runs the guardian's <c>--shell on|off</c>, which needs no elevation.</summary>
    private async Task<Core.Outcome> SetShellAsync(bool on, CancellationToken ct)
    {
        var info = new ProcessStartInfo(GuardianLink.FindGuardian(_guardian?.GuardianPid ?? _options.GuardianPid))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
        };
        info.ArgumentList.Add("--shell");
        info.ArgumentList.Add(on ? "on" : "off");
        try
        {
            using var process = Process.Start(info) ?? throw new InvalidOperationException("Process.Start returned null.");
            var error = process.StandardError.ReadToEndAsync(ct);
            var output = process.StandardOutput.ReadToEndAsync(ct);
            await process.WaitForExitAsync(ct);
            await output;
            var text = (await error).Trim();
            return process.ExitCode switch
            {
                0 => Core.Outcome.Success,
                5 => Core.Outcome.Fail(CoreErrorCode.ShellOverridden, text),
                _ => Core.Outcome.Fail(CoreErrorCode.CommandFailed, text.Length > 0 ? text : $"The guardian exited with {process.ExitCode}."),
            };
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return Core.Outcome.Fail(CoreErrorCode.StartFailed, $"Could not run the guardian: {e.Message}");
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
        StopAppearanceWatching();
        _settingsWindow?.Close();
        _hotkeys?.Dispose();
        _hotkeyService?.Dispose();
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

        (_appBarService as IDisposable)?.Dispose();
        _shellRoles?.Dispose();
        _monitorService?.Dispose();
        _shellEvents?.Dispose();

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

    /// <summary>
    /// A missing file is created with defaults. An invalid one is left alone (I14): the host runs on its
    /// last good copy, or on defaults when there is none.
    /// </summary>
    private async Task<HostSettingsDocument> LoadSettingsAsync(ConfigStore config, IHostLog log)
    {
        var state = config.Get(DocumentId.HostSettings);
        _settingsHealth = state.Health;
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
                var lastGood = config.GetLastGood(DocumentId.HostSettings);
                var recovered = lastGood is { Health: DocumentHealth.Valid, Content: not null }
                    ? Documents.ParseHostSettings(lastGood.Content, [])
                    : null;
                log.Write(LogLevel.Warning, "config", recovered is null ? "No last good settings; using defaults." : "Using the last good settings.");
                return recovered ?? HostSettingsDocument.Default;
        }
    }

    private static PresetDocument LoadPreset(ConfigStore config, string presetId, IHostLog log)
    {
        foreach (var id in presetId == HostSettingsDocument.DefaultPreset ? [presetId] : new[] { presetId, HostSettingsDocument.DefaultPreset })
        {
            if (TryLoadPreset(config, id, log) is { } preset)
            {
                return preset;
            }
        }

        return new PresetDocument(HostSettingsDocument.DefaultPreset, "SubZeroDev default", HostSettingsDocument.DefaultPreset,
            ThemePackageReader.DefaultThemeId, null, null, null, []);
    }

    private static PresetDocument? TryLoadPreset(ConfigStore config, string id, IHostLog log)
    {
        var state = config.Get(new DocumentId(DocumentKind.Preset, id));
        var problems = new List<string>();
        var preset = state is { Health: DocumentHealth.Valid, Content: not null } ? Documents.ParsePreset(state.Content, id, problems) : null;
        if (preset is null)
        {
            log.Write(LogLevel.Error, "config", $"Preset '{id}' is {state.Health}: {state.LastError?.Detail} {string.Join("; ", problems)}");
        }

        return preset;
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

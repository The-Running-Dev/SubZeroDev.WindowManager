using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Host.Surfaces;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.App;

public sealed partial class App : Application
{
    private readonly HostOptions _options;
    private Locations? _locations;
    private HostLog? _log;
    private ThemeEngine? _theme;
    private SurfaceManager? _surfaces;
    private TestHook? _testHook;
    private int _exiting;

    public App(HostOptions options)
    {
        _options = options;
        InitializeComponent();
        UnhandledException += (_, e) =>
        {
            _log?.Write(LogLevel.Error, "host", $"Unhandled: {e.Exception}");
            e.Handled = true;
        };
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
            if (_surfaces is not null)
            {
                try
                {
                    await _surfaces.ShutdownAsync(CancellationToken.None);
                }
                catch (Exception cleanup)
                {
                    _log?.Write(LogLevel.Error, "host", $"Cleanup after the failed start failed: {cleanup}");
                }
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

        var config = new ConfigStore(_locations);
        var settings = await LoadSettingsAsync(config, _log);
        var preset = LoadPreset(config, settings.ActivePreset, _log);

        var dispatcher = DispatcherQueue.GetForCurrentThread();
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

        var monitors = new MonitorService();
        _surfaces = new SurfaceManager(config, monitors, new AppBarService(monitors), _theme, _log);
        await _surfaces.ApplyLayoutAsync(LoadLayout(config, preset.Layout, _log), CancellationToken.None);
        foreach (var panel in _surfaces.Panels)
        {
            var exit = new MenuFlyoutItem { Text = "Exit SubZeroDev.WindowManager" };
            exit.Click += (_, _) => _ = ShutdownAsync();
            var menu = new MenuFlyout();
            menu.Items.Add(exit);
            panel.Panel.ContextFlyout = menu;
        }

        var hookDirectory = Environment.GetEnvironmentVariable(TestHook.Variable);
        if (!string.IsNullOrWhiteSpace(hookDirectory))
        {
            _testHook = new TestHook(hookDirectory, this, _options, _locations, _theme, _surfaces, dispatcher);
            _testHook.Start();
        }
    }

    public async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _exiting, 1) == 1)
        {
            return;
        }

        _testHook?.Stop();
        if (_surfaces is not null)
        {
            await _surfaces.ShutdownAsync(CancellationToken.None);
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

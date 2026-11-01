using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Host.Theming;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.App;

/// <summary>
/// Changing the look while the host runs: a new <c>activePreset</c>, an edited preset, theme or palette
/// file, or a Windows app-mode switch re-applies the appearance. Surfaces restyle from their retained
/// views, so no worker hears about it. A failed apply keeps the previous appearance (I13).
/// </summary>
public sealed partial class App
{
    /// <summary>
    /// The registry key (under HKCU) read for the app mode instead of the Windows one. Honoured only
    /// with the test hook, so tests can switch light and dark without touching the user's setting.
    /// </summary>
    public const string AppModeKeyVariable = "SUBZERODEV_WM_APPMODE_KEY";

    private static readonly TimeSpan AssetSettle = TimeSpan.FromMilliseconds(300);

    private readonly SemaphoreSlim _appearanceGate = new(1, 1);
    private readonly List<FileSystemWatcher> _assetWatchers = [];
    private HostSettingsDocument _settings = HostSettingsDocument.Default;
    private DocumentHealth _settingsHealth = DocumentHealth.Missing;
    private PresetDocument? _preset;
    private AppModeWatcher? _appMode;
    private DispatcherQueueTimer? _assetTimer;

    /// <summary>The preset whose appearance and layout are shown now.</summary>
    public string ActivePresetId => _preset?.Id ?? _settings.ActivePreset;

    /// <summary>The health of <c>settings.json</c> as last read; the host runs on the last good copy when it is not valid.</summary>
    public DocumentHealth SettingsHealth => _settingsHealth;

    /// <summary>
    /// <c>host.preset.apply</c>: writes <c>activePreset</c> to <c>settings.json</c>. The store's change
    /// event then applies the preset, as it would after a hand edit.
    /// </summary>
    public async Task<Outcome> ApplyPresetAsync(string presetId, CancellationToken ct)
    {
        var config = _config ?? throw new InvalidOperationException("The host has not started.");
        if (!Documents.IsPackageId(presetId))
        {
            return Outcome.Fail(CoreErrorCode.NotFound, $"'{presetId}' is not a package id.");
        }

        if (config.Get(new DocumentId(DocumentKind.Preset, presetId)).Health == DocumentHealth.Missing)
        {
            return Outcome.Fail(CoreErrorCode.NotFound, $"Preset '{presetId}' is not installed.");
        }

        var current = config.Get(DocumentId.HostSettings);
        var content = current.Content is { } existing ? (JsonObject)existing.DeepClone() : _settings.ToJson();
        content["activePreset"] = presetId;
        var written = await config.WriteAsync(DocumentId.HostSettings, current.ContentHash, content, ct);
        return written.Ok ? Outcome.Success : new Outcome(written.Error);
    }

    private static AppearanceSelection AppearanceOf(PresetDocument preset) =>
        new(preset.Theme, new PaletteSelection(preset.Palette, preset.PaletteLight, preset.PaletteDark), preset.AssetPacks);

    /// <summary>The app-mode source: the Windows setting, or the test key when the test hook is on.</summary>
    private static AppModeWatcher CreateAppModeWatcher(string? hookDirectory) =>
        new(string.IsNullOrWhiteSpace(hookDirectory) ? null : Environment.GetEnvironmentVariable(AppModeKeyVariable));

    private void StartAppearanceWatching(DispatcherQueue dispatcher)
    {
        _appMode!.Changed += (_, _) => dispatcher.TryEnqueue(() =>
        {
            if (_preset is { PaletteLight: null, PaletteDark: null })
            {
                return;
            }

            _ = ReapplyAppearanceAsync($"Windows switched to {(_appMode.IsDark ? "dark" : "light")} mode");
        });
        _appMode.Start();

        // Theme and palette files are not config documents, so they get their own watchers. Edits
        // arrive in bursts (an editor's save, a folder copy); the apply waits until they settle.
        _assetTimer = dispatcher.CreateTimer();
        _assetTimer.Interval = AssetSettle;
        _assetTimer.IsRepeating = false;
        _assetTimer.Tick += (_, _) => _ = ReapplyAppearanceAsync("A theme or palette file changed");
        foreach (var folder in new[] { "themes", "palettes" })
        {
            var watcher = new FileSystemWatcher(Path.Combine(_locations!.DataRoot, folder))
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            FileSystemEventHandler touched = (_, _) => dispatcher.TryEnqueue(() =>
            {
                _assetTimer.Stop();
                _assetTimer.Start();
            });
            watcher.Changed += touched;
            watcher.Created += touched;
            watcher.Deleted += touched;
            watcher.Renamed += (_, e) => touched(watcher, e);
            watcher.EnableRaisingEvents = true;
            _assetWatchers.Add(watcher);
        }
    }

    private void StopAppearanceWatching()
    {
        foreach (var watcher in _assetWatchers)
        {
            watcher.Dispose();
        }

        _assetWatchers.Clear();
        _assetTimer?.Stop();
        _appMode?.Dispose();
    }

    /// <summary>Runs on the UI thread for every settled change the store reports.</summary>
    private void OnConfigChanged(DocumentChangedEventArgs e)
    {
        if (Volatile.Read(ref _exiting) == 1)
        {
            return;
        }

        _settingsService?.OnDocumentChanged(e);
        if (e.Id == DocumentId.HostSettings)
        {
            OnSettingsChanged(e.State);
        }
        else if (e.Id.Kind == DocumentKind.Preset && (e.Id.Id == _settings.ActivePreset || e.Id.Id == _preset?.Id))
        {
            _ = ReloadPresetAsync($"Preset '{e.Id.Id}' changed");
        }
        else if (e.Origin == ChangeOrigin.External && e.Id == new DocumentId(DocumentKind.Layout, _layoutId))
        {
            _ = ReapplyLayoutAsync();
        }
    }

    private void OnSettingsChanged(DocumentState state)
    {
        _settingsHealth = state.Health;
        var problems = new List<string>();
        var parsed = state is { Health: DocumentHealth.Valid, Content: not null } ? Documents.ParseHostSettings(state.Content, problems) : null;
        if (parsed is null)
        {
            _log?.Write(LogLevel.Error, "config",
                $"settings.json is {state.Health}: {state.LastError?.Detail} {string.Join("; ", problems)} Keeping the last good settings.");
            return;
        }

        var previous = _settings;
        _settings = parsed;
        if (parsed.ActivePreset != previous.ActivePreset)
        {
            _ = ReloadPresetAsync($"activePreset is now '{parsed.ActivePreset}'");
        }
    }

    private async Task ReloadPresetAsync(string reason)
    {
        await _appearanceGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _exiting) == 1)
            {
                return;
            }

            _log?.Write(LogLevel.Info, "host", $"{reason}; applying preset '{_settings.ActivePreset}'.");
            var config = _config!;
            var log = _log!;
            var requested = _settings.ActivePreset;
            // A live edit to an invalid preset keeps the running one; only startup falls back to the default.
            var preset = await Task.Run(() => TryLoadPreset(config, requested, log));
            if (preset is null)
            {
                _log?.Write(LogLevel.Warning, "host", $"Keeping preset '{_preset?.Id}'.");
                return;
            }

            if (preset.Layout != _layoutId)
            {
                _layoutId = preset.Layout;
                await ApplyLayoutAsync();
            }

            var previous = _preset;
            _preset = preset;
            if (!await ApplyAppearanceLockedAsync() && previous is not null)
            {
                // The look stays the previous preset's, so later palette and app-mode changes build on that.
                _preset = previous with { Layout = preset.Layout };
            }
        }
        catch (Exception e)
        {
            _log?.Write(LogLevel.Error, "host", $"Applying the preset failed: {e}");
        }
        finally
        {
            _appearanceGate.Release();
        }
    }

    private async Task ReapplyAppearanceAsync(string reason)
    {
        await _appearanceGate.WaitAsync();
        try
        {
            if (Volatile.Read(ref _exiting) == 1 || _preset is null)
            {
                return;
            }

            _log?.Write(LogLevel.Info, "theme", $"{reason}; applying the appearance again.");
            await ApplyAppearanceLockedAsync();
        }
        catch (Exception e)
        {
            _log?.Write(LogLevel.Error, "theme", $"Applying the appearance failed: {e}");
        }
        finally
        {
            _appearanceGate.Release();
        }
    }

    private async Task<bool> ApplyAppearanceLockedAsync()
    {
        var result = await _theme!.ApplyAsync(AppearanceOf(_preset!), CancellationToken.None);
        if (result.Applied)
        {
            return true;
        }

        foreach (var error in result.Errors)
        {
            _log?.Write(LogLevel.Error, "theme", error.ToString());
        }

        _log?.Write(LogLevel.Warning, "theme", "Keeping the previous appearance.");
        return false;
    }
}

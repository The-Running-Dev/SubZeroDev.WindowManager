using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;

namespace SubZeroDev.WindowManager.Host.Theming;

public sealed record PaletteSelection(string? Single, string? Light, string? Dark);

public sealed record AppearanceSelection(string ThemeId, PaletteSelection Palette, IReadOnlyList<string> AssetPacks);

public sealed record ApplyResult(bool Applied, IReadOnlyList<ThemeError> Errors);

public interface IThemeEngine
{
    AppearanceSelection Current { get; }

    PanelBackdrop CurrentBackdrop { get; }

    Task<ApplyResult> ApplyAsync(AppearanceSelection selection, CancellationToken ct);   // I13

    Task<ApplyResult> ValidateAsync(string themeId, CancellationToken ct);              // no swap

    event EventHandler AppearanceChanged;   // raised on the UI thread after a swap
}

/// <summary>
/// Loads a theme in two halves: <see cref="ThemePackageReader"/> reads and checks the files off the UI
/// thread, then the dictionaries are built with <c>XamlReader.Load</c> on the UI thread, checked, and
/// swapped into <c>Application.Resources</c> in one step (I13). S1 takes token colours from
/// <c>theme.json</c> only; palettes, asset packs and icons arrive with later slices.
/// </summary>
public sealed class ThemeEngine : IThemeEngine
{
    public const string SurfacesNamespace = "SubZeroDev.WindowManager.Host.Surfaces.Controls";

    /// <summary>Style keys whose target type the host fixes, by key prefix.</summary>
    private static readonly (string Prefix, string TargetType)[] TargetTypes =
    [
        ("Wm.Panel", SurfacesNamespace + ".WmPanel"),
        ("Wm.Placeholder", SurfacesNamespace + ".WmPlaceholder"),
        ("Wm.Zone", SurfacesNamespace + ".WmZone"),
        ("Wm.Contribution", SurfacesNamespace + ".WmContribution"),
    ];

    private static readonly string[] RequiredKeys = ["Wm.Panel"];

    private readonly Locations _locations;
    private readonly IHostLog _log;
    private readonly DispatcherQueue _dispatcher;
    private readonly List<ResourceDictionary> _applied = [];

    public ThemeEngine(Locations locations, IHostLog log, DispatcherQueue dispatcher)
    {
        _locations = locations;
        _log = log;
        _dispatcher = dispatcher;
    }

    public AppearanceSelection Current { get; private set; } = new(string.Empty, new PaletteSelection(null, null, null), []);

    public PanelBackdrop CurrentBackdrop { get; private set; } = PanelBackdrop.None;

    /// <summary>Where the active theme came from; <see cref="ThemeSource.HostDefault"/> until a theme applies.</summary>
    public ThemeSource CurrentSource { get; private set; } = ThemeSource.HostDefault;

    public event EventHandler? AppearanceChanged;

    public Task<ApplyResult> ApplyAsync(AppearanceSelection selection, CancellationToken ct) =>
        LoadAsync(selection, builtInOnly: false, swap: true, ct);

    public Task<ApplyResult> ValidateAsync(string themeId, CancellationToken ct) =>
        LoadAsync(new AppearanceSelection(themeId, Current.Palette, []), builtInOnly: false, swap: false, ct);

    /// <summary>
    /// Recovery after a failed apply: the built-in default theme, ignoring any user copy, and failing
    /// that the control defaults in <c>Generic.xaml</c> alone.
    /// </summary>
    public async Task<ApplyResult> ApplyFallbackAsync(CancellationToken ct)
    {
        var selection = new AppearanceSelection(ThemePackageReader.DefaultThemeId, new PaletteSelection(null, null, null), []);
        var result = await LoadAsync(selection, builtInOnly: true, swap: true, ct).ConfigureAwait(false);
        if (result.Applied)
        {
            return result;
        }

        await RunOnUiAsync(() =>
        {
            Swap([], selection with { ThemeId = string.Empty }, PanelBackdrop.None, ThemeSource.HostDefault);
            return true;
        }).ConfigureAwait(false);
        return result;
    }

    private async Task<ApplyResult> LoadAsync(AppearanceSelection selection, bool builtInOnly, bool swap, CancellationToken ct)
    {
        var (package, readErrors) = await Task.Run(() => ThemePackageReader.Read(_locations, selection.ThemeId, builtInOnly), ct)
            .ConfigureAwait(false);
        if (package is null)
        {
            return new ApplyResult(false, readErrors);
        }

        ct.ThrowIfCancellationRequested();
        return await RunOnUiAsync(() =>
        {
            var errors = new List<ThemeError>();
            var dictionaries = new List<ResourceDictionary>();
            foreach (var file in package.Resources)
            {
                try
                {
                    dictionaries.Add((ResourceDictionary)XamlReader.Load(file.Xaml));
                }
                catch (Exception e)
                {
                    errors.Add(new ThemeError(ThemeErrorCode.XamlParse, file.Path, null, e.Message));
                }
            }

            if (errors.Count == 0)
            {
                Check(package, dictionaries, errors);
            }

            if (errors.Count > 0)
            {
                return new ApplyResult(false, errors);
            }

            if (swap)
            {
                dictionaries.Insert(0, BuildTokenDictionary(package.Tokens));
                Swap(dictionaries, selection with { ThemeId = package.Id }, package.Backdrop, package.Source);
                _log.Write(LogLevel.Info, "theme", $"Applied {package.Id} from {package.Source}.");
            }

            return new ApplyResult(true, errors);
        }).ConfigureAwait(false);
    }

    private static void Check(ThemePackage package, List<ResourceDictionary> dictionaries, List<ThemeError> errors)
    {
        var where = new Dictionary<string, (object Value, string File)>(StringComparer.Ordinal);
        for (var i = 0; i < dictionaries.Count; i++)
        {
            foreach (var pair in dictionaries[i])
            {
                if (pair.Key is string key)
                {
                    where[key] = (pair.Value, package.Resources[i].Path);
                }
            }
        }

        foreach (var key in RequiredKeys.Where(k => !where.ContainsKey(k)))
        {
            errors.Add(new ThemeError(ThemeErrorCode.MissingRequiredKey, package.RootPath, key, "The theme must define this style."));
        }

        foreach (var (key, (value, file)) in where)
        {
            var expected = TargetTypes.FirstOrDefault(t => key == t.Prefix || key.StartsWith(t.Prefix + ".", StringComparison.Ordinal));
            if (expected.Prefix is null)
            {
                continue;
            }

            var actual = (value as Style)?.TargetType?.FullName;
            if (actual != expected.TargetType)
            {
                errors.Add(new ThemeError(ThemeErrorCode.WrongTargetType, file, key,
                    $"Expected a Style targeting {expected.TargetType}, found {actual ?? value.GetType().Name}."));
            }
        }
    }

    private static ResourceDictionary BuildTokenDictionary(IReadOnlyDictionary<string, uint> tokens)
    {
        var dictionary = new ResourceDictionary();
        foreach (var (token, argb) in tokens)
        {
            var colour = Windows.UI.Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);
            dictionary["Wm.Color." + token] = colour;
            dictionary["Wm.Brush." + token] = new SolidColorBrush(colour);
        }

        return dictionary;
    }

    /// <summary>Replaces the previous appearance dictionaries in one pass on the UI thread.</summary>
    private void Swap(List<ResourceDictionary> dictionaries, AppearanceSelection selection, PanelBackdrop backdrop, ThemeSource source)
    {
        var merged = Application.Current.Resources.MergedDictionaries;
        foreach (var old in _applied)
        {
            merged.Remove(old);
        }

        _applied.Clear();
        foreach (var dictionary in dictionaries)
        {
            merged.Add(dictionary);
            _applied.Add(dictionary);
        }

        Current = selection;
        CurrentBackdrop = backdrop;
        CurrentSource = source;
        AppearanceChanged?.Invoke(this, EventArgs.Empty);
    }

    private Task<T> RunOnUiAsync<T>(Func<T> work)
    {
        if (_dispatcher.HasThreadAccess)
        {
            return Task.FromResult(work());
        }

        var done = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(() =>
            {
                try
                {
                    done.SetResult(work());
                }
                catch (Exception e)
                {
                    done.SetException(e);
                }
            }))
        {
            done.SetException(new InvalidOperationException("The UI dispatcher is shut down."));
        }

        return done.Task;
    }
}

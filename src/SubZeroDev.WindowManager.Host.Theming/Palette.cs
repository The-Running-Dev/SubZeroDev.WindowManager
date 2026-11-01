using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Theming;

/// <summary>A palette read from disk: token colours that override the theme's defaults.</summary>
public sealed record Palette(string Id, string Path, IReadOnlyDictionary<string, uint> Colors);

/// <summary>
/// Reads <c>palettes\&lt;id&gt;.json</c> or <c>palettes\&lt;id&gt;\palette.json</c>, the user copy under the
/// data root first, then the built-in copy. Runs off the UI thread.
/// </summary>
public static class PaletteReader
{
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip };

    public static string? Locate(Locations locations, string paletteId)
    {
        try
        {
            return LocateUnchecked(locations, paletteId);
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private static string? LocateUnchecked(Locations locations, string paletteId)
    {
        foreach (var root in new[] { locations.DataRoot, locations.ContentRoot })
        {
            var palettes = System.IO.Path.GetFullPath(System.IO.Path.Combine(root, "palettes")) + System.IO.Path.DirectorySeparatorChar;
            foreach (var relative in new[] { paletteId + ".json", System.IO.Path.Combine(paletteId, "palette.json") })
            {
                var path = System.IO.Path.GetFullPath(System.IO.Path.Combine(palettes, relative));
                if (path.StartsWith(palettes, StringComparison.OrdinalIgnoreCase) && File.Exists(path))
                {
                    return path;
                }
            }
        }

        return null;
    }

    public static (Palette? Palette, IReadOnlyList<ThemeError> Errors) Read(Locations locations, string paletteId)
    {
        var errors = new List<ThemeError>();
        var path = Locate(locations, paletteId);
        if (path is null)
        {
            errors.Add(new ThemeError(ThemeErrorCode.AssetMissing, System.IO.Path.Combine("palettes", paletteId + ".json"), null,
                $"Palette '{paletteId}' is not installed."));
            return (null, errors);
        }

        JsonObject? document;
        try
        {
            document = JsonNode.Parse(File.ReadAllText(path), documentOptions: ReadOptions) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add(new ThemeError(ThemeErrorCode.PaletteInvalid, path, null, e.Message));
            return (null, errors);
        }

        if (document?["colors"] is not JsonObject declared)
        {
            errors.Add(new ThemeError(ThemeErrorCode.PaletteInvalid, path, "colors", "A palette needs a colors object."));
            return (null, errors);
        }

        var colors = new Dictionary<string, uint>(StringComparer.Ordinal);
        foreach (var (token, value) in declared)
        {
            if (value is JsonValue v && v.TryGetValue<string>(out var text) && ThemePackageReader.TryParseColour(text, out var argb))
            {
                colors[token] = argb;
            }
            else
            {
                errors.Add(new ThemeError(ThemeErrorCode.PaletteInvalid, path, token, "Palette colours must match #AARRGGBB."));
            }
        }

        return errors.Count > 0 ? (null, errors) : (new Palette(paletteId, path, colors), errors);
    }

    /// <summary>The palette a selection names now: the single palette, else the one for the app mode, else the other one.</summary>
    public static string? Resolve(PaletteSelection selection, bool dark) =>
        selection.Single ?? (dark ? selection.Dark ?? selection.Light : selection.Light ?? selection.Dark);
}

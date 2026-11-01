using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml;
using System.Xml.Linq;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Theming;

public enum ThemeErrorCode { XamlParse, MissingRequiredKey, WrongTargetType, AssetMissing, PathEscape, ThemeApiMismatch, PaletteInvalid }

public enum PanelBackdrop { None, Transparent, Mica, MicaAlt, Acrylic }

/// <summary>Where the active theme came from. <see cref="HostDefault"/> means no theme loaded and the built-in control styles apply.</summary>
public enum ThemeSource { User, BuiltIn, HostDefault }

public sealed record ThemeError(ThemeErrorCode Code, string File, string? Key, string Detail)
{
    public override string ToString() => $"{Code} {File}{(Key is null ? "" : $" [{Key}]")}: {Detail}";
}

/// <summary>A resource dictionary file of a theme, already prepared for <c>XamlReader.Load</c>.</summary>
public sealed record ThemeResourceFile(string Path, string Xaml);

/// <summary>A theme read from disk and checked, before any XAML object exists.</summary>
public sealed record ThemePackage(
    string Id, string RootPath, ThemeSource Source, PanelBackdrop Backdrop,
    IReadOnlyDictionary<string, uint> Tokens, IReadOnlyList<ThemeResourceFile> Resources);

/// <summary>
/// The file half of theme loading: everything that can run off the UI thread (I9). It finds the theme
/// folder, checks <c>theme.json</c>, reads each resource dictionary, and injects the token resources
/// (<c>Wm.Color.t</c>, <c>Wm.Brush.t</c>) into every dictionary so <c>{ThemeResource Wm.Brush.t}</c>
/// resolves while the dictionary loads.
/// </summary>
public static class ThemePackageReader
{
    public const int ThemeApi = 1;
    public const string DefaultThemeId = "subzerodev.default";
    public const string XamlNamespace = "http://schemas.microsoft.com/winfx/2006/xaml";
    public const string PresentationNamespace = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    /// <summary>The tokens the host always defines (contract: palette resources).</summary>
    public static readonly IReadOnlyDictionary<string, uint> HostTokens = new Dictionary<string, uint>
    {
        ["surface"] = 0xFF202020,
        ["surfaceAlt"] = 0xFF2B2B2B,
        ["text"] = 0xFFFFFFFF,
        ["textMuted"] = 0xFFB0B0B0,
        ["accent"] = 0xFF4CC2FF,
        ["accentText"] = 0xFF000000,
        ["border"] = 0xFF3A3A3A,
        ["attention"] = 0xFFFFB900,
        ["warning"] = 0xFFFF8C00,
        ["danger"] = 0xFFE81123,
    };

    /// <summary>The user copy under the data root wins over the built-in copy unless <paramref name="builtInOnly"/>.</summary>
    public static string? Locate(Locations locations, string themeId, bool builtInOnly, out ThemeSource source)
    {
        var user = Path.Combine(locations.DataRoot, "themes", themeId);
        if (!builtInOnly && File.Exists(Path.Combine(user, "theme.json")))
        {
            source = ThemeSource.User;
            return user;
        }

        var builtIn = Path.Combine(locations.ContentRoot, "themes", themeId);
        source = ThemeSource.BuiltIn;
        return File.Exists(Path.Combine(builtIn, "theme.json")) ? builtIn : null;
    }

    public static (ThemePackage? Package, IReadOnlyList<ThemeError> Errors) Read(Locations locations, string themeId, bool builtInOnly)
    {
        var errors = new List<ThemeError>();
        var root = Locate(locations, themeId, builtInOnly, out var source);
        if (root is null)
        {
            errors.Add(new ThemeError(ThemeErrorCode.AssetMissing, Path.Combine("themes", themeId, "theme.json"), null, $"Theme '{themeId}' is not installed."));
            return (null, errors);
        }

        var metadataPath = Path.Combine(root, "theme.json");
        JsonObject? metadata;
        try
        {
            metadata = JsonNode.Parse(File.ReadAllText(metadataPath),
                documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            errors.Add(new ThemeError(ThemeErrorCode.ThemeApiMismatch, metadataPath, null, e.Message));
            return (null, errors);
        }

        if (metadata is null)
        {
            errors.Add(new ThemeError(ThemeErrorCode.ThemeApiMismatch, metadataPath, null, "theme.json is not a JSON object."));
            return (null, errors);
        }

        if (metadata["themeApi"] is not JsonValue api || !api.TryGetValue<int>(out var apiVersion) || apiVersion != ThemeApi)
        {
            errors.Add(new ThemeError(ThemeErrorCode.ThemeApiMismatch, metadataPath, "themeApi", $"The host supports themeApi {ThemeApi}."));
        }

        var tokens = new Dictionary<string, uint>(HostTokens);
        if (metadata["tokens"] is JsonObject declared)
        {
            foreach (var (token, value) in declared)
            {
                if (value is JsonValue v && v.TryGetValue<string>(out var text) && TryParseColour(text, out var argb))
                {
                    tokens[token] = argb;
                }
                else
                {
                    errors.Add(new ThemeError(ThemeErrorCode.PaletteInvalid, metadataPath, token, "Token colours must match #AARRGGBB."));
                }
            }
        }

        var backdrop = metadata["panelBackdrop"] is JsonValue b && b.TryGetValue<string>(out var backdropName)
            ? backdropName switch
            {
                "transparent" => PanelBackdrop.Transparent,
                "mica" => PanelBackdrop.Mica,
                "micaAlt" => PanelBackdrop.MicaAlt,
                "acrylic" => PanelBackdrop.Acrylic,
                _ => PanelBackdrop.None,
            }
            : PanelBackdrop.None;

        var resources = new List<ThemeResourceFile>();
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;
        foreach (var entry in (metadata["resources"] as JsonArray) ?? [])
        {
            if (entry is not JsonValue rv || !rv.TryGetValue<string>(out var relative))
            {
                errors.Add(new ThemeError(ThemeErrorCode.AssetMissing, metadataPath, "resources", "Each resource must be a relative path."));
                continue;
            }

            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                errors.Add(new ThemeError(ThemeErrorCode.PathEscape, path, "resources", "The path leaves the theme folder."));
                continue;
            }

            if (!File.Exists(path))
            {
                errors.Add(new ThemeError(ThemeErrorCode.AssetMissing, path, "resources", "The file does not exist."));
                continue;
            }

            var xaml = InjectTokens(path, File.ReadAllText(path), tokens, out var parseError);
            if (parseError is not null)
            {
                errors.Add(parseError);
                continue;
            }

            resources.Add(new ThemeResourceFile(path, xaml!));
        }

        if (resources.Count == 0 && errors.Count == 0)
        {
            errors.Add(new ThemeError(ThemeErrorCode.MissingRequiredKey, metadataPath, "Wm.Panel", "The theme lists no resource dictionaries."));
        }

        var id = metadata["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var declaredId) ? declaredId : themeId;
        return errors.Count > 0 ? (null, errors) : (new ThemePackage(id, root, source, backdrop, tokens, resources), errors);
    }

    public static bool TryParseColour(string text, out uint argb)
    {
        argb = 0;
        return text.Length == 9 && text[0] == '#'
            && uint.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out argb);
    }

    public static string FormatColour(uint argb) => $"#{argb:X8}";

    /// <summary>Checks the dictionary is well-formed XML and adds the token resources as its first children.</summary>
    internal static string? InjectTokens(string path, string xaml, IReadOnlyDictionary<string, uint> tokens, out ThemeError? error)
    {
        XDocument document;
        try
        {
            document = XDocument.Parse(xaml, LoadOptions.PreserveWhitespace);
        }
        catch (XmlException e)
        {
            error = new ThemeError(ThemeErrorCode.XamlParse, path, null, e.Message);
            return null;
        }

        var root = document.Root!;
        if (root.Name != XName.Get("ResourceDictionary", PresentationNamespace))
        {
            error = new ThemeError(ThemeErrorCode.XamlParse, path, null, "The root element must be a ResourceDictionary.");
            return null;
        }

        XNamespace p = PresentationNamespace;
        XNamespace x = XamlNamespace;
        var injected = new List<XElement>();
        foreach (var (token, argb) in tokens)
        {
            injected.Add(new XElement(p + "Color", new XAttribute(x + "Key", "Wm.Color." + token), FormatColour(argb)));
            injected.Add(new XElement(p + "SolidColorBrush", new XAttribute(x + "Key", "Wm.Brush." + token), new XAttribute("Color", FormatColour(argb))));
        }

        // Property elements (ResourceDictionary.MergedDictionaries and the like) must stay first.
        var firstContent = root.Elements().FirstOrDefault(e => !e.Name.LocalName.Contains('.'));
        if (firstContent is null)
        {
            root.Add(injected);
        }
        else
        {
            firstContent.AddBeforeSelf(injected);
        }

        error = null;
        return document.ToString(SaveOptions.DisableFormatting);
    }
}

using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Theming;

/// <summary>Why a pack is in the chain: the preset listed it, the theme bundles it, or it is the host default.</summary>
public enum AssetPackOrigin { Preset, Theme, Host }

/// <summary>An <c>appIcons</c> entry. <see cref="Exe"/> is a file name (any folder) or a full path; <see cref="Aumid"/> an app user model id.</summary>
public sealed record AppIconRule(string? Exe, string? Aumid, string ImagePath);

/// <summary>An asset pack read from disk. An <see cref="PackageHealth.Invalid"/> pack stays in the chain for status only and is never matched.</summary>
public sealed record AssetPack(
    string Id, string? RootPath, AssetPackOrigin Origin, PackageHealth Health,
    IReadOnlyDictionary<string, string> Icons, IReadOnlyList<AppIconRule> AppIcons, IReadOnlyList<ThemeError> Problems)
{
    /// <summary>How an icon from this pack is reported: <c>pack:id</c>, <c>theme:id</c> or <c>host:id</c>.</summary>
    public string Label => Origin switch
    {
        AssetPackOrigin.Theme => "theme:" + Id,
        AssetPackOrigin.Host => "host:" + Id,
        _ => "pack:" + Id,
    };
}

/// <summary>A match in the chain: the image file and the pack it came from.</summary>
public sealed record AssetPackImage(string Path, AssetPack Pack);

/// <summary>
/// The icon fallback chain, first match wins: the preset's packs in order, the theme's bundled pack, then
/// the host default pack. A new chain is built on every appearance apply, so a resolver can tell a change
/// by reference.
/// </summary>
public sealed class AssetPackChain(IReadOnlyList<AssetPack> packs)
{
    public static readonly AssetPackChain Empty = new([]);

    public IReadOnlyList<AssetPack> Packs { get; } = packs;

    public AssetPackImage? FindNamed(string id)
    {
        foreach (var pack in Packs)
        {
            if (pack.Health == PackageHealth.Valid && pack.Icons.TryGetValue(id, out var path))
            {
                return new AssetPackImage(path, pack);
            }
        }

        return null;
    }

    /// <summary>The first <c>appIcons</c> rule matching an executable path or an app user model id.</summary>
    public AssetPackImage? FindApp(string? exePath, string? aumid)
    {
        foreach (var pack in Packs.Where(p => p.Health == PackageHealth.Valid))
        {
            foreach (var rule in pack.AppIcons)
            {
                if (Matches(rule, exePath, aumid))
                {
                    return new AssetPackImage(rule.ImagePath, pack);
                }
            }
        }

        return null;
    }

    private static bool Matches(AppIconRule rule, string? exePath, string? aumid)
    {
        if (rule.Aumid is not null)
        {
            return aumid is not null && string.Equals(rule.Aumid, aumid, StringComparison.OrdinalIgnoreCase);
        }

        if (rule.Exe is null || string.IsNullOrEmpty(exePath))
        {
            return false;
        }

        return rule.Exe.Contains('\\') || rule.Exe.Contains('/')
            ? string.Equals(Path.GetFullPath(rule.Exe), Path.GetFullPath(exePath), StringComparison.OrdinalIgnoreCase)
            : string.Equals(rule.Exe, Path.GetFileName(exePath), StringComparison.OrdinalIgnoreCase);
    }
}

/// <summary>
/// Reads <c>assetpack.json</c> files (contract: data-root documents) and builds the chain. Everything here
/// is file work and runs off the UI thread. An image path that leaves the pack folder makes the whole pack
/// <see cref="PackageHealth.Invalid"/> with <see cref="ThemeErrorCode.PathEscape"/>; a missing or unsupported
/// image only drops that entry.
/// </summary>
public static class AssetPackReader
{
    public const string HostDefaultId = "subzerodev.default";
    public const string FileName = "assetpack.json";

    private static readonly string[] ImageExtensions = [".png", ".svg"];

    /// <summary>The folder holding a pack: inside the theme first, then the data root, then the built-in content.</summary>
    public static string? Locate(Locations locations, string id, string? themeRoot = null)
    {
        string?[] candidates =
        [
            themeRoot is null ? null : Path.Combine(themeRoot, "assetpacks", id),
            Path.Combine(locations.DataRoot, "assetpacks", id),
            Path.Combine(locations.ContentRoot, "assetpacks", id),
        ];
        return candidates.FirstOrDefault(c => c is not null && File.Exists(Path.Combine(c, FileName)));
    }

    public static AssetPackChain BuildChain(Locations locations, IReadOnlyList<string> presetPacks, string? themeRoot, string? themePack)
    {
        var packs = new List<AssetPack>();
        foreach (var id in presetPacks)
        {
            packs.Add(Load(locations, id, themeRoot, AssetPackOrigin.Preset));
        }

        if (!string.IsNullOrEmpty(themePack))
        {
            packs.Add(Load(locations, themePack, themeRoot, AssetPackOrigin.Theme));
        }

        packs.Add(Load(locations, HostDefaultId, null, AssetPackOrigin.Host));
        return new AssetPackChain(packs);
    }

    private static AssetPack Load(Locations locations, string id, string? themeRoot, AssetPackOrigin origin)
    {
        var root = IsValidId(id) ? Locate(locations, id, themeRoot) : null;
        return root is null
            ? Missing(id, origin, Path.Combine("assetpacks", id, FileName), $"Asset pack '{id}' is not installed.")
            : Read(root, id, origin);
    }

    public static AssetPack Read(string root, string id, AssetPackOrigin origin)
    {
        var metadataPath = Path.Combine(root, FileName);
        JsonObject? metadata;
        try
        {
            metadata = JsonNode.Parse(File.ReadAllText(metadataPath),
                documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject;
        }
        catch (Exception e) when (e is JsonException or IOException or UnauthorizedAccessException)
        {
            return Missing(id, origin, metadataPath, e.Message) with { RootPath = root };
        }

        if (metadata is null)
        {
            return Missing(id, origin, metadataPath, "assetpack.json is not a JSON object.") with { RootPath = root };
        }

        var problems = new List<ThemeError>();
        var escaped = false;
        var fullRoot = Path.GetFullPath(root) + Path.DirectorySeparatorChar;

        string? Image(JsonNode? node, string key)
        {
            if (node is not JsonValue value || !value.TryGetValue<string>(out var relative) || relative.Length == 0)
            {
                problems.Add(new ThemeError(ThemeErrorCode.AssetMissing, metadataPath, key, "An image must be a relative path."));
                return null;
            }

            var path = Path.GetFullPath(Path.Combine(root, relative));
            if (!path.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase))
            {
                problems.Add(new ThemeError(ThemeErrorCode.PathEscape, path, key, "The path leaves the asset pack folder."));
                escaped = true;
                return null;
            }

            if (!ImageExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
            {
                problems.Add(new ThemeError(ThemeErrorCode.AssetMissing, path, key, "Images must be PNG or SVG."));
                return null;
            }

            if (!File.Exists(path))
            {
                problems.Add(new ThemeError(ThemeErrorCode.AssetMissing, path, key, "The file does not exist."));
                return null;
            }

            return path;
        }

        var icons = new Dictionary<string, string>(StringComparer.Ordinal);
        if (metadata["icons"] is JsonObject declared)
        {
            foreach (var (name, node) in declared)
            {
                if (Image(node, name) is { } path)
                {
                    icons[name] = path;
                }
            }
        }

        var appIcons = new List<AppIconRule>();
        foreach (var entry in (metadata["appIcons"] as JsonArray) ?? [])
        {
            var match = (entry as JsonObject)?["match"] as JsonObject;
            var exe = Text(match?["exe"]);
            var aumid = Text(match?["aumid"]);
            if ((exe is null) == (aumid is null))
            {
                problems.Add(new ThemeError(ThemeErrorCode.AssetMissing, metadataPath, "appIcons", "Each match names exactly one of exe or aumid."));
                continue;
            }

            if (Image((entry as JsonObject)?["image"], exe ?? aumid!) is { } path)
            {
                appIcons.Add(new AppIconRule(exe, aumid, path));
            }
        }

        var health = escaped ? PackageHealth.Invalid : PackageHealth.Valid;
        return new AssetPack(id, root, origin, health, icons, appIcons, problems);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static bool IsValidId(string id) =>
        id.Length is > 0 and <= 128 && id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '.' or '-');

    private static AssetPack Missing(string id, AssetPackOrigin origin, string file, string detail) =>
        new(id, null, origin, PackageHealth.Invalid, new Dictionary<string, string>(), [], [new ThemeError(ThemeErrorCode.AssetMissing, file, null, detail)]);
}

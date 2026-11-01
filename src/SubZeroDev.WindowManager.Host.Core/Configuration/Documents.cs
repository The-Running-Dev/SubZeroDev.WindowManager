using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

public enum PanelBehaviour { Reserve, Overlay, AutoHide, Desktop }

public enum MonitorSelectorKind { Primary, All, Device }

public sealed record MonitorSelector(MonitorSelectorKind Kind, string? Device)
{
    public static MonitorSelector Primary { get; } = new(MonitorSelectorKind.Primary, null);
}

public sealed record FloatRect(double X, double Y, double Width, double Height);

/// <summary>Either <see cref="Edge"/> or <see cref="Float"/> is set. <see cref="Length"/> is null for "fill".</summary>
public sealed record PanelPlacement(PanelEdge? Edge, double? Length, string Align, FloatRect? Float);

public sealed record ContributionInstance(Guid Id, string Plugin, string Contribution, JsonObject Settings, int SettingsVersion);

public sealed record PanelDefinition(
    Guid Id, MonitorSelector Monitor, PanelPlacement Placement, double Thickness, PanelBehaviour Behaviour,
    IReadOnlyDictionary<string, IReadOnlyList<ContributionInstance>> Zones);

public sealed record LayoutDocument(string Id, string DisplayName, IReadOnlyList<PanelDefinition> Panels);

public sealed record PresetDocument(string Id, string DisplayName, string Layout, string Theme,
    string? Palette, string? PaletteLight, string? PaletteDark, IReadOnlyList<string> AssetPacks);

public sealed record RestartPolicy(int MaxRestarts, int WindowSeconds)
{
    public static RestartPolicy Default { get; } = new(3, 300);
}

public sealed record HostSettingsDocument(string ActivePreset, bool StartAtSignIn, RestartPolicy RestartPolicy)
{
    public const string DefaultPreset = "subzerodev.default";

    public static HostSettingsDocument Default { get; } = new(DefaultPreset, false, RestartPolicy.Default);

    public JsonObject ToJson() => new()
    {
        ["schemaVersion"] = ConfigStore.SupportedSchemaVersion,
        ["activePreset"] = ActivePreset,
        ["plugins"] = new JsonObject(),
        ["commandProviders"] = new JsonObject(),
        ["hotkeys"] = new JsonObject(),
        ["startAtSignIn"] = StartAtSignIn,
        ["restartPolicy"] = new JsonObject
        {
            ["maxRestarts"] = RestartPolicy.MaxRestarts,
            ["windowSeconds"] = RestartPolicy.WindowSeconds,
        },
    };
}

/// <summary>
/// Reads the typed documents out of their JSON. Each parser records every problem it finds and returns
/// null when there is at least one, so the same code serves loading and <see cref="DocumentValidator"/>.
/// </summary>
public static partial class Documents
{
    public static readonly IReadOnlyList<string> ZoneKeys = ["start", "center", "end"];

    [GeneratedRegex("^[a-z0-9]+(\\.[a-z0-9-]+)+$")]
    private static partial Regex PackageIdPattern();

    [GeneratedRegex("^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex LocalIdPattern();

    public static bool IsPackageId(string? value) => value is { Length: <= 128 } && PackageIdPattern().IsMatch(value);

    public static HostSettingsDocument? ParseHostSettings(JsonObject json, List<string> problems)
    {
        var start = problems.Count;
        RequireSchemaVersion(json, problems);
        var preset = RequiredString(json, "activePreset", problems);
        if (preset is not null && !IsPackageId(preset))
        {
            problems.Add($"activePreset: '{preset}' is not a package id.");
        }

        var startAtSignIn = json["startAtSignIn"] is JsonValue s && s.TryGetValue<bool>(out var b) && b;
        var policy = RestartPolicy.Default;
        if (json["restartPolicy"] is JsonObject rp)
        {
            var max = OptionalInt(rp, "maxRestarts", policy.MaxRestarts, problems, "restartPolicy.");
            var window = OptionalInt(rp, "windowSeconds", policy.WindowSeconds, problems, "restartPolicy.");
            if (max is < 0 or > 10)
            {
                problems.Add("restartPolicy.maxRestarts: must be 0–10.");
            }

            if (window is < 30 or > 3600)
            {
                problems.Add("restartPolicy.windowSeconds: must be 30–3600.");
            }

            policy = new RestartPolicy(max, window);
        }
        else if (json["restartPolicy"] is not null)
        {
            problems.Add("restartPolicy: must be an object.");
        }

        return problems.Count > start ? null : new HostSettingsDocument(preset!, startAtSignIn, policy);
    }

    public static PresetDocument? ParsePreset(JsonObject json, string expectedId, List<string> problems)
    {
        var start = problems.Count;
        RequireSchemaVersion(json, problems);
        var id = RequireIdMatching(json, expectedId, problems);
        var layout = RequiredPackageId(json, "layout", problems);
        var theme = RequiredPackageId(json, "theme", problems);

        string? single = null, light = null, dark = null;
        switch (json["palette"])
        {
            case JsonValue v when v.TryGetValue<string>(out var p):
                single = p;
                break;
            case JsonObject o:
                light = RequiredPackageId(o, "light", problems, "palette.");
                dark = RequiredPackageId(o, "dark", problems, "palette.");
                break;
            case null:
                break;
            default:
                problems.Add("palette: must be a palette id or { light, dark }.");
                break;
        }

        var packs = new List<string>();
        if (json["assetPacks"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue pv && pv.TryGetValue<string>(out var pack) && IsPackageId(pack))
                {
                    packs.Add(pack);
                }
                else
                {
                    problems.Add("assetPacks: every entry must be a package id.");
                }
            }
        }
        else if (json["assetPacks"] is not null)
        {
            problems.Add("assetPacks: must be an array.");
        }

        return problems.Count > start
            ? null
            : new PresetDocument(id!, DisplayName(json, id!), layout!, theme!, single, light, dark, packs);
    }

    public static LayoutDocument? ParseLayout(JsonObject json, string expectedId, List<string> problems)
    {
        var start = problems.Count;
        RequireSchemaVersion(json, problems);
        var id = RequireIdMatching(json, expectedId, problems);
        var panels = new List<PanelDefinition>();
        var panelIds = new HashSet<Guid>();
        var instanceIds = new HashSet<Guid>();

        if (json["panels"] is not JsonArray array)
        {
            problems.Add("panels: required array.");
        }
        else
        {
            for (var i = 0; i < array.Count; i++)
            {
                if (array[i] is not JsonObject p)
                {
                    problems.Add($"panels[{i}]: must be an object.");
                    continue;
                }

                var panel = ParsePanel(p, $"panels[{i}].", instanceIds, problems);
                if (panel is not null && !panelIds.Add(panel.Id))
                {
                    problems.Add($"panels[{i}].id: {panel.Id} is used twice.");
                }

                if (panel is not null)
                {
                    panels.Add(panel);
                }
            }
        }

        return problems.Count > start ? null : new LayoutDocument(id!, DisplayName(json, id!), panels);
    }

    private static PanelDefinition? ParsePanel(JsonObject p, string at, HashSet<Guid> instanceIds, List<string> problems)
    {
        var start = problems.Count;
        var id = RequiredGuid(p, "id", problems, at);

        var monitor = MonitorSelector.Primary;
        switch (p["monitor"])
        {
            case null:
                break;
            case JsonValue v when v.TryGetValue<string>(out var m) && m is "primary" or "all":
                monitor = m == "all" ? new MonitorSelector(MonitorSelectorKind.All, null) : MonitorSelector.Primary;
                break;
            case JsonObject o when o["device"] is JsonValue d && d.TryGetValue<string>(out var device) && device.Length > 0:
                monitor = new MonitorSelector(MonitorSelectorKind.Device, device);
                break;
            default:
                problems.Add($"{at}monitor: must be \"primary\", \"all\" or {{ device }}.");
                break;
        }

        PanelPlacement? placement = null;
        if (p["placement"] is not JsonObject pl)
        {
            problems.Add($"{at}placement: required object.");
        }
        else if (pl["float"] is JsonObject f)
        {
            var x = RequiredNumber(f, "x", problems, at + "placement.float.");
            var y = RequiredNumber(f, "y", problems, at + "placement.float.");
            var w = RequiredNumber(f, "width", problems, at + "placement.float.");
            var h = RequiredNumber(f, "height", problems, at + "placement.float.");
            placement = new PanelPlacement(null, null, "start", new FloatRect(x, y, w, h));
        }
        else
        {
            PanelEdge? edge = (pl["edge"] as JsonValue)?.TryGetValue<string>(out var e) == true
                ? e switch { "top" => PanelEdge.Top, "bottom" => PanelEdge.Bottom, "left" => PanelEdge.Left, "right" => PanelEdge.Right, _ => null }
                : null;
            if (edge is null)
            {
                problems.Add($"{at}placement: needs edge (top|bottom|left|right) or float.");
            }

            double? length = null;
            switch (pl["length"])
            {
                case null:
                case JsonValue lv when lv.TryGetValue<string>(out var fill) && fill == "fill":
                    break;
                case JsonValue lv when lv.TryGetValue<double>(out var dip) && dip > 0:
                    length = dip;
                    break;
                default:
                    problems.Add($"{at}placement.length: must be a positive DIP value or \"fill\".");
                    break;
            }

            var align = "start";
            if (pl["align"] is JsonValue av && av.TryGetValue<string>(out var a))
            {
                if (a is "start" or "center" or "end")
                {
                    align = a;
                }
                else
                {
                    problems.Add($"{at}placement.align: must be start, center or end.");
                }
            }

            placement = new PanelPlacement(edge, length, align, null);
        }

        var thickness = RequiredNumber(p, "thickness", problems, at);
        if (thickness is < 16 or > 512)
        {
            problems.Add($"{at}thickness: must be 16–512 DIP.");
        }

        PanelBehaviour? behaviour = (p["behaviour"] as JsonValue)?.TryGetValue<string>(out var bh) == true
            ? bh switch { "reserve" => PanelBehaviour.Reserve, "overlay" => PanelBehaviour.Overlay, "autoHide" => PanelBehaviour.AutoHide, "desktop" => PanelBehaviour.Desktop, _ => null }
            : null;
        if (behaviour is null)
        {
            problems.Add($"{at}behaviour: must be reserve, overlay, autoHide or desktop.");
        }

        var zones = new Dictionary<string, IReadOnlyList<ContributionInstance>>();
        if (p["zones"] is JsonObject z)
        {
            foreach (var (key, value) in z)
            {
                if (!ZoneKeys.Contains(key))
                {
                    problems.Add($"{at}zones.{key}: only start, center and end are allowed.");
                    continue;
                }

                if (value is not JsonArray items)
                {
                    problems.Add($"{at}zones.{key}: must be an array.");
                    continue;
                }

                var list = new List<ContributionInstance>();
                for (var i = 0; i < items.Count; i++)
                {
                    var instance = ParseInstance(items[i], $"{at}zones.{key}[{i}].", problems);
                    if (instance is null)
                    {
                        continue;
                    }

                    if (!instanceIds.Add(instance.Id))
                    {
                        problems.Add($"{at}zones.{key}[{i}].id: {instance.Id} is used twice.");
                    }

                    list.Add(instance);
                }

                zones[key] = list;
            }
        }
        else if (p["zones"] is not null)
        {
            problems.Add($"{at}zones: must be an object.");
        }

        return problems.Count > start ? null : new PanelDefinition(id, monitor, placement!, thickness, behaviour!.Value, zones);
    }

    private static ContributionInstance? ParseInstance(JsonNode? node, string at, List<string> problems)
    {
        if (node is not JsonObject o)
        {
            problems.Add($"{at.TrimEnd('.')}: must be an object.");
            return null;
        }

        var start = problems.Count;
        var id = RequiredGuid(o, "id", problems, at);
        var plugin = RequiredPackageId(o, "plugin", problems, at);
        var contribution = RequiredString(o, "contribution", problems, at);
        if (contribution is not null && !LocalIdPattern().IsMatch(contribution))
        {
            problems.Add($"{at}contribution: '{contribution}' is not a valid id.");
        }

        var settings = o["settings"] switch
        {
            null => new JsonObject(),
            JsonObject s => (JsonObject)s.DeepClone(),
            _ => null,
        };
        if (settings is null)
        {
            problems.Add($"{at}settings: must be an object.");
        }

        var version = OptionalInt(o, "settingsVersion", 1, problems, at);
        return problems.Count > start ? null : new ContributionInstance(id, plugin!, contribution!, settings!, version);
    }

    private static void RequireSchemaVersion(JsonObject json, List<string> problems)
    {
        if (json["schemaVersion"] is not JsonValue v || !v.TryGetValue<int>(out var version) || version < 1)
        {
            problems.Add("schemaVersion: required positive integer.");
        }
    }

    private static string? RequireIdMatching(JsonObject json, string expectedId, List<string> problems)
    {
        var id = RequiredPackageId(json, "id", problems);
        if (id is not null && !string.Equals(id, expectedId, StringComparison.Ordinal))
        {
            problems.Add($"id: '{id}' does not match the file name '{expectedId}'.");
        }

        return id;
    }

    private static string DisplayName(JsonObject json, string fallback) =>
        json["displayName"] is JsonValue v && v.TryGetValue<string>(out var name) ? name : fallback;

    private static string? RequiredString(JsonObject json, string key, List<string> problems, string at = "")
    {
        if (json[key] is JsonValue v && v.TryGetValue<string>(out var s) && s.Length > 0)
        {
            return s;
        }

        problems.Add($"{at}{key}: required string.");
        return null;
    }

    private static string? RequiredPackageId(JsonObject json, string key, List<string> problems, string at = "")
    {
        var value = RequiredString(json, key, problems, at);
        if (value is not null && !IsPackageId(value))
        {
            problems.Add($"{at}{key}: '{value}' is not a package id.");
            return null;
        }

        return value;
    }

    private static Guid RequiredGuid(JsonObject json, string key, List<string> problems, string at)
    {
        if (json[key] is JsonValue v && v.TryGetValue<string>(out var s) && Guid.TryParse(s, out var g))
        {
            return g;
        }

        problems.Add($"{at}{key}: required GUID.");
        return Guid.Empty;
    }

    private static double RequiredNumber(JsonObject json, string key, List<string> problems, string at)
    {
        if (json[key] is JsonValue v && v.TryGetValue<double>(out var d))
        {
            return d;
        }

        problems.Add($"{at}{key}: required number.");
        return double.NaN;
    }

    private static int OptionalInt(JsonObject json, string key, int fallback, List<string> problems, string at)
    {
        switch (json[key])
        {
            case null:
                return fallback;
            case JsonValue v when v.TryGetValue<int>(out var i):
                return i;
            default:
                problems.Add($"{at}{key}: must be an integer.");
                return fallback;
        }
    }
}

public static class DocumentValidator
{
    public static IReadOnlyList<string> Validate(DocumentId id, JsonObject content)
    {
        var problems = new List<string>();
        switch (id.Kind)
        {
            case DocumentKind.HostSettings:
                Documents.ParseHostSettings(content, problems);
                break;
            case DocumentKind.Layout:
                Documents.ParseLayout(content, id.Id, problems);
                break;
            case DocumentKind.Preset:
                Documents.ParsePreset(content, id.Id, problems);
                break;
            case DocumentKind.PluginSettings:
                if (content["settings"] is not JsonObject)
                {
                    problems.Add("settings: required object.");
                }

                break;
        }

        return problems;
    }
}

using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SubZeroDev.WindowManager.Protocol;

public sealed record PluginEntry(string Path, IReadOnlyList<string> Args);

public sealed record ContributionDeclaration(string Id, string Kind, string DisplayName, bool MultiInstance);

public sealed record CommandDeclaration(string Id, string Title, string? DefaultHotkey);

/// <summary>A parsed <c>plugin.json</c>. <see cref="Raw"/> keeps the whole document for fields later slices read.</summary>
public sealed record PluginManifest(
    int ManifestVersion, string Id, SemVersion Version, string DisplayName, string Publisher, string Description,
    VersionRange HostApi, PluginEntry Entry, IReadOnlyList<HostMode> Modes,
    IReadOnlyList<ContributionDeclaration> Contributions, JsonObject Raw)
{
    public bool Supports(HostMode mode) => Modes.Contains(mode);

    /// <summary>The <c>commands</c> the plugin provides, in manifest order.</summary>
    public IReadOnlyList<CommandDeclaration> Commands =>
        [.. (Raw["commands"] as JsonArray ?? []).OfType<JsonObject>().Select(command => new CommandDeclaration(
            Text(command, "id") ?? string.Empty, Text(command, "title") ?? Text(command, "id") ?? string.Empty,
            Text(command, "defaultHotkey")))];

    /// <summary>The ids under <c>feeds</c>: the feeds the plugin publishes.</summary>
    public IReadOnlyList<string> Feeds =>
        [.. (Raw["feeds"] as JsonArray ?? []).OfType<JsonObject>().Select(feed => Text(feed, "id") ?? string.Empty)];

    /// <summary>The ids under <c>popups</c>: the popup surfaces the plugin may open.</summary>
    public IReadOnlyList<string> Popups =>
        [.. (Raw["popups"] as JsonArray ?? []).OfType<JsonObject>().Select(popup => Text(popup, "id") ?? string.Empty)];

    /// <summary>The feed ids under <c>subscribes</c>: the feeds the plugin consumes.</summary>
    public IReadOnlyList<string> Subscribes =>
        [.. (Raw["subscribes"] as JsonArray ?? []).Select(item => item is JsonValue value && value.TryGetValue<string>(out var id) ? id : string.Empty)];

    /// <summary>The plugin-level <c>settingsSchema</c>; null when absent.</summary>
    public JsonObject? SettingsSchema => Raw["settingsSchema"] as JsonObject;

    /// <summary>The version of the plugin's settings shape; 1 when absent.</summary>
    public int SettingsVersion => Raw["settingsVersion"] is JsonValue value && value.TryGetValue<int>(out var version) ? version : 1;

    /// <summary>The <c>settingsSchema</c> of a contribution; null when absent or the contribution is not declared.</summary>
    public JsonObject? ContributionSettingsSchema(string contributionId) =>
        (Raw["contributions"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(contribution => Text(contribution, "id") == contributionId)?["settingsSchema"] as JsonObject;

    private static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

public static partial class PluginManifestReader
{
    public const int SupportedManifestVersion = 1;

    /// <summary>Parses and checks a manifest; returns null and fills <paramref name="problems"/> when it is invalid.</summary>
    public static PluginManifest? Parse(JsonObject document, List<string> problems)
    {
        var manifestVersion = Int(document, "manifestVersion");
        if (manifestVersion != SupportedManifestVersion)
        {
            problems.Add($"manifestVersion must be {SupportedManifestVersion}.");
        }

        var id = Text(document, "id");
        if (id is null || id.Length > 128 || !PluginIdPattern().IsMatch(id))
        {
            problems.Add($"id '{id}' is not a valid plugin id.");
        }

        var versionText = Text(document, "version");
        if (!SemVersion.TryParse(versionText, out var version))
        {
            problems.Add($"version '{versionText}' is not a semantic version.");
        }

        var hostApiText = Text(document, "hostApi");
        if (!VersionRange.TryParse(hostApiText, out var hostApi))
        {
            problems.Add($"hostApi '{hostApiText}' is not a version range.");
        }

        PluginEntry? entry = null;
        if (document["entry"] is JsonObject entryObject && Text(entryObject, "path") is { Length: > 0 } path)
        {
            var args = new List<string>();
            foreach (var item in entryObject["args"] as JsonArray ?? [])
            {
                if (item is JsonValue value && value.TryGetValue<string>(out var arg))
                {
                    args.Add(arg);
                }
                else
                {
                    problems.Add($"entry.args item {item?.ToJsonString() ?? "null"} is not a string.");
                }
            }

            entry = new PluginEntry(path, args);
        }
        else
        {
            problems.Add("entry.path is required.");
        }

        var modes = new List<HostMode>();
        if (document["modes"] is JsonArray modeArray)
        {
            foreach (var item in modeArray)
            {
                switch (item is JsonValue value && value.TryGetValue<string>(out var mode) ? mode : null)
                {
                    case "coexistence": modes.Add(HostMode.Coexistence); break;
                    case "replacement": modes.Add(HostMode.Replacement); break;
                    default: problems.Add($"mode '{item?.ToJsonString()}' is not known."); break;
                }
            }
        }

        if (modes.Count == 0)
        {
            problems.Add("modes must name at least one mode.");
        }

        var contributions = new List<ContributionDeclaration>();
        if (document["contributions"] is JsonArray contributionArray)
        {
            foreach (var item in contributionArray.OfType<JsonObject>())
            {
                var contributionId = Text(item, "id");
                var kind = Text(item, "kind");
                if (kind != "panelItem")
                {
                    problems.Add($"contribution '{contributionId}' has kind '{kind}'; only panelItem is defined.");
                }

                var multiInstance = false;
                if (item["multiInstance"] is { } multi && !(multi is JsonValue value && value.TryGetValue(out multiInstance)))
                {
                    problems.Add($"contribution '{contributionId}' has a multiInstance that is not true or false.");
                }

                SettingsSchemaSubset.Check(item["settingsSchema"], $"contribution '{contributionId}' settingsSchema", problems);
                contributions.Add(new ContributionDeclaration(
                    contributionId ?? string.Empty, kind ?? string.Empty,
                    Text(item, "displayName") ?? contributionId ?? string.Empty,
                    multiInstance));
            }
        }

        CheckIds("contributions", contributions.Select(c => c.Id), problems);
        CheckIds("commands", Ids(document, "commands"), problems);
        CheckIds("feeds", Ids(document, "feeds"), problems);
        CheckIds("popups", Ids(document, "popups"), problems);
        if (document["subscribes"] is { } subscribes)
        {
            if (subscribes is JsonArray subscribeArray
                && subscribeArray.All(item => item is JsonValue value && value.TryGetValue<string>(out _)))
            {
                CheckIds("subscribes", subscribeArray.Select(item => item!.GetValue<string>()), problems);
            }
            else
            {
                problems.Add("subscribes must be a list of feed ids.");
            }
        }

        if (Ids(document, "commands").Any(command => command.StartsWith("host.", StringComparison.Ordinal)))
        {
            problems.Add("command ids may not start with 'host.'.");
        }

        foreach (var command in (document["commands"] as JsonArray ?? []).OfType<JsonObject>())
        {
            if (command["defaultHotkey"] is { } hotkey && !(hotkey is JsonValue value && value.TryGetValue<string>(out _)))
            {
                problems.Add($"command '{Text(command, "id")}' has a defaultHotkey that is not a string.");
            }

            SettingsSchemaSubset.Check(command["argsSchema"], $"command '{Text(command, "id")}' argsSchema", problems);
        }

        SettingsSchemaSubset.Check(document["settingsSchema"], "settingsSchema", problems);
        if (document["settingsVersion"] is { } settingsVersion && !(Int(document, "settingsVersion") is > 0))
        {
            problems.Add($"settingsVersion {settingsVersion.ToJsonString()} is not a positive whole number.");
        }

        if (document["dependencies"] is JsonArray dependencies
            && dependencies.OfType<JsonObject>().Any(dependency => Text(dependency, "id") == id))
        {
            problems.Add("dependencies may not contain the plugin itself.");
        }

        if (problems.Count > 0)
        {
            return null;
        }

        return new PluginManifest(
            manifestVersion!.Value, id!, version, Text(document, "displayName") ?? id!,
            Text(document, "publisher") ?? string.Empty, Text(document, "description") ?? string.Empty,
            hostApi, entry!, modes, contributions, document);
    }

    private static IEnumerable<string> Ids(JsonObject document, string list) =>
        document[list] is JsonArray array
            ? array.OfType<JsonObject>().Select(item => Text(item, "id") ?? string.Empty)
            : [];

    private static void CheckIds(string list, IEnumerable<string> ids, List<string> problems)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
        {
            if (!MemberIdPattern().IsMatch(id))
            {
                problems.Add($"{list} id '{id}' is not valid.");
            }
            else if (!seen.Add(id))
            {
                problems.Add($"{list} id '{id}' is used more than once.");
            }
        }
    }

    private static string? Text(JsonObject document, string name) =>
        document[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int? Int(JsonObject document, string name) =>
        document[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : null;

    [GeneratedRegex(@"^[a-z0-9]+(\.[a-z0-9-]+)+$")]
    private static partial Regex PluginIdPattern();

    [GeneratedRegex(@"^[a-z0-9]+([.-][a-z0-9]+)*$")]
    private static partial Regex MemberIdPattern();
}

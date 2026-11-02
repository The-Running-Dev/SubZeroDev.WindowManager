using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>Where one contribution instance sits in a layout.</summary>
public sealed record LayoutPlacement(Guid Panel, string Zone, int Index, Guid Instance, string Plugin, string Contribution);

/// <summary>
/// The layout editor's operations. Each takes the layout's JSON and returns an edited copy, leaving the input
/// alone, and keeps every member it does not touch (instance settings, unknown fields) as it was. A result that
/// would not parse as a layout is refused with <c>DocumentInvalid</c>.
/// </summary>
public static class LayoutEditor
{
    /// <summary>Every instance in panel, zone and position order.</summary>
    public static IReadOnlyList<LayoutPlacement> Placements(JsonObject layout)
    {
        ArgumentNullException.ThrowIfNull(layout);
        var placements = new List<LayoutPlacement>();
        foreach (var panel in Panels(layout))
        {
            if (Id(panel) is not { } panelId || panel["zones"] is not JsonObject zones)
            {
                continue;
            }

            foreach (var zone in Documents.ZoneKeys)
            {
                var items = (zones[zone] as JsonArray ?? []).OfType<JsonObject>().ToList();
                for (var i = 0; i < items.Count; i++)
                {
                    if (Id(items[i]) is { } instance)
                    {
                        placements.Add(new LayoutPlacement(panelId, zone, i, instance, Text(items[i], "plugin") ?? "", Text(items[i], "contribution") ?? ""));
                    }
                }
            }
        }

        return placements;
    }

    /// <summary>The ids of the layout's panels, in order.</summary>
    public static IReadOnlyList<Guid> PanelIds(JsonObject layout) =>
        [.. Panels(layout ?? throw new ArgumentNullException(nameof(layout))).Select(Id).OfType<Guid>()];

    /// <summary>Places a new instance with the given id at the end of a panel's zone.</summary>
    public static Outcome<JsonObject> Add(JsonObject layout, Guid panel, string zone, string plugin, string contribution, Guid instance)
    {
        var copy = Copy(layout);
        if (Zone(copy, panel, zone) is not { } items)
        {
            return NoZone(panel, zone);
        }

        items.Add(new JsonObject { ["id"] = instance.ToString("D"), ["plugin"] = plugin, ["contribution"] = contribution });
        return Checked(copy);
    }

    /// <summary>Moves an instance to a position in any panel's zone; the index is clamped to the zone.</summary>
    public static Outcome<JsonObject> Move(JsonObject layout, Guid instance, Guid panel, string zone, int index)
    {
        var copy = Copy(layout);
        if (Zone(copy, panel, zone) is not { } target)
        {
            return NoZone(panel, zone);
        }

        if (Detach(copy, instance) is not { } item)
        {
            return NoInstance(instance);
        }

        target.Insert(Math.Clamp(index, 0, target.Count), item);
        return Checked(copy);
    }

    /// <summary>Takes an instance out of the layout.</summary>
    public static Outcome<JsonObject> Remove(JsonObject layout, Guid instance)
    {
        var copy = Copy(layout);
        return Detach(copy, instance) is null ? NoInstance(instance) : Checked(copy);
    }

    /// <summary>Adds an empty reserving panel, 40 DIP thick, on the primary monitor's edge.</summary>
    public static Outcome<JsonObject> AddPanel(JsonObject layout, PanelEdge edge, Guid panel)
    {
        var copy = Copy(layout);
        if (copy["panels"] is not JsonArray panels)
        {
            panels = [];
            copy["panels"] = panels;
        }

        panels.Add(new JsonObject
        {
            ["id"] = panel.ToString("D"),
            ["monitor"] = "primary",
            ["placement"] = new JsonObject { ["edge"] = edge.ToString().ToLowerInvariant() },
            ["thickness"] = 40d,
            ["behaviour"] = "reserve",
            ["zones"] = new JsonObject { ["start"] = new JsonArray(), ["center"] = new JsonArray(), ["end"] = new JsonArray() },
        });
        return Checked(copy);
    }

    /// <summary>Takes a panel, and every instance on it, out of the layout.</summary>
    public static Outcome<JsonObject> RemovePanel(JsonObject layout, Guid panel)
    {
        var copy = Copy(layout);
        var panels = copy["panels"] as JsonArray;
        var found = Panels(copy).FirstOrDefault(item => Id(item) == panel);
        if (panels is null || found is null)
        {
            return Outcome<JsonObject>.Fail(CoreErrorCode.NotFound, $"The layout has no panel {panel}.");
        }

        panels.Remove(found);
        return Checked(copy);
    }

    private static JsonObject Copy(JsonObject layout) =>
        (JsonObject)(layout ?? throw new ArgumentNullException(nameof(layout))).DeepClone();

    private static IEnumerable<JsonObject> Panels(JsonObject layout) => (layout["panels"] as JsonArray ?? []).OfType<JsonObject>();

    private static JsonArray? Zone(JsonObject layout, Guid panel, string zone)
    {
        if (!Documents.ZoneKeys.Contains(zone) || Panels(layout).FirstOrDefault(item => Id(item) == panel) is not { } found)
        {
            return null;
        }

        if (found["zones"] is not JsonObject zones)
        {
            zones = [];
            found["zones"] = zones;
        }

        if (zones[zone] is not JsonArray items)
        {
            items = [];
            zones[zone] = items;
        }

        return items;
    }

    private static JsonObject? Detach(JsonObject layout, Guid instance)
    {
        foreach (var items in Panels(layout).Select(panel => panel["zones"]).OfType<JsonObject>()
            .SelectMany(zones => zones.Select(zone => zone.Value).OfType<JsonArray>()))
        {
            if (items.OfType<JsonObject>().FirstOrDefault(item => Id(item) == instance) is { } found)
            {
                items.Remove(found);
                return found;
            }
        }

        return null;
    }

    private static Outcome<JsonObject> Checked(JsonObject layout)
    {
        var problems = new List<string>();
        Documents.ParseLayout(layout, Text(layout, "id") ?? "", problems);
        return problems.Count == 0
            ? Outcome<JsonObject>.Success(layout)
            : Outcome<JsonObject>.Fail(CoreErrorCode.DocumentInvalid, string.Join("; ", problems));
    }

    private static Outcome<JsonObject> NoZone(Guid panel, string zone) =>
        Outcome<JsonObject>.Fail(CoreErrorCode.NotFound, $"The layout has no panel {panel} with a '{zone}' zone.");

    private static Outcome<JsonObject> NoInstance(Guid instance) =>
        Outcome<JsonObject>.Fail(CoreErrorCode.NotFound, $"The layout has no instance {instance}.");

    private static Guid? Id(JsonObject json) => Guid.TryParse(Text(json, "id"), out var id) ? id : null;

    private static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

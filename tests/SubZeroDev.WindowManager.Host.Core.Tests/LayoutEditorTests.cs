using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S11: the layout editor's operations on the layout's JSON.</summary>
public sealed class LayoutEditorTests
{
    private static readonly Guid Bottom = Guid.Parse("6f0d3c1e-5a3b-4f0e-9c1d-2b7a8e4f5a01");
    private static readonly Guid Top = Guid.Parse("11111111-2222-4333-8444-555555555555");
    private static readonly Guid Launcher = Guid.Parse("9a4c2e71-3d5b-4c8f-b1e6-7f0a2d9c4e18");
    private static readonly Guid Clock = Guid.Parse("3b8e6c2a-9d41-4f7e-a5c0-1e2d3f4a5b60");

    [Fact]
    public void S11_5_adding_places_a_new_instance_at_the_end_of_the_zone_and_leaves_the_input_alone()
    {
        var layout = Layout();
        var fresh = Guid.NewGuid();

        var added = LayoutEditor.Add(layout, Bottom, "start", "subzerodev.clock", "clock", fresh);

        Assert.True(added.Ok, added.Error?.Detail);
        Assert.Equal(
            [(Bottom, "start", 0, Launcher), (Bottom, "start", 1, fresh), (Bottom, "end", 0, Clock)],
            LayoutEditor.Placements(added.Value!).Select(p => (p.Panel, p.Zone, p.Index, p.Instance)));
        Assert.Equal(2, LayoutEditor.Placements(layout).Count);
        Assert.Equal("first", added.Value!["unknown"]!.GetValue<string>());
    }

    [Fact]
    public void Adding_to_a_zone_the_panel_does_not_list_yet_creates_it()
    {
        var added = LayoutEditor.Add(Layout(), Top, "center", "subzerodev.clock", "clock", Guid.NewGuid());

        Assert.True(added.Ok, added.Error?.Detail);
        Assert.Single(LayoutEditor.Placements(added.Value!), p => p.Panel == Top && p.Zone == "center");
    }

    [Fact]
    public void Adding_to_an_unknown_panel_or_zone_is_refused()
    {
        Assert.Equal(CoreErrorCode.NotFound, LayoutEditor.Add(Layout(), Guid.NewGuid(), "start", "a.b", "c", Guid.NewGuid()).Error?.Code);
        Assert.Equal(CoreErrorCode.NotFound, LayoutEditor.Add(Layout(), Bottom, "middle", "a.b", "c", Guid.NewGuid()).Error?.Code);
    }

    [Fact]
    public void Adding_an_id_already_in_use_is_refused_as_invalid()
    {
        var added = LayoutEditor.Add(Layout(), Top, "start", "subzerodev.clock", "clock", Clock);

        Assert.Equal(CoreErrorCode.DocumentInvalid, added.Error?.Code);
    }

    [Fact]
    public void Moving_carries_the_instance_and_its_settings_to_another_panel_and_position()
    {
        var moved = LayoutEditor.Move(Layout(), Clock, Top, "start", 99);

        Assert.True(moved.Ok, moved.Error?.Detail);
        Assert.Equal([(Bottom, "start", Launcher), (Top, "start", Clock)],
            LayoutEditor.Placements(moved.Value!).Select(p => (p.Panel, p.Zone, p.Instance)));
        var clock = (JsonObject)moved.Value!["panels"]![1]!["zones"]!["start"]![0]!;
        Assert.Equal("HH:mm", clock["settings"]!["format"]!.GetValue<string>());
    }

    [Fact]
    public void Moving_within_a_zone_reorders_it()
    {
        var added = LayoutEditor.Add(Layout(), Bottom, "end", "subzerodev.clock", "clock", Top).Value!;

        var moved = LayoutEditor.Move(added, Top, Bottom, "end", 0);

        Assert.Equal([Top, Clock], LayoutEditor.Placements(moved.Value!).Where(p => p.Zone == "end").Select(p => p.Instance));
    }

    [Fact]
    public void S11_6_removing_takes_the_instance_out_of_the_layout()
    {
        var removed = LayoutEditor.Remove(Layout(), Launcher);

        Assert.True(removed.Ok, removed.Error?.Detail);
        Assert.Equal([Clock], LayoutEditor.Placements(removed.Value!).Select(p => p.Instance));
        Assert.Equal(CoreErrorCode.NotFound, LayoutEditor.Remove(removed.Value!, Launcher).Error?.Code);
    }

    [Fact]
    public void Adding_a_panel_gives_an_empty_reserving_panel_on_that_edge()
    {
        var panel = Guid.NewGuid();

        var added = LayoutEditor.AddPanel(Layout(), PanelEdge.Left, panel);

        Assert.True(added.Ok, added.Error?.Detail);
        Assert.Equal([Bottom, Top, panel], LayoutEditor.PanelIds(added.Value!));
        var problems = new List<string>();
        var parsed = Documents.ParseLayout(added.Value!, "example.layout", problems);
        Assert.Empty(problems);
        Assert.Equal(PanelEdge.Left, parsed!.Panels[2].Placement.Edge);
    }

    [Fact]
    public void Removing_a_panel_takes_its_instances_with_it()
    {
        var removed = LayoutEditor.RemovePanel(Layout(), Bottom);

        Assert.True(removed.Ok, removed.Error?.Detail);
        Assert.Equal([Top], LayoutEditor.PanelIds(removed.Value!));
        Assert.Empty(LayoutEditor.Placements(removed.Value!));
        Assert.Equal(CoreErrorCode.NotFound, LayoutEditor.RemovePanel(removed.Value!, Bottom).Error?.Code);
    }

    private static JsonObject Layout() => JsonNode.Parse($$"""
        {
          "schemaVersion": 1,
          "id": "example.layout",
          "displayName": "Example",
          "unknown": "first",
          "panels": [
            {
              "id": "{{Bottom}}",
              "monitor": "primary",
              "placement": { "edge": "bottom" },
              "thickness": 40,
              "behaviour": "reserve",
              "zones": {
                "start": [ { "id": "{{Launcher}}", "plugin": "subzerodev.launcher", "contribution": "launcher" } ],
                "end": [ { "id": "{{Clock}}", "plugin": "subzerodev.clock", "contribution": "clock", "settings": { "format": "HH:mm" } } ]
              }
            },
            {
              "id": "{{Top}}",
              "monitor": "primary",
              "placement": { "edge": "top" },
              "thickness": 32,
              "behaviour": "overlay"
            }
          ]
        }
        """)!.AsObject();
}

using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

public sealed class ConfigStoreTests
{
    private static JsonObject DefaultSettings() => new()
    {
        ["schemaVersion"] = 1,
        ["activePreset"] = "subzerodev.default",
        ["plugins"] = new JsonObject(),
        ["commandProviders"] = new JsonObject(),
        ["hotkeys"] = new JsonObject(),
        ["startAtSignIn"] = false,
        ["restartPolicy"] = new JsonObject { ["maxRestarts"] = 3, ["windowSeconds"] = 300 },
    };

    [Fact]
    public async Task Settings_are_missing_then_written_without_a_bom()
    {
        using var scratch = new Scratch();
        var store = new ConfigStore(scratch.Locations);
        Assert.Equal(DocumentHealth.Missing, store.Get(DocumentId.HostSettings).Health);

        var written = await store.WriteAsync(DocumentId.HostSettings, null, DefaultSettings(), TestContext.Current.CancellationToken);

        Assert.True(written.Ok, written.Error?.ToString());
        var bytes = await File.ReadAllBytesAsync(Path.Combine(scratch.DataRoot, "settings.json"), TestContext.Current.CancellationToken);
        Assert.NotEqual(0xEF, bytes[0]);
        var state = store.Get(DocumentId.HostSettings);
        Assert.Equal(DocumentHealth.Valid, state.Health);
        Assert.Equal(written.Value!.ContentHash, state.ContentHash);
    }

    [Fact]
    public async Task A_stale_hash_is_a_conflict()
    {
        using var scratch = new Scratch();
        var store = new ConfigStore(scratch.Locations);
        await store.WriteAsync(DocumentId.HostSettings, null, DefaultSettings(), TestContext.Current.CancellationToken);

        var second = await store.WriteAsync(DocumentId.HostSettings, "stale", DefaultSettings(), TestContext.Current.CancellationToken);

        Assert.False(second.Ok);
        Assert.Equal(CoreErrorCode.Conflict, second.Error!.Code);
    }

    [Fact]
    public async Task S5_8_a_write_based_on_an_old_hash_is_a_conflict_and_keeps_the_external_edit()
    {
        using var scratch = new Scratch();
        var store = new ConfigStore(scratch.Locations);
        var first = await store.WriteAsync(DocumentId.HostSettings, null, DefaultSettings(), TestContext.Current.CancellationToken);
        var path = Path.Combine(scratch.DataRoot, "settings.json");
        var external = DefaultSettings();
        external["activePreset"] = "someone.else";
        await File.WriteAllTextAsync(path, external.ToJsonString(), TestContext.Current.CancellationToken);
        var mine = DefaultSettings();
        mine["activePreset"] = "my.preset";

        var second = await store.WriteAsync(DocumentId.HostSettings, first.Value!.ContentHash, mine, TestContext.Current.CancellationToken);

        Assert.Equal(CoreErrorCode.Conflict, second.Error!.Code);
        Assert.Equal(external.ToJsonString(), await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task S5_7_broken_json_is_invalid_read_only_and_never_overwritten()
    {
        using var scratch = new Scratch();
        var path = Path.Combine(scratch.DataRoot, "settings.json");
        await File.WriteAllTextAsync(path, "{ not json", TestContext.Current.CancellationToken);
        var store = new ConfigStore(scratch.Locations);

        var state = store.Get(DocumentId.HostSettings);
        var write = await store.WriteAsync(DocumentId.HostSettings, state.ContentHash, DefaultSettings(), TestContext.Current.CancellationToken);
        var blind = await store.WriteAsync(DocumentId.HostSettings, null, DefaultSettings(), TestContext.Current.CancellationToken);

        Assert.Equal(DocumentHealth.Invalid, state.Health);
        Assert.Equal(CoreErrorCode.DocumentReadOnly, write.Error!.Code);
        Assert.False(blind.Ok);
        Assert.Equal("{ not json", await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task S5_7_the_last_good_copy_outlives_a_broken_edit()
    {
        using var scratch = new Scratch();
        var settings = DefaultSettings();
        settings["activePreset"] = "kept.preset";
        await new ConfigStore(scratch.Locations).WriteAsync(DocumentId.HostSettings, null, settings, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(scratch.DataRoot, "settings.json"), "{ broken", TestContext.Current.CancellationToken);
        var store = new ConfigStore(scratch.Locations);

        var current = store.Get(DocumentId.HostSettings);
        var lastGood = store.GetLastGood(DocumentId.HostSettings);

        Assert.Equal(DocumentHealth.Invalid, current.Health);
        Assert.Equal(DocumentHealth.Valid, lastGood.Health);
        Assert.Equal("kept.preset", (string?)lastGood.Content!["activePreset"]);
    }

    [Fact]
    public async Task A_valid_hand_edit_replaces_the_last_good_copy_when_loaded()
    {
        using var scratch = new Scratch();
        var settings = DefaultSettings();
        settings["activePreset"] = "hand.edited";
        await File.WriteAllTextAsync(Path.Combine(scratch.DataRoot, "settings.json"), settings.ToJsonString(), TestContext.Current.CancellationToken);
        var store = new ConfigStore(scratch.Locations);

        Assert.Equal(DocumentHealth.Valid, store.Get(DocumentId.HostSettings).Health);
        Assert.Equal("hand.edited", (string?)store.GetLastGood(DocumentId.HostSettings).Content!["activePreset"]);
    }

    [Fact]
    public void Without_a_last_good_copy_it_is_missing()
    {
        using var scratch = new Scratch();

        Assert.Equal(DocumentHealth.Missing, new ConfigStore(scratch.Locations).GetLastGood(DocumentId.HostSettings).Health);
    }

    [Fact]
    public async Task Deleting_a_document_drops_its_last_good_copy()
    {
        using var scratch = new Scratch();
        var store = new ConfigStore(scratch.Locations);
        var written = await store.WriteAsync(DocumentId.HostSettings, null, DefaultSettings(), TestContext.Current.CancellationToken);
        Assert.Equal(DocumentHealth.Valid, store.GetLastGood(DocumentId.HostSettings).Health);

        var deleted = await store.DeleteAsync(DocumentId.HostSettings, written.Value!.ContentHash, TestContext.Current.CancellationToken);

        Assert.True(deleted.Ok, deleted.Error?.ToString());
        Assert.Equal(DocumentHealth.Missing, store.GetLastGood(DocumentId.HostSettings).Health);
    }

    [Fact]
    public void A_single_palette_must_be_a_package_id()
    {
        var preset = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "my.preset",
            ["displayName"] = "Mine",
            ["layout"] = "subzerodev.default",
            ["theme"] = "subzerodev.default",
            ["palette"] = @"..\outside",
        };
        var problems = new List<string>();

        Assert.Null(Documents.ParsePreset(preset, "my.preset", problems));
        Assert.Contains(problems, p => p.StartsWith("palette:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_newer_schema_is_too_new()
    {
        using var scratch = new Scratch();
        var settings = DefaultSettings();
        settings["schemaVersion"] = 2;
        await File.WriteAllTextAsync(Path.Combine(scratch.DataRoot, "settings.json"), settings.ToJsonString(), TestContext.Current.CancellationToken);

        var state = new ConfigStore(scratch.Locations).Get(DocumentId.HostSettings);

        Assert.Equal(DocumentHealth.TooNew, state.Health);
    }

    [Fact]
    public void Built_in_preset_and_layout_are_valid()
    {
        using var scratch = new Scratch();
        var store = new ConfigStore(scratch.Locations);

        var preset = store.Get(new DocumentId(DocumentKind.Preset, "subzerodev.default"));
        var layout = store.Get(new DocumentId(DocumentKind.Layout, "subzerodev.default"));

        Assert.Equal(DocumentHealth.Valid, preset.Health);
        Assert.Equal(DocumentHealth.Valid, layout.Health);
        var parsed = Documents.ParseLayout(layout.Content!, "subzerodev.default", []);
        var panel = Assert.Single(parsed!.Panels);
        Assert.Equal(PanelEdge.Bottom, panel.Placement.Edge);
        Assert.Equal(40, panel.Thickness);
    }

    [Fact]
    public void A_user_layout_wins_over_the_built_in_copy()
    {
        using var scratch = new Scratch();
        var layouts = Directory.CreateDirectory(Path.Combine(scratch.DataRoot, "layouts")).FullName;
        File.WriteAllText(Path.Combine(layouts, "subzerodev.default.json"), """
            { "schemaVersion": 1, "id": "subzerodev.default", "displayName": "Mine", "panels": [] }
            """);

        var layout = new ConfigStore(scratch.Locations).Get(new DocumentId(DocumentKind.Layout, "subzerodev.default"));

        Assert.Equal("Mine", (string?)layout.Content!["displayName"]);
    }

    [Fact]
    public void Layout_problems_name_the_field()
    {
        var problems = new List<string>();
        var json = JsonNode.Parse("""
            { "schemaVersion": 1, "id": "x.y", "displayName": "X",
              "panels": [ { "id": "not-a-guid", "monitor": "primary", "placement": { "edge": "sideways" }, "thickness": 40, "behaviour": "reserve",
                            "zones": { "middle": [] } } ] }
            """)!.AsObject();

        var layout = Documents.ParseLayout(json, "x.y", problems);

        Assert.Null(layout);
        Assert.Contains(problems, p => p.Contains("panels[0].id", StringComparison.Ordinal));
        Assert.Contains(problems, p => p.Contains("zones.middle", StringComparison.Ordinal));
    }
}

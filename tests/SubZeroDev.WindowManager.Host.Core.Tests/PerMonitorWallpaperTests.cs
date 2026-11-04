using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S30.2: presets at schema v2 carry a wallpaper per monitor, and a v1 preset still loads for every monitor.</summary>
public sealed class PerMonitorWallpaperTests
{
    private static JsonObject Preset(int version, JsonNode? wallpaper) => new()
    {
        ["schemaVersion"] = version,
        ["id"] = "my.preset",
        ["displayName"] = "Mine",
        ["layout"] = "subzerodev.default",
        ["theme"] = "subzerodev.default",
        ["wallpaper"] = wallpaper,
    };

    [Fact]
    public void S30_2_a_v2_preset_reads_the_default_and_each_monitors_own_wallpaper()
    {
        var problems = new List<string>();
        var wallpaper = JsonNode.Parse("""
            { "default": { "image": "dunes", "fit": "fit" },
              "monitors": { "MON-B": { "image": "night", "fit": "center" } } }
            """);

        var preset = Documents.ParsePreset(Preset(2, wallpaper), "my.preset", problems);

        Assert.Empty(problems);
        Assert.Equal(new PresetWallpaper("dunes", WallpaperFit.Fit), preset!.Wallpaper);
        Assert.Equal(new PresetWallpaper("night", WallpaperFit.Center), preset.MonitorWallpapers["MON-B"]);
        Assert.Equal(["night", "dunes"], preset.WallpapersFor("MON-B").Select(w => w.Image));
        Assert.Equal(["dunes"], preset.WallpapersFor("MON-A").Select(w => w.Image));
    }

    [Fact]
    public void S30_2_a_v1_preset_still_loads_and_applies_to_every_monitor()
    {
        var problems = new List<string>();

        var preset = Documents.ParsePreset(Preset(1, new JsonObject { ["image"] = "dunes", ["fit"] = "stretch" }), "my.preset", problems);

        Assert.Empty(problems);
        Assert.Equal(new PresetWallpaper("dunes", WallpaperFit.Stretch), preset!.Wallpaper);
        Assert.Empty(preset.MonitorWallpapers);
        Assert.All(new[] { "MON-A", "MON-B", "anything" }, id => Assert.Equal(["dunes"], preset.WallpapersFor(id).Select(w => w.Image)));
    }

    [Fact]
    public void S30_2_the_migration_wraps_the_old_object_as_the_default_and_leaves_it_alone()
    {
        var old = new JsonObject { ["image"] = "dunes", ["fit"] = "fill" };

        var migrated = Documents.MigrateWallpaperV1(old);

        Assert.Equal("""{"default":{"image":"dunes","fit":"fill"}}""", migrated.ToJsonString());
        Assert.Equal("""{"image":"dunes","fit":"fill"}""", old.ToJsonString());
        Assert.Null(migrated["monitors"]);
    }

    [Fact]
    public void S30_2_a_v2_preset_may_name_only_monitors_and_a_monitor_id_matches_regardless_of_case()
    {
        var wallpaper = JsonNode.Parse("""{ "monitors": { "Mon-B": { "image": "night" } } }""");

        var preset = Documents.ParsePreset(Preset(2, wallpaper), "my.preset", []);

        Assert.Null(preset!.Wallpaper);
        Assert.Equal(["night"], preset.WallpapersFor("MON-B").Select(w => w.Image));
        Assert.Empty(preset.WallpapersFor("MON-A"));
    }

    [Theory]
    [InlineData("""{ "default": { "fit": "fit" } }""", "wallpaper.default.image:")]
    [InlineData("""{ "default": { "image": "a", "fit": "tile" } }""", "wallpaper.default.fit:")]
    [InlineData("""{ "default": "a" }""", "wallpaper.default:")]
    [InlineData("""{ "monitors": { "M": { "image": " " } } }""", "wallpaper.monitors.M.image:")]
    [InlineData("""{ "monitors": { "M": "a" } }""", "wallpaper.monitors.M:")]
    [InlineData("""{ "monitors": [] }""", "wallpaper.monitors:")]
    [InlineData("""{ "monitors": { " ": { "image": "a" } } }""", "wallpaper.monitors:")]
    [InlineData("\"a.png\"", "wallpaper:")]
    public void S30_2_a_bad_v2_wallpaper_makes_the_preset_invalid(string wallpaper, string problem)
    {
        var problems = new List<string>();

        Assert.Null(Documents.ParsePreset(Preset(2, JsonNode.Parse(wallpaper)), "my.preset", problems));
        Assert.Contains(problems, p => p.StartsWith(problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(1, DocumentHealth.Valid)]
    [InlineData(2, DocumentHealth.Valid)]
    [InlineData(3, DocumentHealth.TooNew)]
    public void S30_2_the_store_reads_presets_up_to_v2_and_calls_a_newer_one_too_new(int version, DocumentHealth expected)
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(Path.Combine(scratch.DataRoot, "presets"));
        File.WriteAllText(Path.Combine(scratch.DataRoot, "presets", "my.preset.json"), Preset(version, null).ToJsonString());

        var state = new ConfigStore(scratch.Locations).Get(new DocumentId(DocumentKind.Preset, "my.preset"));

        Assert.Equal(expected, state.Health);
    }

    [Fact]
    public void S30_2_the_built_in_presets_ship_at_v2_and_load()
    {
        var files = Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "content", "presets"), "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            var json = JsonNode.Parse(File.ReadAllText(file))!.AsObject();
            Assert.Equal(Documents.PresetSchemaVersion, (int)json["schemaVersion"]!);
            var problems = new List<string>();
            Assert.NotNull(Documents.ParsePreset(json, Path.GetFileNameWithoutExtension(file), problems));
            Assert.Empty(problems);
        }
    }
}

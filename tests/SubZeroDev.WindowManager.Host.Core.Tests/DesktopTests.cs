using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Platform;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

public sealed class DesktopTests
{
    private static readonly byte[] Png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    private static JsonObject Preset(JsonNode? wallpaper) => new()
    {
        ["schemaVersion"] = 1,
        ["id"] = "my.preset",
        ["displayName"] = "Mine",
        ["layout"] = "subzerodev.default",
        ["theme"] = "subzerodev.default",
        ["wallpaper"] = wallpaper,
    };

    [Theory]
    [InlineData("10", false, WallpaperFit.Fill)]
    [InlineData("22", false, WallpaperFit.Fill)]
    [InlineData("6", false, WallpaperFit.Fit)]
    [InlineData("2", false, WallpaperFit.Stretch)]
    [InlineData("0", false, WallpaperFit.Center)]
    [InlineData("0", true, WallpaperFit.Center)]
    [InlineData(null, false, WallpaperFit.Fill)]
    public void S20_1_the_windows_wallpaper_style_maps_to_a_fit(string? style, bool tile, WallpaperFit expected) =>
        Assert.Equal(expected, SystemWallpaper.FitOf(style, tile));

    [Fact]
    public void S20_1_a_preset_wallpaper_is_read_with_its_fit()
    {
        var problems = new List<string>();

        var preset = Documents.ParsePreset(Preset(new JsonObject { ["image"] = "walls/a.png", ["fit"] = "fit" }), "my.preset", problems);

        Assert.Empty(problems);
        Assert.Equal(new PresetWallpaper("walls/a.png", WallpaperFit.Fit), preset!.Wallpaper);
    }

    [Fact]
    public void S20_1_a_preset_without_a_wallpaper_leaves_the_system_one()
    {
        var problems = new List<string>();
        var json = Preset(null);
        json.Remove("wallpaper");

        var preset = Documents.ParsePreset(json, "my.preset", problems);

        Assert.Empty(problems);
        Assert.Null(preset!.Wallpaper);
    }

    [Fact]
    public void S20_1_the_fit_defaults_to_fill()
    {
        var preset = Documents.ParsePreset(Preset(new JsonObject { ["image"] = "a" }), "my.preset", []);

        Assert.Equal(WallpaperFit.Fill, preset!.Wallpaper!.Fit);
    }

    [Theory]
    [InlineData("""{ "image": "a", "fit": "tile" }""", "wallpaper.fit:")]
    [InlineData("""{ "fit": "fit" }""", "wallpaper.image:")]
    [InlineData("""{ "image": "  " }""", "wallpaper.image:")]
    [InlineData("\"walls/a.png\"", "wallpaper:")]
    public void S20_1_a_bad_wallpaper_makes_the_preset_invalid(string wallpaper, string problem)
    {
        var problems = new List<string>();

        Assert.Null(Documents.ParsePreset(Preset(JsonNode.Parse(wallpaper)), "my.preset", problems));
        Assert.Contains(problems, p => p.StartsWith(problem, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("fill", WallpaperFit.Fill)]
    [InlineData("fit", WallpaperFit.Fit)]
    [InlineData("stretch", WallpaperFit.Stretch)]
    [InlineData("center", WallpaperFit.Center)]
    public void S20_1_the_four_fits_parse(string text, WallpaperFit expected)
    {
        Assert.True(Documents.TryParseFit(text, out var fit));
        Assert.Equal(expected, fit);
    }

    [Theory]
    [InlineData("tile")]
    [InlineData("Fit")]
    [InlineData("")]
    public void S20_1_other_fits_do_not_parse_as_layout_words_are_lower_case(string text) =>
        Assert.False(Documents.TryParseFit(text, out _));

    [Fact]
    public void S20_1_a_pack_wallpaper_is_found_by_relative_path_or_by_name()
    {
        using var scratch = new Scratch();
        var root = Path.Combine(scratch.DataRoot, "assetpacks", "test.walls");
        Directory.CreateDirectory(Path.Combine(root, "walls"));
        File.WriteAllBytes(Path.Combine(root, "walls", "Dunes.png"), Png);
        File.WriteAllBytes(Path.Combine(root, "walls", "night.jpg"), Png);
        File.WriteAllText(Path.Combine(root, AssetPackReader.FileName),
            """{ "id": "test.walls", "icons": {}, "wallpapers": ["walls/Dunes.png", "walls/night.jpg"] }""");

        var chain = AssetPackReader.BuildChain(scratch.Locations, ["test.walls"], null, null);

        Assert.Equal(Path.Combine(root, "walls", "Dunes.png"), chain.FindWallpaper(@"walls\dunes.png")!.Path);
        Assert.Equal(Path.Combine(root, "walls", "night.jpg"), chain.FindWallpaper("NIGHT")!.Path);
        Assert.Equal("pack:test.walls", chain.FindWallpaper("dunes")!.Pack.Label);
        Assert.Null(chain.FindWallpaper("missing"));
    }

    [Fact]
    public void S20_1_a_wallpaper_that_is_not_an_image_is_a_problem_and_never_matched()
    {
        using var scratch = new Scratch();
        var root = Path.Combine(scratch.DataRoot, "assetpacks", "test.walls");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "a.gif"), "x");
        File.WriteAllText(Path.Combine(root, AssetPackReader.FileName), """{ "id": "test.walls", "wallpapers": ["a.gif"] }""");

        var chain = AssetPackReader.BuildChain(scratch.Locations, ["test.walls"], null, null);

        var pack = Assert.Single(chain.Packs, p => p.Id == "test.walls");
        // As with a missing icon, the rest of the pack still applies.
        Assert.Equal(PackageHealth.Valid, pack.Health);
        Assert.Empty(pack.Wallpapers);
        Assert.Contains(pack.Problems, p => p.Detail == "Wallpapers must be PNG, JPEG or BMP.");
        Assert.Null(chain.FindWallpaper("a"));
    }

    [Fact]
    public void S20_5_a_plugin_may_declare_a_gadget()
    {
        var problems = new List<string>();

        var manifest = PluginManifestReader.Parse(JsonNode.Parse("""
            {"manifestVersion":1,"id":"samples.gadget","version":"1.0.0","displayName":"P","publisher":"T","description":"D",
             "hostApi":"^1.0","entry":{"path":"p.exe"},"modes":["coexistence"],
             "contributions":[{"id":"g","kind":"gadget","displayName":"G","multiInstance":true},{"id":"x","kind":"widget"}]}
            """)!.AsObject(), problems);

        Assert.Null(manifest);
        Assert.Equal(["contribution 'x' has kind 'widget'; only panelItem and gadget are defined."], problems);
    }
}

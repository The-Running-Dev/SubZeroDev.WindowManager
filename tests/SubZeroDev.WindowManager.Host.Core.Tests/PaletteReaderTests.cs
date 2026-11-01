using SubZeroDev.WindowManager.Host.Theming;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

public sealed class PaletteReaderTests
{
    [Fact]
    public void The_built_in_palettes_read()
    {
        using var scratch = new Scratch();

        foreach (var id in new[] { "subzerodev.light", "subzerodev.dark" })
        {
            var (palette, errors) = PaletteReader.Read(scratch.Locations, id);

            Assert.Empty(errors);
            Assert.Equal(id, palette!.Id);
            Assert.True(palette.Colors.ContainsKey("accent"));
        }
    }

    [Fact]
    public void A_user_palette_wins_over_the_built_in_one_and_may_live_in_a_folder()
    {
        using var scratch = new Scratch();
        var folder = Directory.CreateDirectory(Path.Combine(scratch.DataRoot, "palettes", "subzerodev.light")).FullName;
        File.WriteAllText(Path.Combine(folder, "palette.json"), """{ "id": "subzerodev.light", "colors": { "accent": "#FF112233" } }""");

        var (palette, errors) = PaletteReader.Read(scratch.Locations, "subzerodev.light");

        Assert.Empty(errors);
        Assert.Equal(0xFF112233u, Assert.Single(palette!.Colors).Value);
        Assert.StartsWith(scratch.DataRoot, palette.Path, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_missing_palette_is_an_asset_missing_error()
    {
        using var scratch = new Scratch();

        var (palette, errors) = PaletteReader.Read(scratch.Locations, "nobody.palette");

        Assert.Null(palette);
        Assert.Equal(ThemeErrorCode.AssetMissing, Assert.Single(errors).Code);
    }

    [Fact]
    public void A_bad_colour_names_the_token()
    {
        using var scratch = new Scratch();
        Directory.CreateDirectory(Path.Combine(scratch.DataRoot, "palettes"));
        File.WriteAllText(Path.Combine(scratch.DataRoot, "palettes", "bad.json"), """{ "colors": { "accent": "blue" } }""");

        var (palette, errors) = PaletteReader.Read(scratch.Locations, "bad");

        Assert.Null(palette);
        var error = Assert.Single(errors);
        Assert.Equal(ThemeErrorCode.PaletteInvalid, error.Code);
        Assert.Equal("accent", error.Key);
    }

    [Fact]
    public void A_palette_id_cannot_escape_the_palettes_folder()
    {
        using var scratch = new Scratch();
        File.WriteAllText(Path.Combine(scratch.DataRoot, "outside.json"), """{ "colors": {} }""");

        Assert.Null(PaletteReader.Locate(scratch.Locations, @"..\outside"));
    }

    [Fact]
    public void A_palette_id_that_is_not_a_path_is_not_found()
    {
        using var scratch = new Scratch();

        Assert.Null(PaletteReader.Locate(scratch.Locations, "bad\0id"));
        Assert.Equal(ThemeErrorCode.AssetMissing, Assert.Single(PaletteReader.Read(scratch.Locations, "bad\0id").Errors).Code);
    }

    [Theory]
    [InlineData("one", null, null, false, "one")]
    [InlineData("one", "light", "dark", true, "one")]
    [InlineData(null, "light", "dark", false, "light")]
    [InlineData(null, "light", "dark", true, "dark")]
    [InlineData(null, "light", null, true, "light")]
    [InlineData(null, null, "dark", false, "dark")]
    [InlineData(null, null, null, true, null)]
    public void S5_6_the_selection_follows_the_app_mode(string? single, string? light, string? dark, bool isDark, string? expected) =>
        Assert.Equal(expected, PaletteReader.Resolve(new PaletteSelection(single, light, dark), isDark));
}

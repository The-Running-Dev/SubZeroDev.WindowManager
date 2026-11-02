using System.Text.Json.Nodes;
using System.Xml.Linq;
using SubZeroDev.WindowManager.Host.Theming;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

public sealed class SampleLooksTests
{
    private const string Glass = "subzerodev.sample-glass";
    private const string Retro = "subzerodev.sample-retro";
    private static readonly XNamespace Ui = ThemePackageReader.PresentationNamespace;
    private static readonly XNamespace X = ThemePackageReader.XamlNamespace;

    private sealed record Look(PresetDocument Preset, ThemePackage Theme, Palette Palette, AssetPack Pack, XDocument Xaml);

    private static Look Load(Scratch scratch, string presetId)
    {
        var path = Path.Combine(scratch.Locations.ContentRoot, "presets", presetId + ".json");
        var problems = new List<string>();
        var preset = Documents.ParsePreset((JsonObject)JsonNode.Parse(File.ReadAllText(path))!, presetId, problems);
        Assert.Empty(problems);
        Assert.NotNull(preset);

        var (palette, paletteErrors) = PaletteReader.Read(scratch.Locations, preset.Palette!);
        Assert.Empty(paletteErrors);
        var (theme, themeErrors) = ThemePackageReader.Read(scratch.Locations, preset.Theme, builtInOnly: true, palette!.Colors);
        Assert.Empty(themeErrors);
        Assert.Equal(ThemeSource.BuiltIn, theme!.Source);

        var packId = Assert.Single(preset.AssetPacks);
        var packRoot = AssetPackReader.Locate(scratch.Locations, packId);
        Assert.NotNull(packRoot);
        var pack = AssetPackReader.Read(packRoot, packId, AssetPackOrigin.Preset);

        return new Look(preset, theme, palette, pack, XDocument.Parse(Assert.Single(theme.Resources).Xaml));
    }

    private static XElement Style(XDocument xaml, string key) =>
        xaml.Descendants(Ui + "Style").Single(style => (string?)style.Attribute(X + "Key") == key);

    private static string? Setter(XElement style, string property) =>
        (string?)style.Elements(Ui + "Setter").SingleOrDefault(setter => (string?)setter.Attribute("Property") == property)?.Attribute("Value");

    private static string PanelShape(XDocument xaml)
    {
        var border = Style(xaml, "Wm.Panel").Descendants(Ui + "Border").First();
        return $"{(string?)border.Attribute("CornerRadius")}|{(string?)border.Attribute("Margin")}";
    }

    private static HashSet<string> ButtonTransitionDurations(XDocument xaml) =>
        Style(xaml, "Wm.Element.Button").Descendants(Ui + "VisualTransition")
            .Select(transition => (string?)transition.Attribute("GeneratedDuration") ?? "")
            .ToHashSet(StringComparer.Ordinal);

    [Theory]
    [InlineData(Glass)]
    [InlineData(Retro)]
    public void S14_1_each_sample_preset_names_a_layout_theme_palette_and_pack_that_ship_and_are_valid(string presetId)
    {
        using var scratch = new Scratch();

        var look = Load(scratch, presetId);

        Assert.Equal(presetId, look.Preset.Theme);
        Assert.Equal(presetId, look.Preset.Palette);
        Assert.Equal(PackageHealth.Valid, look.Pack.Health);
        Assert.Empty(look.Pack.Problems);
        foreach (var name in new[] { "launcher", "action.search", "action.settings", "action.close", "action.run", "app.default", "clock", "window" })
        {
            Assert.True(look.Pack.Icons.ContainsKey(name), $"{presetId} has no '{name}' icon.");
        }

        foreach (var (token, colour) in look.Palette.Colors)
        {
            Assert.Equal(colour, look.Theme.Tokens[token]);
        }

        var layoutPath = Path.Combine(scratch.Locations.ContentRoot, "layouts", look.Preset.Layout + ".json");
        var problems = new List<string>();
        var layout = Documents.ParseLayout((JsonObject)JsonNode.Parse(File.ReadAllText(layoutPath))!, look.Preset.Layout, problems);
        Assert.Empty(problems);
        var panel = Assert.Single(layout!.Panels);
        Assert.Contains(panel.Zones.SelectMany(zone => zone.Value), item => item.Plugin == "subzerodev.running-windows");
    }

    [Fact]
    public void S14_2_the_samples_share_a_layout_and_differ_in_shape_backdrop_font_accent_icons_and_transitions()
    {
        using var scratch = new Scratch();

        var glass = Load(scratch, Glass);
        var retro = Load(scratch, Retro);

        Assert.Equal(glass.Preset.Layout, retro.Preset.Layout);
        Assert.NotEqual(PanelShape(glass.Xaml), PanelShape(retro.Xaml));
        Assert.NotEqual(glass.Theme.Backdrop, retro.Theme.Backdrop);
        Assert.NotEqual(glass.Theme.Tokens["accent"], retro.Theme.Tokens["accent"]);
        Assert.NotEqual(glass.Pack.Id, retro.Pack.Id);

        var glassFont = Setter(Style(glass.Xaml, "Wm.Element.Button"), "FontFamily");
        var retroFont = Setter(Style(retro.Xaml, "Wm.Element.Button"), "FontFamily");
        Assert.NotNull(glassFont);
        Assert.NotNull(retroFont);
        Assert.NotEqual(glassFont, retroFont);

        Assert.Equal("0", Assert.Single(ButtonTransitionDurations(retro.Xaml)));
        Assert.DoesNotContain("0", ButtonTransitionDurations(glass.Xaml));
    }
}

using SubZeroDev.WindowManager.Host.Theming;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

public sealed class ThemePackageReaderTests
{
    private const string ThemeId = ThemePackageReader.DefaultThemeId;

    [Fact]
    public void The_built_in_theme_reads_and_carries_its_tokens()
    {
        using var scratch = new Scratch();

        var (package, errors) = ThemePackageReader.Read(scratch.Locations, ThemeId, builtInOnly: false);

        Assert.Empty(errors);
        Assert.Equal(ThemeSource.BuiltIn, package!.Source);
        var resource = Assert.Single(package.Resources);
        Assert.Contains("x:Key=\"Wm.Brush.surface\"", resource.Xaml, StringComparison.Ordinal);
        Assert.Contains("x:Key=\"Wm.Color.danger\"", resource.Xaml, StringComparison.Ordinal);
    }

    [Fact]
    public void S5_2_and_S5_3_palette_colours_replace_their_tokens_and_the_rest_keep_the_theme_default()
    {
        using var scratch = new Scratch();
        var builtIn = ThemePackageReader.Read(scratch.Locations, ThemeId, builtInOnly: false).Package!;

        var (package, errors) = ThemePackageReader.Read(scratch.Locations, ThemeId, builtInOnly: false,
            new Dictionary<string, uint> { ["accent"] = 0xFF00FF00 });

        Assert.Empty(errors);
        Assert.Equal(0xFF00FF00u, package!.Tokens["accent"]);
        foreach (var (token, value) in builtIn.Tokens.Where(pair => pair.Key != "accent"))
        {
            Assert.Equal(value, package.Tokens[token]);
        }

        Assert.Contains("#FF00FF00", package.Resources[0].Xaml, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_user_copy_wins_unless_built_in_only()
    {
        using var scratch = new Scratch();
        scratch.CopyBuiltInTheme(ThemeId);

        Assert.Equal(ThemeSource.User, ThemePackageReader.Read(scratch.Locations, ThemeId, builtInOnly: false).Package!.Source);
        Assert.Equal(ThemeSource.BuiltIn, ThemePackageReader.Read(scratch.Locations, ThemeId, builtInOnly: true).Package!.Source);
    }

    [Fact]
    public void Broken_xaml_is_a_parse_error_naming_the_file()
    {
        using var scratch = new Scratch();
        var copy = scratch.CopyBuiltInTheme(ThemeId);
        var panel = Path.Combine(copy, "Panel.xaml");
        File.WriteAllText(panel, "<ResourceDictionary><Style");

        var (package, errors) = ThemePackageReader.Read(scratch.Locations, ThemeId, builtInOnly: false);

        Assert.Null(package);
        var error = Assert.Single(errors);
        Assert.Equal(ThemeErrorCode.XamlParse, error.Code);
        Assert.Equal(panel, error.File, ignoreCase: true);
    }

    [Fact]
    public void A_resource_outside_the_theme_folder_is_refused()
    {
        using var scratch = new Scratch();
        var copy = scratch.CopyBuiltInTheme(ThemeId);
        var metadata = Path.Combine(copy, "theme.json");
        File.WriteAllText(metadata, File.ReadAllText(metadata).Replace("\"Panel.xaml\"", "\"..\\\\..\\\\outside.xaml\"", StringComparison.Ordinal));

        var (_, errors) = ThemePackageReader.Read(scratch.Locations, ThemeId, builtInOnly: false);

        Assert.Contains(errors, e => e.Code == ThemeErrorCode.PathEscape);
    }

    [Fact]
    public void A_bad_token_colour_and_a_wrong_api_are_reported()
    {
        using var scratch = new Scratch();
        var copy = scratch.CopyBuiltInTheme(ThemeId);
        var metadata = Path.Combine(copy, "theme.json");
        File.WriteAllText(metadata, File.ReadAllText(metadata)
            .Replace("\"themeApi\": 1", "\"themeApi\": 9", StringComparison.Ordinal)
            .Replace("#FFE81123", "red", StringComparison.Ordinal));

        var (_, errors) = ThemePackageReader.Read(scratch.Locations, ThemeId, builtInOnly: false);

        Assert.Contains(errors, e => e.Code == ThemeErrorCode.ThemeApiMismatch);
        Assert.Contains(errors, e => e.Code == ThemeErrorCode.PaletteInvalid && e.Key == "danger");
    }

    [Theory]
    [InlineData("#FF102030", true, 0xFF102030u)]
    [InlineData("#102030", false, 0u)]
    [InlineData("FF102030", false, 0u)]
    public void Colours_must_be_argb(string text, bool ok, uint expected)
    {
        Assert.Equal(ok, ThemePackageReader.TryParseColour(text, out var argb));
        if (ok)
        {
            Assert.Equal(expected, argb);
            Assert.Equal(text, ThemePackageReader.FormatColour(argb));
        }
    }
}

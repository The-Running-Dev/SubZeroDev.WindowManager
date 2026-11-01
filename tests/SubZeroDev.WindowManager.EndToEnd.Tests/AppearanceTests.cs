using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>Slice S5: presets, themes and palettes change the look while the host runs.</summary>
public sealed class AppearanceTests
{
    private const string TestPlugin = "subzerodev.testplugin";
    private const string DefaultSurface = "#F01C1C1C";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S5_1_a_preset_with_another_theme_restyles_the_panel_within_a_second_without_a_worker_message()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        CopyThemeAs(run, "user.blue", json => json["tokens"]!["surface"] = "#FF224466");
        WritePreset(run, "user.blue", "user.blue", null);
        run.Start(HostRun.HostExe);
        // Every worker's initialize and instance.create go out before the baseline is taken: the clock and the launcher.
        var before = await run.WaitForAsync(s => HostRun.Texts(s, "subzerodev.clock").Count == 1
            && s["contributions"]!.AsArray().All(c => c!["reason"] is null), Startup);
        Assert.Equal(DefaultSurface, Background(before));
        var messages = (long)before["workerMessages"]!;

        var asked = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteSettings(run, "user.blue");
        var after = await run.WaitForAsync(s => (string?)s["theme"]!["id"] == "user.blue", Settle);

        Assert.Equal("#FF224466", Background(after));
        Assert.InRange((long)after["theme"]!["appliedAtUtcMs"]! - asked, 0, 1000);
        Assert.Equal(messages, (long)after["workerMessages"]!);
        Assert.Equal("user.blue", (string?)after["settings"]!["activePreset"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S5_2_and_S5_3_a_new_accent_changes_only_its_users_and_missing_tokens_keep_the_theme_default()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        StartStyledPlugin(run);
        var palette = WritePalette(run, "user.accent", "#FF00FF00");
        WritePreset(run, "user.accented", "subzerodev.default", JsonValue.Create("user.accent"));
        WriteSettings(run, "user.accented");
        run.Start(HostRun.HostExe);
        var before = await run.WaitForAsync(s => StyledText(s)?["foreground"]?.GetValue<string>() == "#FF00FF00", Startup);
        Assert.Equal("user.accent", (string?)before["theme"]!["palette"]);
        Assert.Equal(DefaultSurface, Background(before));

        File.WriteAllText(palette, PaletteJson("#FFFF00FF"));
        var after = await run.WaitForAsync(s => StyledText(s)?["foreground"]?.GetValue<string>() == "#FFFF00FF", Settle);

        Assert.Equal(DefaultSurface, Background(after));
        Assert.Equal(Background(before), Background(after));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S5_4_a_theme_without_Wm_Panel_is_refused_and_the_previous_look_stays()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        CopyThemeAs(run, "user.broken", json => json["tokens"]!["surface"] = "#FF660000",
            xaml => xaml.Replace("x:Key=\"Wm.Panel\"", "x:Key=\"Wm.NotThePanel\"", StringComparison.Ordinal));
        WritePreset(run, "user.broken", "user.broken", null);
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => Background(s) == DefaultSurface, Startup);

        WriteSettings(run, "user.broken");
        var after = await run.WaitForAsync(s => s["theme"]!["lastApply"]?["applied"]?.GetValue<bool>() == false, Settle);

        Assert.Contains(after["theme"]!["lastApply"]!["errors"]!.AsArray(), e => (string?)e!["code"] == "MissingRequiredKey");
        Assert.Equal("subzerodev.default", (string?)after["theme"]!["id"]);
        Assert.Equal(DefaultSurface, Background(after));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S5_4_a_broken_edit_to_the_active_preset_keeps_the_running_look()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        CopyThemeAs(run, "user.blue", json => json["tokens"]!["surface"] = "#FF224466");
        WritePreset(run, "user.blue", "user.blue", null);
        WriteSettings(run, "user.blue");
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => Background(s) == "#FF224466", Startup);

        run.WriteDataFile(Path.Combine("presets", "user.blue.json"), "{ \"schemaVersion\": 1, broken");
        var after = await run.WaitForAsync(_ => run.HostLog().Contains("Keeping preset 'user.blue'", StringComparison.Ordinal), Settle);

        Assert.Equal("user.blue", (string?)after["theme"]!["id"]);
        Assert.Equal("#FF224466", Background(after));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S5_5_classes_resolve_in_order_then_fall_back_to_the_element_style()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        StartStyledPlugin(run);
        var panelXaml = Path.Combine(run.CopyBuiltInTheme(), "Panel.xaml");
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => Style(s) == "Wm.Element.Text.primary", Startup);

        RemoveStyle(panelXaml, "Wm.Element.Text.primary");
        var compact = await run.WaitForAsync(s => Style(s) == "Wm.Element.Text.compact", Settle);
        Assert.Equal(11, (double)StyledText(compact)!["fontSize"]!);

        RemoveStyle(panelXaml, "Wm.Element.Text.compact");
        var plain = await run.WaitForAsync(s => Style(s) == "Wm.Element.Text", Settle);
        Assert.Equal("#FFF2F2F2", (string?)StyledText(plain)!["foreground"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S5_6_a_light_and_dark_preset_follows_the_app_mode_within_two_seconds()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var keyPath = $@"Software\SubZeroDev\WindowManager.Tests\{Guid.NewGuid():N}";
        using (var key = Registry.CurrentUser.CreateSubKey(keyPath))
        {
            key.SetValue("AppsUseLightTheme", 1, RegistryValueKind.DWord);
        }

        try
        {
            run.AppModeKey = keyPath;
            WritePreset(run, "user.modes", "subzerodev.default",
                new JsonObject { ["light"] = "subzerodev.light", ["dark"] = "subzerodev.dark" });
            WriteSettings(run, "user.modes");
            run.Start(HostRun.HostExe);
            var light = await run.WaitForAsync(s => (string?)s["theme"]!["palette"] == "subzerodev.light", Startup);
            Assert.Equal("#F0F3F3F3", Background(light));

            var switched = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            using (var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true)!)
            {
                key.SetValue("AppsUseLightTheme", 0, RegistryValueKind.DWord);
            }

            var dark = await run.WaitForAsync(s => (string?)s["theme"]!["palette"] == "subzerodev.dark", Settle);

            Assert.InRange((long)dark["theme"]!["appliedAtUtcMs"]! - switched, 0, 2000);
            Assert.Equal(DefaultSurface, Background(dark));
            Assert.Equal(0, await run.StopAsync());
        }
        finally
        {
            Registry.CurrentUser.DeleteSubKeyTree(keyPath, throwOnMissingSubKey: false);
        }
    }

    [Fact]
    public async Task S5_7_invalid_settings_are_never_overwritten_and_the_host_runs_on()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var settings = run.WriteDataFile("settings.json", "{ \"schemaVersion\": 1, broken");
        run.Start(HostRun.HostExe);
        var status = await run.WaitForAsync(s => s["panels"]!.AsArray().Count == 1, Startup);
        Assert.Equal("Invalid", (string?)status["settings"]!["health"]);

        File.WriteAllText(Path.Combine(run.HookDirectory, "preset"), "subzerodev.default");
        var applied = await run.WaitForAsync(s => s["lastPresetApply"] is JsonObject, Settle);

        Assert.Equal("DocumentReadOnly", (string?)applied["lastPresetApply"]!["code"]);
        Assert.Equal("{ \"schemaVersion\": 1, broken", File.ReadAllText(settings));
        Assert.Equal(DefaultSurface, Background(applied));
        Assert.Equal(0, await run.StopAsync());
    }

    private static string? Background(JsonObject status) =>
        status["panels"]!.AsArray().FirstOrDefault()?["background"]?.GetValue<string>();

    private static JsonObject? StyledText(JsonObject status) =>
        status["contributions"]!.AsArray()
            .Where(c => (string?)c!["plugin"] == TestPlugin && c["reason"] is null)
            .SelectMany(c => c!["textItems"]?.AsArray() ?? [])
            .FirstOrDefault() as JsonObject;

    private static string? Style(JsonObject status) => StyledText(status)?["style"]?.GetValue<string>();

    private static void RemoveStyle(string panelXaml, string key)
    {
        var xaml = File.ReadAllText(panelXaml);
        var pattern = $"<Style x:Key=\"{Regex.Escape(key)}\".*?</Style>";
        Assert.Matches(new Regex(pattern, RegexOptions.Singleline), xaml);
        File.WriteAllText(panelXaml, Regex.Replace(xaml, pattern, string.Empty, RegexOptions.Singleline));
    }

    private static void StartStyledPlugin(HostRun run)
    {
        run.CopyPlugin(HostRun.TestPluginDirectory, TestPlugin, ["styled"]);
        run.WriteDefaultLayout((Guid.NewGuid(), TestPlugin, "probe"));
    }

    private static void CopyThemeAs(HostRun run, string id, Action<JsonObject> editTheme, Func<string, string>? editPanel = null)
    {
        var copy = run.CopyBuiltInTheme();
        var target = Path.Combine(run.DataRoot, "themes", id);
        Directory.Move(copy, target);
        var themeJson = Path.Combine(target, "theme.json");
        var theme = JsonNode.Parse(File.ReadAllText(themeJson))!.AsObject();
        theme["id"] = id;
        editTheme(theme);
        File.WriteAllText(themeJson, theme.ToJsonString());
        if (editPanel is not null)
        {
            var panelXaml = Path.Combine(target, "Panel.xaml");
            File.WriteAllText(panelXaml, editPanel(File.ReadAllText(panelXaml)));
        }
    }

    private static void WritePreset(HostRun run, string id, string theme, JsonNode? palette)
    {
        var preset = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["displayName"] = id,
            ["layout"] = "subzerodev.default",
            ["theme"] = theme,
        };
        if (palette is not null)
        {
            preset["palette"] = palette;
        }

        run.WriteDataFile(Path.Combine("presets", id + ".json"), preset.ToJsonString());
    }

    private static void WriteSettings(HostRun run, string preset) =>
        run.WriteDataFile("settings.json", new JsonObject { ["schemaVersion"] = 1, ["activePreset"] = preset }.ToJsonString());

    private static string WritePalette(HostRun run, string id, string accent) =>
        run.WriteDataFile(Path.Combine("palettes", id + ".json"), PaletteJson(accent));

    private static string PaletteJson(string accent) =>
        new JsonObject { ["id"] = "user.accent", ["colors"] = new JsonObject { ["accent"] = accent } }.ToJsonString();
}

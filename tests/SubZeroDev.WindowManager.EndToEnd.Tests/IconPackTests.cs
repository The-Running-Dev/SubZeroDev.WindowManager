using System.Globalization;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S13: icons come from the preset's asset packs, then the theme's pack, then the host default.
/// The test plugin's <c>icons</c> scenario shows one Button per named icon; the running-windows strip shows
/// the fixture windows, whose executable an <c>appIcons</c> rule can match.
/// </summary>
public sealed class IconPackTests
{
    private const string TestPlugin = "subzerodev.testplugin";
    private const string RunningWindows = "subzerodev.running-windows";
    private const string Preset = "user.icons";
    private const string Theme = "user.packed";
    private const string FixtureExe = "SubZeroDev.WindowManager.TestPlugin.exe";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S13_1_and_S13_4_each_named_icon_comes_from_the_first_pack_that_has_it_and_a_reorder_reaches_no_worker()
    {
        HostRun.RequireDesktop();
        using var run = StartIcons(["test.a", "test.b"], (theme, folder) =>
        {
            theme["defaultAssetPack"] = "test.bundled";
            WritePack(Path.Combine(folder, "assetpacks"), "test.bundled", new JsonObject { ["wm.test.theme"] = "t.png" });
        });
        WritePack(Path.Combine(run.DataRoot, "assetpacks"), "test.a", new JsonObject { ["wm.test.first"] = "a1.png" });
        WritePack(Path.Combine(run.DataRoot, "assetpacks"), "test.b",
            new JsonObject { ["wm.test.first"] = "b1.png", ["wm.test.second"] = "b2.png" });
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => Source(s, "wm.test.first") == "pack:test.a"
            && s["contributions"]!.AsArray().All(c => c!["reason"] is null), Startup);
        Assert.Equal("pack:test.b", Source(status, "wm.test.second"));
        Assert.Equal("theme:test.bundled", Source(status, "wm.test.theme"));
        Assert.Equal("host:subzerodev.default", Source(status, "launcher"));
        Assert.Equal("named", Source(status, "wm.test.missing"));
        Assert.Equal(["pack:test.a", "pack:test.b", "theme:test.bundled", "host:subzerodev.default"],
            status["theme"]!["assetPacks"]!.AsArray().Select(p => (string)p!["label"]!).ToArray());
        var messages = (long)status["workerMessages"]!;

        WritePreset(run, Theme, ["test.b", "test.a"]);
        var after = await run.WaitForAsync(s => Source(s, "wm.test.first") == "pack:test.b", Settle);

        Assert.Equal("theme:test.bundled", Source(after, "wm.test.theme"));
        Assert.Equal(messages, (long)after["workerMessages"]!);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S13_2_an_app_icon_rule_replaces_a_running_windows_icon_within_a_second_of_the_preset_change()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WritePack(Path.Combine(run.DataRoot, "assetpacks"), "test.apps", [], new JsonArray(new JsonObject
        {
            ["match"] = new JsonObject { ["exe"] = FixtureExe },
            ["image"] = "fixture.png",
        }));
        WritePreset(run, "subzerodev.default", []);
        WriteSettings(run);
        run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows"));
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => s["contributions"]!.AsArray().Any(c => (string?)c!["plugin"] == RunningWindows && c["reason"] is null),
            Startup);

        using var fixture = RunningWindowsTests.FixtureWindows.Open();
        var key = ((long)fixture["normal"]).ToString(CultureInfo.InvariantCulture);
        var before = await run.WaitForAsync(s => WindowSource(s, key) is "extracted" or "default", Settle);
        Assert.NotEqual("pack:test.apps", WindowSource(before, key));

        var asked = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WritePreset(run, "subzerodev.default", ["test.apps"]);
        var after = await run.WaitForAsync(s => WindowSource(s, key) == "pack:test.apps", Settle);

        Assert.InRange((long)after["theme"]!["appliedAtUtcMs"]! - asked, 0, 1000);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S13_3_a_pack_that_escapes_its_folder_is_skipped_and_named_and_the_next_pack_answers()
    {
        HostRun.RequireDesktop();
        using var run = StartIcons(["test.escape", "test.a"], null);
        WritePack(Path.Combine(run.DataRoot, "assetpacks"), "test.escape",
            new JsonObject { ["wm.test.first"] = "../../outside.png", ["wm.test.second"] = "e2.png" });
        WritePack(Path.Combine(run.DataRoot, "assetpacks"), "test.a", new JsonObject { ["wm.test.first"] = "a1.png" });
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => Source(s, "wm.test.first") == "pack:test.a", Startup);

        var escape = status["theme"]!["assetPacks"]!.AsArray().Single(p => (string?)p!["id"] == "test.escape")!;
        Assert.Equal("Invalid", (string?)escape["health"]);
        Assert.Contains(escape["problems"]!.AsArray(), p => (string?)p!["code"] == "PathEscape");
        Assert.Equal("named", Source(status, "wm.test.second"));
        Assert.Contains("is skipped", run.HostLog(), StringComparison.Ordinal);
        Assert.Equal(0, await run.StopAsync());
    }

    /// <summary>
    /// Lays out the <c>icons</c> scenario under a preset of <paramref name="packs"/>. With
    /// <paramref name="editTheme"/>, the preset uses a copy of the default theme it may edit; it gets the
    /// theme's json and folder.
    /// </summary>
    private static HostRun StartIcons(string[] packs, Action<JsonObject, string>? editTheme)
    {
        var run = HostRun.Create();
        var theme = "subzerodev.default";
        if (editTheme is not null)
        {
            theme = Theme;
            var target = Path.Combine(run.DataRoot, "themes", Theme);
            Directory.Move(run.CopyBuiltInTheme(), target);
            var themeJson = Path.Combine(target, "theme.json");
            var json = JsonNode.Parse(File.ReadAllText(themeJson))!.AsObject();
            json["id"] = Theme;
            editTheme(json, target);
            File.WriteAllText(themeJson, json.ToJsonString());
        }

        run.CopyPlugin(HostRun.TestPluginDirectory, TestPlugin, ["icons"]);
        run.WriteDefaultLayout((Guid.NewGuid(), TestPlugin, "probe"));
        WritePreset(run, theme, packs);
        WriteSettings(run);
        return run;
    }

    /// <summary>Writes a pack whose every image is a copy of the host default's launcher icon.</summary>
    private static void WritePack(string folder, string id, JsonObject icons, JsonArray? appIcons = null)
    {
        var root = Path.Combine(folder, id);
        Directory.CreateDirectory(root);
        var source = Path.Combine(HostRun.ContentDirectory, "assetpacks", "subzerodev.default", "icons", "launcher.png");
        var images = icons.Select(pair => (string)pair.Value!)
            .Concat(appIcons?.Select(rule => (string)rule!["image"]!) ?? [])
            .Where(image => !image.Contains("..", StringComparison.Ordinal));
        foreach (var image in images)
        {
            File.Copy(source, Path.Combine(root, image), overwrite: true);
        }

        var pack = new JsonObject { ["id"] = id, ["version"] = "0.1.0", ["icons"] = icons, ["appIcons"] = appIcons ?? [] };
        File.WriteAllText(Path.Combine(root, "assetpack.json"), pack.ToJsonString());
    }

    private static void WritePreset(HostRun run, string theme, string[] packs) =>
        run.WriteDataFile(Path.Combine("presets", Preset + ".json"), new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = Preset,
            ["displayName"] = Preset,
            ["layout"] = "subzerodev.default",
            ["theme"] = theme,
            ["assetPacks"] = new JsonArray([.. packs.Select(p => (JsonNode?)p)]),
        }.ToJsonString());

    private static void WriteSettings(HostRun run) =>
        run.WriteDataFile("settings.json", new JsonObject { ["schemaVersion"] = 1, ["activePreset"] = Preset }.ToJsonString());

    private static string? Source(JsonObject status, string id) =>
        Interactive(status, TestPlugin).FirstOrDefault(e => (string?)e["id"] == id)?["iconSource"]?.GetValue<string>();

    private static string? WindowSource(JsonObject status, string key) =>
        Interactive(status, RunningWindows).FirstOrDefault(e => (string?)e["itemKey"] == key)?["iconSource"]?.GetValue<string>();

    private static IEnumerable<JsonObject> Interactive(JsonObject status, string plugin) =>
        status["contributions"]!.AsArray()
            .Where(c => (string?)c!["plugin"] == plugin && c["reason"] is null)
            .SelectMany(c => c!["interactive"]!.AsArray().Select(e => e!.AsObject()));
}

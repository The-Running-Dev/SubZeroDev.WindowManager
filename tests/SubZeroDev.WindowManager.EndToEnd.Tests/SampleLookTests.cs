using System.Security.Cryptography;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>Slice S14: the two shipped sample looks load in the real host and switch without touching the plugins.</summary>
public sealed class SampleLookTests
{
    private const string Glass = "subzerodev.sample-glass";
    private const string Retro = "subzerodev.sample-retro";
    private static readonly string[] Plugins = ["subzerodev.launcher", "subzerodev.running-windows", "subzerodev.clock"];
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan Settle = TimeSpan.FromSeconds(5);

    [Theory]
    [InlineData(Glass, "Acrylic")]
    [InlineData(Retro, "None")]
    public async Task S14_1_each_sample_preset_loads_in_the_host_with_no_theme_errors(string preset, string backdrop)
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteSettings(run, preset);
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => (string?)s["theme"]!["id"] == preset && Live(s), Startup);

        var theme = status["theme"]!;
        Assert.Equal("BuiltIn", (string?)theme["source"]);
        Assert.Equal(backdrop, (string?)theme["backdrop"]);
        Assert.Equal(preset, (string?)theme["palette"]);
        Assert.True((bool)theme["lastApply"]!["applied"]!);
        Assert.Empty(theme["lastApply"]!["errors"]!.AsArray());
        Assert.Empty(theme["errors"]!.AsArray());
        Assert.Contains(theme["assetPacks"]!.AsArray(), pack => (string?)pack!["label"] == "pack:" + preset && (string?)pack["health"] == "Valid");
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S14_3_switching_between_the_samples_restarts_no_worker_and_changes_no_plugin_settings()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteSettings(run, Glass);
        run.Start(HostRun.HostExe);
        var before = await run.WaitForAsync(s => (string?)s["theme"]!["id"] == Glass && Live(s), Startup);
        var pids = Pids(before);
        var files = PluginSettings(run);

        WriteSettings(run, Retro);
        var retro = await run.WaitForAsync(s => (string?)s["theme"]!["id"] == Retro, Settle);
        Assert.Equal("None", (string?)retro["theme"]!["backdrop"]);
        Assert.Equal(pids, Pids(retro));

        WriteSettings(run, Glass);
        var glass = await run.WaitForAsync(s => (string?)s["theme"]!["id"] == Glass, Settle);
        Assert.Equal("Acrylic", (string?)glass["theme"]!["backdrop"]);
        Assert.Equal(pids, Pids(glass));
        Assert.True(Live(glass));

        Assert.Equal(files, PluginSettings(run));
        Assert.Equal(0, await run.StopAsync());
    }

    private static bool Live(JsonObject status) =>
        Plugins.All(id => status["contributions"]!.AsArray().Any(c => (string?)c!["plugin"] == id && c["reason"] is null))
        && Plugins.All(id => status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == id && p["pid"] is not null));

    private static string Pids(JsonObject status) =>
        string.Join(",", Plugins.Select(id => (int?)status["plugins"]!.AsArray().First(p => (string?)p!["id"] == id)!["pid"]));

    private static string PluginSettings(HostRun run)
    {
        var folder = Path.Combine(run.DataRoot, "plugin-settings");
        if (!Directory.Exists(folder))
        {
            return "";
        }

        return string.Join("\n", Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Order(StringComparer.Ordinal)
            .Select(file => $"{Path.GetRelativePath(folder, file)}={Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}"));
    }

    private static void WriteSettings(HostRun run, string preset) =>
        run.WriteDataFile("settings.json", new JsonObject { ["schemaVersion"] = 1, ["activePreset"] = preset }.ToJsonString());
}

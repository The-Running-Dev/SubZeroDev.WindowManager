using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>Slice S9: settings change live, persist, are checked against the schema and migrate.</summary>
public sealed partial class SettingsTests
{
    private const string Clock = "subzerodev.clock";
    private const string TestPlugin = "subzerodev.testplugin";

    // The first run of a newly written worker exe waits on the virus scanner; S16 measures the real budgets.
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S9_1_editing_the_clock_format_shows_seconds_without_restarting_the_worker()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var instance = Guid.NewGuid();
        run.WriteDefaultLayout((instance, Clock, "clock"));
        run.Start(HostRun.HostExe);
        var status = await run.WaitForAsync(s => PluginPid(s, Clock) is not null && ClockText(s) is { } t && Minutes().IsMatch(t), Startup);
        var pid = PluginPid(status, Clock);

        var edited = Stopwatch.StartNew();
        run.WriteDefaultLayout((instance, Clock, "clock", new JsonObject { ["format"] = "HH:mm:ss" }));
        status = await run.WaitForAsync(s => ClockText(s) is { } t && Seconds().IsMatch(t), TimeSpan.FromSeconds(4));

        Assert.True(edited.Elapsed < TimeSpan.FromSeconds(2), $"Seconds showed after {edited.Elapsed}.");
        Assert.Equal(pid, PluginPid(status, Clock));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S9_2_saved_settings_survive_a_host_restart()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, TestPlugin, ["save-settings"], CountSchema);
        run.WriteDefaultLayout((Guid.NewGuid(), TestPlugin, "probe"));
        var log = SettingsLog(run);

        run.Start(HostRun.HostExe);
        await WaitForLinesAsync(log, lines => lines.Contains("saved ok"));
        Assert.Equal(0, await run.StopAsync());
        File.Delete(Path.Combine(run.HookDirectory, "exit"));
        File.Delete(Path.Combine(run.HookDirectory, "status.json"));

        run.Start(HostRun.HostExe);
        var lines = await WaitForLinesAsync(log, lines => lines.Count(line => line == "saved ok") == 2);
        Assert.Equal(["start {}", "saved ok", """start {"count":1}""", "saved ok"], lines);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S9_3_a_save_that_fails_the_schema_is_refused_with_1008_and_writes_nothing()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, TestPlugin, ["save-invalid"], CountSchema);
        run.WriteDefaultLayout((Guid.NewGuid(), TestPlugin, "probe"));
        var file = WriteStored(run, TestPlugin, 1, new JsonObject { ["count"] = 5 });
        var before = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);

        run.Start(HostRun.HostExe);
        var lines = await WaitForLinesAsync(SettingsLog(run), lines => lines.Any(line => line.StartsWith("saved", StringComparison.Ordinal)));

        Assert.Equal(["""start {"count":5}""", "saved SettingsInvalid 1008"], lines);
        Assert.Equal(before, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S9_4_version_1_settings_read_by_a_version_2_plugin_are_migrated_and_the_old_file_kept()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(Path.Combine(HostRun.ContentDirectory, "plugins", Clock), Clock, edit: manifest =>
        {
            manifest["settingsVersion"] = 2;
            manifest["settingsSchema"] = Schema("zone", "string");
        });
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        var file = WriteStored(run, Clock, 1, new JsonObject { ["zone"] = "local" });
        var before = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);
        var backup = Path.Combine(run.DataRoot, "plugin-settings", $"{Clock}.v1.bak");

        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => PluginPid(s, Clock) is not null, Startup);

        Assert.Equal(before, await File.ReadAllBytesAsync(backup, TestContext.Current.CancellationToken));
        var migrated = JsonNode.Parse(await File.ReadAllTextAsync(file, TestContext.Current.CancellationToken))!;
        Assert.Equal(2, (int?)migrated["settingsVersion"]);
        Assert.Equal("local", (string?)migrated["settings"]!["zone"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S9_5_a_migration_that_throws_starts_the_plugin_with_defaults_and_leaves_the_file()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, TestPlugin, ["migrate-throws"], manifest =>
        {
            CountSchema(manifest);
            manifest["settingsVersion"] = 2;
        });
        run.WriteDefaultLayout((Guid.NewGuid(), TestPlugin, "probe"));
        var file = WriteStored(run, TestPlugin, 1, new JsonObject { ["count"] = 3 });
        var before = await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken);

        run.Start(HostRun.HostExe);
        var lines = await WaitForLinesAsync(SettingsLog(run), lines => lines.Length > 0);
        await run.WaitForAsync(s => HostRun.Texts(s, TestPlugin).Count == 1, Startup);

        Assert.Equal(["start {}"], lines);
        Assert.Equal(before, await File.ReadAllBytesAsync(file, TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(run.DataRoot, "plugin-settings", $"{TestPlugin}.v1.bak")));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S9_6_a_schema_keyword_outside_the_subset_makes_the_plugin_invalid_and_is_named()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        const string odd = "subzerodev.oneof";
        run.CopyPlugin(HostRun.TestPluginDirectory, odd, edit: manifest =>
        {
            var schema = Schema("count", "integer");
            schema["properties"]!["count"]!["oneOf"] = new JsonArray(new JsonObject { ["minimum"] = 1 });
            manifest["contributions"]![0]!["settingsSchema"] = schema;
        });
        run.Start(HostRun.HostExe);

        await run.WaitForAsync(
            s => s["catalog"]!.AsArray().Any(p => (string?)p!["id"] == odd && (string?)p["health"] == "Invalid"), Startup);

        Assert.Contains("oneOf", run.HostLog(), StringComparison.Ordinal);
        Assert.Equal(0, await run.StopAsync());
    }

    private static void CountSchema(JsonObject manifest) => manifest["settingsSchema"] = Schema("count", "integer");

    private static JsonObject Schema(string property, string type) => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject { [property] = new JsonObject { ["type"] = type } },
    };

    private static string SettingsLog(HostRun run) => Path.Combine(run.DataRoot, "plugin-data", TestPlugin, "settings.log");

    private static string WriteStored(HostRun run, string plugin, int version, JsonObject settings) =>
        run.WriteDataFile(Path.Combine("plugin-settings", plugin + ".json"),
            new JsonObject { ["pluginId"] = plugin, ["settingsVersion"] = version, ["settings"] = settings }.ToJsonString());

    private static string? ClockText(JsonObject status) => HostRun.Texts(status, Clock).SingleOrDefault()?.FirstOrDefault();

    private static int? PluginPid(JsonObject status, string plugin) =>
        status["plugins"]!.AsArray()
            .Where(p => (string?)p!["id"] == plugin && (string?)p["state"] == "Running")
            .Select(p => (int?)p!["pid"])
            .FirstOrDefault();

    private static async Task<string[]> WaitForLinesAsync(string path, Func<string[], bool> condition)
    {
        var deadline = DateTime.UtcNow + Startup;
        string[] lines = [];
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path))
            {
                await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream);
                lines = (await reader.ReadToEndAsync()).Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
                if (condition(lines))
                {
                    return lines;
                }
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{path} never matched. Lines: {string.Join(" | ", lines)}");
    }

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex Minutes();

    [GeneratedRegex(@"^\d{2}:\d{2}:\d{2}$")]
    private static partial Regex Seconds();
}

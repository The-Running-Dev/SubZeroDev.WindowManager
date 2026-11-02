using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S10: plugins are enabled and disabled, and the settings window reads what it shows.</summary>
public sealed class PluginManagerTests
{
    private const string BuiltIn = "example.builtin";
    private const string User = "example.user";

    [Fact]
    public void A_plugin_absent_from_the_map_is_enabled_when_built_in_and_disabled_when_user_installed()
    {
        using var scratch = new Scratch();
        var locations = WithPlugins(scratch);
        using var catalog = new PackageCatalog(locations, new NullLog(), watch: false);
        using var config = new ConfigStore(locations);
        var enablement = new PluginEnablement(config, catalog);

        Assert.True(enablement.IsEnabled(BuiltIn));
        Assert.False(enablement.IsEnabled(User));
    }

    [Fact]
    public async Task Setting_enabled_keeps_the_rest_of_the_host_settings()
    {
        using var scratch = new Scratch();
        var locations = WithPlugins(scratch);
        File.WriteAllText(Path.Combine(scratch.DataRoot, "settings.json"),
            """{ "schemaVersion": 1, "activePreset": "example.kept", "startAtSignIn": false, "plugins": { "other": { "enabled": false } }, "future": 7 }""");
        using var catalog = new PackageCatalog(locations, new NullLog(), watch: false);
        using var config = new ConfigStore(locations);
        var enablement = new PluginEnablement(config, catalog);

        var set = await enablement.SetAsync(User, true, CancellationToken.None);

        Assert.True(set.Ok, set.Error?.Detail);
        Assert.True(enablement.IsEnabled(User));
        var saved = JsonNode.Parse(File.ReadAllText(Path.Combine(scratch.DataRoot, "settings.json")))!;
        Assert.Equal("example.kept", (string?)saved["activePreset"]);
        Assert.Equal(7, (int?)saved["future"]);
        Assert.False((bool)saved["plugins"]!["other"]!["enabled"]!);
        Assert.True((bool)saved["plugins"]![User]!["enabled"]!);
    }

    [Fact]
    public async Task S10_2_disable_and_enable_move_the_state_and_persist_the_switch()
    {
        using var scratch = new Scratch();
        var locations = WithPlugins(scratch);
        using var catalog = new PackageCatalog(locations, new NullLog(), watch: false);
        using var config = new ConfigStore(locations);
        var lines = new Lines();
        await using var supervisor = Supervisor(catalog, locations, lines);
        supervisor.Enablement = new PluginEnablement(config, catalog);

        var disabled = await supervisor.DisableAsync(BuiltIn, CancellationToken.None);
        Assert.True(disabled.Ok, disabled.Error?.Detail);
        Assert.Equal(PluginState.Disabled, supervisor.GetStatus(BuiltIn).State);
        var start = await supervisor.EnsureRunningAsync(BuiltIn, CancellationToken.None);
        Assert.Equal(CoreErrorCode.PluginDisabled, start.Error?.Code);
        Assert.Equal(PluginState.Disabled, supervisor.GetStatus(BuiltIn).State);
        Assert.False(new PluginEnablement(new ConfigStore(locations), catalog).IsEnabled(BuiltIn));

        var enabled = await supervisor.EnableAsync(BuiltIn, CancellationToken.None);
        Assert.True(enabled.Ok, enabled.Error?.Detail);
        Assert.Equal(PluginState.Discovered, supervisor.GetStatus(BuiltIn).State);
        Assert.True(supervisor.Enablement.IsEnabled(BuiltIn));
        Assert.Contains($"{BuiltIn} host Disabled.", lines.All);
        Assert.Contains($"{BuiltIn} host Enabled.", lines.All);
    }

    [Fact]
    public async Task A_user_plugin_is_described_as_disabled_until_enabled_and_an_unknown_one_is_not_found()
    {
        using var scratch = new Scratch();
        var locations = WithPlugins(scratch);
        using var catalog = new PackageCatalog(locations, new NullLog(), watch: false);
        using var config = new ConfigStore(locations);
        await using var supervisor = Supervisor(catalog, locations, new Lines());
        supervisor.Enablement = new PluginEnablement(config, catalog);

        Assert.Equal(PluginState.Disabled, supervisor.Describe(User).State);
        Assert.Equal(PluginState.Discovered, supervisor.GetStatus(User).State);
        Assert.Equal(CoreErrorCode.NotFound, (await supervisor.DisableAsync("example.none", CancellationToken.None)).Error?.Code);
    }

    [Fact]
    public async Task S10_3_a_plugin_needing_host_api_2_is_incompatible_and_never_starts()
    {
        using var scratch = new Scratch();
        var locations = WithPlugins(scratch, hostApi: "^2.0");
        using var catalog = new PackageCatalog(locations, new NullLog(), watch: false);
        using var config = new ConfigStore(locations);
        var launcher = new RefusingLauncher();
        await using var supervisor = Supervisor(catalog, locations, new Lines(), launcher);
        supervisor.Enablement = new PluginEnablement(config, catalog);

        var described = supervisor.Describe(BuiltIn);
        var start = await supervisor.EnsureRunningAsync(BuiltIn, CancellationToken.None);

        Assert.Equal(PluginState.Incompatible, described.State);
        Assert.Equal(CoreErrorCode.HostApiMismatch, Assert.Single(described.Problems).Code);
        Assert.Equal(CoreErrorCode.HostApiMismatch, start.Error?.Code);
        Assert.Equal(PluginState.Incompatible, supervisor.GetStatus(BuiltIn).State);
        Assert.Equal(0, launcher.Starts);
    }

    [Fact]
    public void The_plugins_map_parses_and_a_bad_entry_is_a_problem()
    {
        var problems = new List<string>();
        var parsed = Documents.ParseHostSettings(Settings(new JsonObject { ["a.b"] = new JsonObject { ["enabled"] = false } }), problems);

        Assert.Empty(problems);
        Assert.False(parsed!.Plugins["a.b"]);
        Assert.False(parsed.ToJson()["plugins"]!["a.b"]!["enabled"]!.GetValue<bool>());

        Assert.Null(Documents.ParseHostSettings(Settings(new JsonObject { ["a.b"] = "off" }), problems));
        Assert.Contains("plugins.a.b: must be { enabled: bool }.", problems);
    }

    [Fact]
    public async Task S10_6_the_log_tail_is_the_last_lines_oldest_first()
    {
        using var scratch = new Scratch();
        await using (var logs = new PluginLogs(scratch.Locations.LogsDirectory))
        {
            for (var i = 0; i < 250; i++)
            {
                logs.Write(BuiltIn, "Information", $"line {i}");
            }
        }

        await using var reader = new PluginLogs(scratch.Locations.LogsDirectory);
        var tail = reader.Tail(BuiltIn);

        Assert.Equal(200, tail.Count);
        Assert.EndsWith("line 50", tail[0], StringComparison.Ordinal);
        Assert.EndsWith("line 249", tail[^1], StringComparison.Ordinal);
        Assert.Empty(reader.Tail("example.none"));
    }

    [Fact]
    public async Task S10_4_instances_come_with_their_schema_and_defaults_and_save_into_the_layout()
    {
        using var scratch = new Scratch();
        var locations = WithPlugins(scratch);
        var instanceId = Guid.NewGuid().ToString("D");
        Directory.CreateDirectory(Path.Combine(scratch.DataRoot, "layouts"));
        File.WriteAllText(Path.Combine(scratch.DataRoot, "layouts", "example.mine.json"), new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "example.mine",
            ["panels"] = new JsonArray(new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("D"),
                ["placement"] = new JsonObject { ["edge"] = "bottom" },
                ["thickness"] = 40,
                ["behaviour"] = "reserve",
                ["zones"] = new JsonObject
                {
                    ["start"] = new JsonArray(new JsonObject
                    {
                        ["id"] = instanceId, ["plugin"] = BuiltIn, ["contribution"] = "item", ["settings"] = new JsonObject { ["count"] = 2 },
                    }),
                },
            }),
        }.ToJsonString());
        using var catalog = new PackageCatalog(locations, new NullLog(), watch: false);
        using var config = new ConfigStore(locations);
        await using var supervisor = Supervisor(catalog, locations, new Lines());
        var service = new SettingsService(catalog, supervisor, config, new PluginSettingsStore(config, locations, new Lines()), () => "example.mine");

        var instance = Assert.Single(service.InstancesOf(BuiltIn));
        Assert.Equal(instanceId, instance.InstanceId);
        Assert.Equal("Item", instance.ContributionName);
        Assert.Equal("on", (string?)instance.Settings["mode"]);
        Assert.Equal(2, (int?)instance.Settings["count"]);

        var refused = await service.SaveInstanceAsync(BuiltIn, instanceId, new JsonObject { ["count"] = 99 }, CancellationToken.None);
        var saved = await service.SaveInstanceAsync(BuiltIn, instanceId, new JsonObject { ["mode"] = "off", ["count"] = 3 }, CancellationToken.None);

        Assert.Equal(CoreErrorCode.DocumentInvalid, refused.Error?.Code);
        Assert.True(saved.Ok, saved.Error?.Detail);
        var after = Assert.Single(service.InstancesOf(BuiltIn));
        Assert.Equal("off", (string?)after.Settings["mode"]);
        Assert.Equal(3, (int?)after.Settings["count"]);
    }

    private static JsonObject Settings(JsonObject plugins) => new()
    {
        ["schemaVersion"] = 1,
        ["activePreset"] = "subzerodev.default",
        ["startAtSignIn"] = false,
        ["plugins"] = plugins,
    };

    private static PluginSupervisor Supervisor(IPackageCatalog catalog, Locations locations, IPluginLogs lines, IWorkerLauncher? launcher = null) =>
        new(catalog, launcher ?? new RefusingLauncher(), lines, new NullLog(), locations, HostMode.Coexistence, "0.1.0", RestartPolicy.Default);

    /// <summary>A content root with one built-in plugin and a data root with one user plugin.</summary>
    private static Locations WithPlugins(Scratch scratch, string hostApi = "^1.0")
    {
        var content = Path.Combine(scratch.DataRoot, "content");
        WritePlugin(Path.Combine(content, "plugins", BuiltIn), BuiltIn, hostApi);
        WritePlugin(Path.Combine(scratch.DataRoot, "plugins", User), User, "^1.0");
        return new Locations(scratch.DataRoot, content);
    }

    private static void WritePlugin(string folder, string id, string hostApi)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "worker.exe"), string.Empty);
        File.WriteAllText(Path.Combine(folder, "plugin.json"), new JsonObject
        {
            ["manifestVersion"] = 1,
            ["id"] = id,
            ["version"] = "0.1.0",
            ["displayName"] = id,
            ["publisher"] = "Test",
            ["hostApi"] = hostApi,
            ["entry"] = new JsonObject { ["path"] = "worker.exe", ["args"] = new JsonArray() },
            ["modes"] = new JsonArray("coexistence"),
            ["contributions"] = new JsonArray(new JsonObject
            {
                ["id"] = "item",
                ["kind"] = "panelItem",
                ["displayName"] = "Item",
                ["settingsSchema"] = new JsonObject
                {
                    ["type"] = "object",
                    ["properties"] = new JsonObject
                    {
                        ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("on", "off"), ["default"] = "on" },
                        ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 10 },
                    },
                },
            }),
        }.ToJsonString());
    }

    private sealed class RefusingLauncher : IWorkerLauncher
    {
        public int Starts { get; private set; }

        public Task<Outcome<IWorkerProcess>> StartAsync(WorkerStartInfo info, CancellationToken ct)
        {
            Starts++;
            return Task.FromResult(Outcome<IWorkerProcess>.Fail(CoreErrorCode.IoFailure, "Tests start no workers."));
        }

        public IReadOnlyList<int> JobProcessIds() => [];
    }

    private sealed class NullLog : IHostLog
    {
        public void Write(Diagnostics.LogLevel level, string area, string message)
        {
        }
    }

    private sealed class Lines : IPluginLogs
    {
        private readonly List<string> _lines = [];

        public IReadOnlyList<string> All
        {
            get
            {
                lock (_lines)
                {
                    return [.. _lines];
                }
            }
        }

        public void Write(string pluginId, string level, string message)
        {
            lock (_lines)
            {
                _lines.Add($"{pluginId} {level} {message}");
            }
        }
    }
}

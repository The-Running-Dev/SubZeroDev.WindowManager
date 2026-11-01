using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S9: settings are checked against the schema subset, persisted and migrated.</summary>
public sealed class SettingsTests
{
    private const string PluginId = "example.settings";

    private static JsonObject Schema() => new()
    {
        ["type"] = "object",
        ["properties"] = new JsonObject
        {
            ["format"] = new JsonObject { ["type"] = "string", ["default"] = "HH:mm" },
            ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0 },
        },
    };

    private static JsonObject ManifestJson(int settingsVersion = 1, JsonObject? contributionSchema = null) => new()
    {
        ["manifestVersion"] = 1,
        ["id"] = PluginId,
        ["version"] = "0.1.0",
        ["displayName"] = "Settings",
        ["publisher"] = "SubZeroDev",
        ["hostApi"] = "^1.0",
        ["entry"] = new JsonObject { ["path"] = "example.exe", ["args"] = new JsonArray() },
        ["modes"] = new JsonArray("coexistence"),
        ["contributions"] = new JsonArray(new JsonObject
        {
            ["id"] = "item",
            ["kind"] = "panelItem",
            ["displayName"] = "Item",
            ["settingsSchema"] = contributionSchema ?? Schema(),
        }),
        ["settingsSchema"] = Schema(),
        ["settingsVersion"] = settingsVersion,
    };

    private static PluginManifest Manifest(int settingsVersion = 1)
    {
        var problems = new List<string>();
        var manifest = PluginManifestReader.Parse(ManifestJson(settingsVersion), problems);
        Assert.True(manifest is not null, string.Join("; ", problems));
        return manifest;
    }

    [Fact]
    public void S9_6_a_keyword_outside_the_subset_makes_the_manifest_invalid_and_is_named()
    {
        var schema = Schema();
        schema["properties"]!["format"]!["oneOf"] = new JsonArray(new JsonObject { ["type"] = "string" });
        var problems = new List<string>();

        Assert.Null(PluginManifestReader.Parse(ManifestJson(contributionSchema: schema), problems));
        Assert.Contains(problems, problem => problem.Contains("oneOf", StringComparison.Ordinal));
    }

    [Fact]
    public void The_subset_checks_types_and_bounds_and_fills_defaults()
    {
        var schema = Schema();

        Assert.Empty(SettingsSchemaSubset.Validate(schema, new JsonObject { ["format"] = "HH", ["count"] = 2 }));
        Assert.NotEmpty(SettingsSchemaSubset.Validate(schema, new JsonObject { ["format"] = 5 }));
        Assert.NotEmpty(SettingsSchemaSubset.Validate(schema, new JsonObject { ["count"] = -1 }));
        Assert.Equal("HH:mm", (string?)SettingsSchemaSubset.Defaults(schema)["format"]);
        var filled = SettingsSchemaSubset.WithDefaults(schema, new JsonObject { ["count"] = 3 });
        Assert.Equal("HH:mm", (string?)filled["format"]);
        Assert.Equal(3, (int?)filled["count"]);
    }

    [Fact]
    public async Task Missing_settings_start_with_the_defaults()
    {
        using var scratch = new Scratch();
        var settings = new PluginSettingsStore(new ConfigStore(scratch.Locations), scratch.Locations, new Lines());

        var loaded = await settings.LoadAsync(Manifest(), NoMigration, TestContext.Current.CancellationToken);

        Assert.Equal("HH:mm", (string?)loaded["format"]);
    }

    [Fact]
    public async Task S9_2_saved_settings_load_again_with_a_new_store()
    {
        using var scratch = new Scratch();
        var manifest = Manifest();
        var first = new PluginSettingsStore(new ConfigStore(scratch.Locations), scratch.Locations, new Lines());

        var saved = await first.SaveAsync(manifest, new JsonObject { ["count"] = 4 }, TestContext.Current.CancellationToken);
        var second = new PluginSettingsStore(new ConfigStore(scratch.Locations), scratch.Locations, new Lines());
        var loaded = await second.LoadAsync(manifest, NoMigration, TestContext.Current.CancellationToken);

        Assert.True(saved.Ok, saved.Error?.ToString());
        Assert.Equal(4, (int?)loaded["count"]);
        Assert.Equal("HH:mm", (string?)loaded["format"]);
    }

    [Fact]
    public async Task S9_3_invalid_settings_are_refused_and_nothing_is_written()
    {
        using var scratch = new Scratch();
        var manifest = Manifest();
        var settings = new PluginSettingsStore(new ConfigStore(scratch.Locations), scratch.Locations, new Lines());
        await settings.SaveAsync(manifest, new JsonObject { ["count"] = 1 }, TestContext.Current.CancellationToken);
        var before = await File.ReadAllBytesAsync(SettingsPath(scratch), TestContext.Current.CancellationToken);

        var refused = await settings.SaveAsync(manifest, new JsonObject { ["count"] = "many" }, TestContext.Current.CancellationToken);

        Assert.Equal(CoreErrorCode.DocumentInvalid, refused.Error!.Code);
        Assert.Equal(WireErrorCodes.SettingsInvalid, WorkerChannel.WireCodeOf(refused.Error.Code));
        Assert.Equal(before, await File.ReadAllBytesAsync(SettingsPath(scratch), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task S9_4_older_settings_are_migrated_and_the_prior_file_is_kept()
    {
        using var scratch = new Scratch();
        var original = WriteStored(scratch, 1, new JsonObject { ["count"] = 2 });
        var settings = new PluginSettingsStore(new ConfigStore(scratch.Locations), scratch.Locations, new Lines());
        var asked = 0;

        var loaded = await settings.LoadAsync(Manifest(settingsVersion: 2), (from, stored, _) =>
        {
            asked = from;
            stored["count"] = (int)stored["count"]! * 10;
            return Task.FromResult(Outcome<SettingsMigrateResult>.Success(new SettingsMigrateResult(2, stored)));
        }, TestContext.Current.CancellationToken);

        Assert.Equal(1, asked);
        Assert.Equal(20, (int?)loaded["count"]);
        var backup = Path.Combine(scratch.DataRoot, "plugin-settings", $"{PluginId}.v1.bak");
        Assert.Equal(original, await File.ReadAllBytesAsync(backup, TestContext.Current.CancellationToken));
        var written = JsonNode.Parse(await File.ReadAllTextAsync(SettingsPath(scratch), TestContext.Current.CancellationToken))!;
        Assert.Equal(2, (int?)written["settingsVersion"]);
        Assert.Equal(20, (int?)written["settings"]!["count"]);
    }

    [Fact]
    public async Task S9_5_a_failed_migration_starts_with_defaults_and_leaves_the_file_alone()
    {
        using var scratch = new Scratch();
        var original = WriteStored(scratch, 1, new JsonObject { ["count"] = 2 });
        var lines = new Lines();
        var settings = new PluginSettingsStore(new ConfigStore(scratch.Locations), scratch.Locations, lines);

        var loaded = await settings.LoadAsync(Manifest(settingsVersion: 2),
            (_, _, _) => throw new InvalidOperationException("boom"), TestContext.Current.CancellationToken);

        Assert.Null(loaded["count"]);
        Assert.Equal("HH:mm", (string?)loaded["format"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(SettingsPath(scratch), TestContext.Current.CancellationToken));
        Assert.False(File.Exists(Path.Combine(scratch.DataRoot, "plugin-settings", $"{PluginId}.v1.bak")));
        Assert.Contains(lines.All, line => line.Contains("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Settings_newer_than_the_plugin_start_with_defaults_and_are_kept()
    {
        using var scratch = new Scratch();
        var original = WriteStored(scratch, 3, new JsonObject { ["count"] = 2 });
        var settings = new PluginSettingsStore(new ConfigStore(scratch.Locations), scratch.Locations, new Lines());

        var loaded = await settings.LoadAsync(Manifest(settingsVersion: 2), NoMigration, TestContext.Current.CancellationToken);

        Assert.Null(loaded["count"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(SettingsPath(scratch), TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task A_migration_that_returns_the_wrong_version_is_not_written()
    {
        using var scratch = new Scratch();
        var original = WriteStored(scratch, 1, new JsonObject { ["count"] = 2 });
        var settings = new PluginSettingsStore(new ConfigStore(scratch.Locations), scratch.Locations, new Lines());

        var loaded = await settings.LoadAsync(Manifest(settingsVersion: 2),
            (_, stored, _) => Task.FromResult(Outcome<SettingsMigrateResult>.Success(new SettingsMigrateResult(1, stored))),
            TestContext.Current.CancellationToken);

        Assert.Null(loaded["count"]);
        Assert.Equal(original, await File.ReadAllBytesAsync(SettingsPath(scratch), TestContext.Current.CancellationToken));
    }

    private static Task<Outcome<SettingsMigrateResult>> NoMigration(int fromVersion, JsonObject settings, CancellationToken ct) =>
        throw new InvalidOperationException("No migration was expected.");

    private static string SettingsPath(Scratch scratch) => Path.Combine(scratch.DataRoot, "plugin-settings", PluginId + ".json");

    private static byte[] WriteStored(Scratch scratch, int version, JsonObject settings)
    {
        var path = SettingsPath(scratch);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = new JsonObject { ["pluginId"] = PluginId, ["settingsVersion"] = version, ["settings"] = settings }.ToJsonString();
        File.WriteAllText(path, json);
        return File.ReadAllBytes(path);
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

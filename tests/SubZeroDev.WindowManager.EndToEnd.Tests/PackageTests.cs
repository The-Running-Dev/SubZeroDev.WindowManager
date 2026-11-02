using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json.Nodes;
using static SubZeroDev.WindowManager.EndToEnd.Tests.SettingsWindowTests;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S11: installing a package, placing and removing instances in the layout editor, and removing a plugin,
/// all from the settings window while the host runs.
/// </summary>
public sealed class PackageTests
{
    private const string Clock = "subzerodev.clock";
    private const string Installed = "samples.installed";
    private const string Placed = "samples.placed";
    private const string Removable = "samples.removable";
    private const string Purged = "samples.purged";

    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan Promptly = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task S11_2_installing_a_zip_lists_the_plugin_as_disabled_without_a_restart()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        var zip = Package(run, Installed);
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1, Startup);
        var host = run.Process!.Id;

        await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" },
            new JsonObject { ["set"] = "Plugins.InstallPath", ["value"] = zip });
        var installing = Stopwatch.StartNew();
        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Plugins.Install" });
        var status = await run.WaitForAsync(s => State(s, Installed) == "Disabled", Promptly - installing.Elapsed);

        var row = Row(status, Installed);
        Assert.Equal("User", (string?)row["source"]);
        Assert.Equal("0.1.0", (string?)row["version"]);
        Assert.True(File.Exists(Path.Combine(run.DataRoot, "plugins", Installed, "plugin.json")));
        Assert.Equal(host, run.Process.Id);
        Assert.False(run.Process.HasExited);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S11_3_a_zip_that_escapes_its_root_is_refused_and_staging_is_left_empty()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        var zip = Path.Combine(run.Root, "escaping.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            Add(archive, "plugin.json", File.ReadAllText(Path.Combine(HostRun.TestPluginDirectory, "plugin.json")));
            Add(archive, "../escaped.txt", "out");
        }

        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1, Startup);

        await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" },
            new JsonObject { ["set"] = "Plugins.InstallPath", ["value"] = zip },
            new JsonObject { ["invoke"] = "Plugins.Install" });
        var status = await run.WaitForAsync(s => Message(s).StartsWith("PackageInvalid", StringComparison.Ordinal), Reaction);

        var staging = Path.Combine(run.DataRoot, "staging");
        Assert.True(!Directory.Exists(staging) || !Directory.EnumerateFileSystemEntries(staging).Any(),
            $"staging still holds: {string.Join(", ", Directory.EnumerateFileSystemEntries(staging))}");
        Assert.False(File.Exists(Path.Combine(run.DataRoot, "escaped.txt")));
        Assert.False(File.Exists(Path.Combine(run.DataRoot, "plugins", "escaped.txt")));
        Assert.DoesNotContain(Window(status)!["plugins"]!.AsArray(), row => (string?)row!["id"] == "subzerodev.testplugin");
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S11_5_and_S11_6_the_layout_editor_adds_an_instance_to_the_panel_and_removes_it_again()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Placed, ["styled"]);
        var existing = Guid.NewGuid();
        var layout = run.WriteDefaultLayout((existing, Placed, "probe"));
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => HostRun.Texts(s, Placed).Count == 1, Startup);

        var status = await SettingsUiAsync(run, new JsonObject { ["page"] = "layout" });
        Assert.Contains(Window(status)!["layout"]!["contributions"]!.AsArray(), item => (string?)item == $"{Placed}/probe");
        await SettingsUiAsync(run,
            new JsonObject { ["set"] = "Layout.Contribution", ["value"] = $"{Placed}/probe" },
            new JsonObject { ["set"] = "Layout.TargetZone", ["value"] = "start" });
        var adding = Stopwatch.StartNew();
        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Layout.Add" });
        status = await run.WaitForAsync(s => HostRun.Texts(s, Placed).Count == 2, Promptly - adding.Elapsed);

        var added = Guid.Parse((string)Window(status)!["layout"]!["selectedInstance"]!);
        Assert.NotEqual(existing, added);
        var saved = JsonNode.Parse(await WaitForFileAsync(layout, text => text.Contains(added.ToString("D"), StringComparison.Ordinal)))!;
        var start = saved["panels"]![0]!["zones"]!["start"]!.AsArray();
        Assert.Contains(start, item => (string?)item!["id"] == added.ToString("D") && (string?)item["plugin"] == Placed
            && (string?)item["contribution"] == "probe");

        await SettingsUiAsync(run,
            new JsonObject { ["set"] = "Layout.Instances", ["value"] = added.ToString("D") },
            new JsonObject { ["invoke"] = "Layout.Remove" });
        await WaitForFileAsync(layout, text => text.Length > 0 && !text.Contains(added.ToString("D"), StringComparison.Ordinal));
        await WaitForFileAsync(Path.Combine(run.DataRoot, "plugin-data", Placed, "instances.log"),
            text => text.Contains($"destroyed {added:D}", StringComparison.Ordinal));
        // The settings snapshot refreshes on its own tick, so wait for the panel and the layout list together.
        await run.WaitForAsync(s => HostRun.Texts(s, Placed).Count == 1
            && Window(s)?["layout"]?["instances"]?.AsArray().All(item => (string?)item != added.ToString("D")) == true, Reaction);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S11_7_removing_a_user_plugin_stops_it_deletes_the_package_and_keeps_data_unless_asked_and_a_built_in_is_in_use()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Removable, ["styled"]);
        run.CopyPlugin(HostRun.TestPluginDirectory, Purged, ["styled"]);
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"), (Guid.NewGuid(), Removable, "probe"), (Guid.NewGuid(), Purged, "probe"));
        run.Start(HostRun.HostExe);
        var status = await run.WaitForAsync(s => HostRun.Texts(s, Removable).Count == 1 && HostRun.Texts(s, Purged).Count == 1
            && Plugin(s, Removable)?["pid"] is not null, Startup);
        using var worker = Process.GetProcessById((int)Plugin(status, Removable)!["pid"]!);
        var keptData = Path.Combine(run.DataRoot, "plugin-data", Removable);
        Assert.True(Directory.Exists(keptData));

        await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" }, new JsonObject { ["select"] = Removable },
            new JsonObject { ["invoke"] = "Plugin.Remove" });
        Assert.True(worker.WaitForExit(Reaction), "The removed plugin's worker was still running.");
        status = await run.WaitForAsync(s => Reason(s, Removable) == "Missing", Reaction);
        Assert.False(Directory.Exists(Path.Combine(run.DataRoot, "plugins", Removable)));
        Assert.True(Directory.Exists(keptData));
        Assert.Null(Plugin(status, Removable)?["pid"]);

        await SettingsUiAsync(run, new JsonObject { ["select"] = Purged }, new JsonObject { ["set"] = "Plugin.DeleteData", ["value"] = true },
            new JsonObject { ["invoke"] = "Plugin.Remove" });
        await run.WaitForAsync(s => Reason(s, Purged) == "Missing" && Message(s).Contains(Purged, StringComparison.Ordinal), Reaction);
        Assert.False(Directory.Exists(Path.Combine(run.DataRoot, "plugins", Purged)));
        Assert.False(Directory.Exists(Path.Combine(run.DataRoot, "plugin-data", Purged)));

        await SettingsUiAsync(run, new JsonObject { ["select"] = Clock }, new JsonObject { ["invoke"] = "Plugin.Remove" });
        await run.WaitForAsync(s => Message(s).StartsWith("PackageInUse", StringComparison.Ordinal), Reaction);
        Assert.Single(HostRun.Texts(await run.WaitForStatusAsync(), Clock));
        Assert.Equal(0, await run.StopAsync());
    }

    /// <summary>Zips the built test plugin under <paramref name="id"/>, as <c>wm-pack</c> would.</summary>
    private static string Package(HostRun run, string id)
    {
        var folder = Path.Combine(run.Root, "package-" + id);
        foreach (var file in Directory.EnumerateFiles(HostRun.TestPluginDirectory, "*", SearchOption.AllDirectories))
        {
            var destination = Path.Combine(folder, Path.GetRelativePath(HostRun.TestPluginDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(folder, "plugin.json")))!.AsObject();
        manifest["id"] = id;
        File.WriteAllText(Path.Combine(folder, "plugin.json"), manifest.ToJsonString());
        var zip = Path.Combine(run.Root, id + ".zip");
        ZipFile.CreateFromDirectory(folder, zip);
        return zip;
    }

    private static void Add(ZipArchive archive, string name, string text)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.Write(text);
    }

    private static string Message(JsonObject status) => (string?)Window(status)?["message"] ?? string.Empty;
}

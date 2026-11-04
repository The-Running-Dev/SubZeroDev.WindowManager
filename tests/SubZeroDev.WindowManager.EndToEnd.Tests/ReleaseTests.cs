using System.Diagnostics;
using System.Text.Json.Nodes;
using static SubZeroDev.WindowManager.EndToEnd.Tests.SettingsWindowTests;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S15: the released app installs a third-party plugin built from the released packages alone. It runs where
/// <c>build\release.ps1</c> has run and says where: <c>SUBZERODEV_WM_RELEASE_DIR</c> (the unpacked release) and
/// <c>SUBZERODEV_WM_SAMPLE_PACKAGE</c> (the sample's wm-pack zip). Elsewhere it is skipped.
/// </summary>
public sealed class ReleaseTests
{
    private const string ReleaseVariable = "SUBZERODEV_WM_RELEASE_DIR";
    private const string SampleVariable = "SUBZERODEV_WM_SAMPLE_PACKAGE";
    private const string FixtureVariable = "SUBZERODEV_WM_FIXTURE_PACKAGE";
    private const string Clock = "subzerodev.clock";
    private const string Sample = "example.hello";
    private const string Fixture = "example.windows";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S15_3_the_released_host_installs_and_runs_the_sample_plugin()
    {
        var release = Environment.GetEnvironmentVariable(ReleaseVariable);
        var sample = Environment.GetEnvironmentVariable(SampleVariable);
        if (string.IsNullOrEmpty(release) || string.IsNullOrEmpty(sample))
        {
            Assert.Skip($"Set {ReleaseVariable} and {SampleVariable} after build\\release.ps1 to run this.");
        }

        HostRun.RequireDesktop();
        var host = Path.Combine(release, "host", "SubZeroDev.WindowManager.Host.exe");
        Assert.True(File.Exists(host), $"No released host at {host}.");
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        run.Start(host);
        await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1, Startup);
        Assert.StartsWith(Path.GetFullPath(Path.Combine(release, "runtime")), RuntimeOf(run.Process!), StringComparison.OrdinalIgnoreCase);

        await InstallAndPlaceAsync(run, sample, Sample, "hello");
        await run.WaitForAsync(s => State(s, Sample) == "Running" && HostRun.Texts(s, Sample).Any(texts => texts.Contains("Hello, world")), Startup);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S25_9_a_fixture_built_from_the_packages_reads_both_feeds_and_acts_by_window_key()
    {
        var release = Environment.GetEnvironmentVariable(ReleaseVariable);
        var fixture = Environment.GetEnvironmentVariable(FixtureVariable);
        if (string.IsNullOrEmpty(release) || string.IsNullOrEmpty(fixture))
        {
            Assert.Skip($"Set {ReleaseVariable} and {FixtureVariable} after build\\release.ps1 to run this.");
        }

        HostRun.RequireDesktop();
        var host = Path.Combine(release, "host", "SubZeroDev.WindowManager.Host.exe");
        Assert.True(File.Exists(host), $"No released host at {host}.");
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        run.Start(host);
        await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1, Startup);

        await InstallAndPlaceAsync(run, fixture, Fixture, "status");
        var status = await run.WaitForAsync(s => State(s, Fixture) == "Running"
            && HostRun.Texts(s, Fixture).Any(texts => texts.Any(text => text.StartsWith("Windows r", StringComparison.Ordinal))), Startup);
        var epoch = (long)status["windowModel"]!["epoch"]!;

        // A key from this run that names no window, and a key from another run (S25.7, S25.2).
        var gone = await InvokeAsync(run, "example.windows.activate", new JsonObject { ["windowKey"] = $"wk1:{epoch}:999999" });
        Assert.False((bool)gone["ok"]!);
        Assert.Contains("WindowGone", (string?)gone["detail"], StringComparison.Ordinal);

        var stale = await InvokeAsync(run, "example.windows.minimize", new JsonObject { ["windowKey"] = $"wk1:{epoch + 1}:1" });
        Assert.False((bool)stale["ok"]!);
        Assert.Contains("StaleWindowReference", (string?)stale["detail"], StringComparison.Ordinal);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S26_7_a_fixture_built_from_the_packages_switches_desktops_moves_a_window_and_shows_thumbnails()
    {
        var release = Environment.GetEnvironmentVariable(ReleaseVariable);
        var fixture = Environment.GetEnvironmentVariable(FixtureVariable);
        if (string.IsNullOrEmpty(release) || string.IsNullOrEmpty(fixture))
        {
            Assert.Skip($"Set {ReleaseVariable} and {FixtureVariable} after build\\release.ps1 to run this.");
        }

        HostRun.RequireDesktop();
        var host = Path.Combine(release, "host", "SubZeroDev.WindowManager.Host.exe");
        Assert.True(File.Exists(host), $"No released host at {host}.");
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        run.Start(host, "--mode", "replacement");
        await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1, Startup);

        await InstallAndPlaceAsync(run, fixture, Fixture, "status");
        await run.WaitForAsync(s => State(s, Fixture) == "Running", Startup);

        // Only windows the test hook made are touched (S26.10): two of them, and a second desktop to file one on.
        await File.WriteAllTextAsync(Path.Combine(run.HookDirectory, "synthetic-windows"),
            new JsonObject { ["op"] = "add", ["count"] = 2 }.ToJsonString(), TestContext.Current.CancellationToken);
        var status = await run.WaitForAsync(s => (int?)s["windowModel"]?["synthetic"] == 2
            && s["windowModel"]!["firstSynthetic"]!.AsArray().Count == 2, Startup);
        var epoch = (long)status["windowModel"]!["epoch"]!;
        var key = (string)status["windowModel"]!["firstSynthetic"]![0]!["key"]!;
        var first = (string)status["desktops"]!["current"]!;

        var created = await InvokeAsync(run, "host.desktops.create", new JsonObject { ["switchTo"] = false });
        Assert.True((bool)created["ok"]!, created.ToJsonString());
        status = await run.WaitForAsync(s => (int)s["desktops"]!["count"]! == 2, Startup);
        var second = status["desktops"]!["ids"]!.AsArray().Select(i => (string)i!).Single(id => id != first);

        // The published SDK alone moves a window, and the host names what it refused.
        var moved = await InvokeAsync(run, "example.windows.moveWindow", new JsonObject { ["windowKey"] = key, ["id"] = second });
        Assert.True((bool)moved["ok"]!, moved.ToJsonString());
        await run.WaitForAsync(s => (int)s["windowModel"]!["syntheticHidden"]! == 1, Startup);

        var switched = await InvokeAsync(run, "example.windows.switchDesktop", new JsonObject { ["id"] = second });
        Assert.True((bool)switched["ok"]!, switched.ToJsonString());
        await run.WaitForAsync(s => (string?)s["desktops"]!["current"] == second && (int)s["windowModel"]!["syntheticHidden"]! == 1, Startup);

        // A thumbnail of a key from another run, one that names no window, and one that is hidden: a card each, and nobody
        // is told (S26.7); the plugin is neither faulted nor stopped.
        foreach (var shown in new[] { $"wk1:{epoch + 1}:1", $"wk1:{epoch}:999999", key })
        {
            var result = await InvokeAsync(run, "example.windows.showThumbnail", new JsonObject { ["windowKey"] = shown });
            Assert.True((bool)result["ok"]!, result.ToJsonString());
        }

        status = await run.WaitForStatusAsync();
        var plugin = status["plugins"]!.AsArray().Single(p => (string?)p!["id"] == Fixture)!;
        Assert.Equal("Running", (string?)plugin["state"]);
        Assert.Equal(0, (int)plugin["recentFaults"]!);
        Assert.Equal(0, await run.StopAsync());
    }

    /// <summary>Installs a plugin package through the settings pages, enables it and places one instance, as a user would.</summary>
    private static async Task InstallAndPlaceAsync(HostRun run, string package, string plugin, string contribution)
    {
        await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" },
            new JsonObject { ["set"] = "Plugins.InstallPath", ["value"] = package });
        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Plugins.Install" });
        await run.WaitForAsync(s => State(s, plugin) == "Disabled", Startup);

        await SettingsUiAsync(run, new JsonObject { ["select"] = plugin });
        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Plugin.Enable" });

        // A plugin starts when an instance of it is placed.
        await SettingsUiAsync(run, new JsonObject { ["page"] = "layout" });
        await SettingsUiAsync(run,
            new JsonObject { ["set"] = "Layout.Contribution", ["value"] = $"{plugin}/{contribution}" },
            new JsonObject { ["set"] = "Layout.TargetZone", ["value"] = "start" });
        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Layout.Add" });
    }

    private static async Task<JsonObject> InvokeAsync(HostRun run, string command, JsonObject args)
    {
        var before = (await run.WaitForStatusAsync())["lastInvoke"]?.ToJsonString();
        await File.WriteAllLinesAsync(Path.Combine(run.HookDirectory, "invoke"), [command, "keyboard", args.ToJsonString()],
            TestContext.Current.CancellationToken);
        var status = await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == command
            && last.ToJsonString() != before, Startup);
        return status["lastInvoke"]!.AsObject();
    }

    /// <summary>Where the process loaded the .NET runtime from: the folder holding its <c>coreclr.dll</c>.</summary>
    private static string RuntimeOf(Process process)
    {
        process.Refresh();
        return process.Modules.Cast<ProcessModule>()
            .Single(module => string.Equals(module.ModuleName, "coreclr.dll", StringComparison.OrdinalIgnoreCase))
            .FileName;
    }
}

using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S25: the shell's window model outlives the plugin that shows it. Windows here are the test hook's model-only
/// synthetic ones, so no test touches a real window (I38).
/// </summary>
public sealed class WindowModelTests
{
    private const string RunningWindows = "subzerodev.running-windows";
    private const string PreviousVariable = "SUBZERODEV_WM_PREVIOUS_RELEASE_DIR";
    private const string ReleaseVariable = "SUBZERODEV_WM_RELEASE_DIR";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S25_6_killing_the_plugin_leaves_the_host_feeds_published_and_the_model_advancing()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows"));
        run.Start(HostRun.HostExe);
        var status = await run.WaitForAsync(s => PluginOf(s, RunningWindows)?["state"]?.GetValue<string>() == "Running"
            && FeedOf(s, "windows.running")?["available"]?.GetValue<bool>() == true, Startup);
        var epoch = (long)status["windowModel"]!["epoch"]!;
        var pid = (int)PluginOf(status, RunningWindows)!["pid"]!;
        Assert.Equal("host", (string?)FeedOf(status, "host.windows")!["provider"]);
        Assert.Equal("host", (string?)FeedOf(status, "host.desktops")!["provider"]);

        Synthetic(run, "add", 3);
        status = await run.WaitForAsync(s => (int?)s["windowModel"]!["synthetic"] == 3, Startup);
        var revision = (long)status["windowModel"]!["revision"]!;
        var windows = (int)status["windowModel"]!["windows"]!;

        using (var worker = Process.GetProcessById(pid))
        {
            worker.Kill();
        }

        status = await run.WaitForAsync(s => PluginOf(s, RunningWindows)?["state"]?.GetValue<string>() != "Running"
            || (int?)PluginOf(s, RunningWindows)?["pid"] != pid, TimeSpan.FromSeconds(10));
        Assert.True(FeedOf(status, "host.windows")!["available"]!.GetValue<bool>());
        Assert.True(FeedOf(status, "host.desktops")!["available"]!.GetValue<bool>());

        // The model keeps advancing whether or not the plugin is up.
        Synthetic(run, "add", 2);
        status = await run.WaitForAsync(s => (int?)s["windowModel"]!["synthetic"] == 5, Startup);
        Assert.True((long)status["windowModel"]!["revision"]! > revision);

        // The supervisor restarts the plugin; the same epoch and every window the model held come back with it.
        status = await run.WaitForAsync(s => PluginOf(s, RunningWindows)?["state"]?.GetValue<string>() == "Running"
            && (int?)PluginOf(s, RunningWindows)?["pid"] is { } now && now != pid
            && FeedOf(s, "windows.running")?["available"]?.GetValue<bool>() == true, TimeSpan.FromSeconds(30));
        Assert.Equal(epoch, (long)status["windowModel"]!["epoch"]!);
        Assert.Equal(windows + 2, (int)status["windowModel"]!["windows"]!);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S25_10_the_previous_release_restores_the_old_plugin_and_the_old_tracker_together()
    {
        var current = Environment.GetEnvironmentVariable(ReleaseVariable);
        var previous = Environment.GetEnvironmentVariable(PreviousVariable);
        if (string.IsNullOrEmpty(current) || string.IsNullOrEmpty(previous))
        {
            Assert.Skip($"Set {ReleaseVariable} and {PreviousVariable} (an unpacked earlier release) to run this.");
        }

        HostRun.RequireDesktop();
        var scratch = Path.Combine(Path.GetTempPath(), "szd-rollback-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            Copy(current, scratch);
            Copy(previous, scratch);
            var plugin = Path.Combine("host", "content", "plugins", RunningWindows, "plugin.json");
            Assert.Equal(File.ReadAllBytes(Path.Combine(previous, plugin)), File.ReadAllBytes(Path.Combine(scratch, plugin)));

            using var run = HostRun.Create();
            run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows"));
            run.Start(Path.Combine(scratch, "host", "SubZeroDev.WindowManager.Host.exe"));
            var status = await run.WaitForAsync(s => PluginOf(s, RunningWindows)?["state"]?.GetValue<string>() == "Running"
                && FeedOf(s, "windows.running")?["available"]?.GetValue<bool>() == true, Startup);

            // One tracker: the earlier release's, inside its own plugin. It has no shell window model.
            Assert.Null(status["windowModel"]);
            Assert.Equal(0, await run.StopAsync());
        }
        finally
        {
            Directory.Delete(scratch, recursive: true);
        }
    }

    private static void Synthetic(HostRun run, string op, int count) =>
        File.WriteAllText(Path.Combine(run.HookDirectory, "synthetic-windows"), new JsonObject { ["op"] = op, ["count"] = count }.ToJsonString());

    private static JsonObject? PluginOf(JsonObject status, string id) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == id)?.AsObject();

    private static JsonObject? FeedOf(JsonObject status, string id) =>
        status["feeds"]!.AsArray().FirstOrDefault(f => (string?)f!["id"] == id)?.AsObject();

    private static void Copy(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(target, Path.GetRelativePath(source, directory)));
        }

        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            File.Copy(file, Path.Combine(target, Path.GetRelativePath(source, file)), overwrite: true);
        }
    }
}

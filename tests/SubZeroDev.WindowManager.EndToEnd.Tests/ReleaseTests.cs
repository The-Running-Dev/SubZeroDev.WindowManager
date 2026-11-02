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
    private const string Clock = "subzerodev.clock";
    private const string Sample = "example.hello";
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

        await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" },
            new JsonObject { ["set"] = "Plugins.InstallPath", ["value"] = sample });
        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Plugins.Install" });
        await run.WaitForAsync(s => State(s, Sample) == "Disabled", Startup);

        await SettingsUiAsync(run, new JsonObject { ["select"] = Sample });
        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Plugin.Enable" });

        // A plugin starts when an instance of it is placed, so place one as a user would.
        await SettingsUiAsync(run, new JsonObject { ["page"] = "layout" });
        await SettingsUiAsync(run,
            new JsonObject { ["set"] = "Layout.Contribution", ["value"] = $"{Sample}/hello" },
            new JsonObject { ["set"] = "Layout.TargetZone", ["value"] = "start" });
        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Layout.Add" });
        await run.WaitForAsync(s => State(s, Sample) == "Running" && HostRun.Texts(s, Sample).Any(texts => texts.Contains("Hello, world")), Startup);
        Assert.Equal(0, await run.StopAsync());
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

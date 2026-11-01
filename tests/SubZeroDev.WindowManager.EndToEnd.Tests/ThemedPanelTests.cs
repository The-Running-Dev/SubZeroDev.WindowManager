using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>Slice S1: the guardian, the host and one themed, reserved panel.</summary>
public sealed class ThemedPanelTests
{
    [Fact]
    public async Task S1_2_guardian_starts_the_host_with_its_pid_and_pipe_and_exits_clean()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var guardian = run.Start(HostRun.GuardianExe);

        var status = await run.WaitForStatusAsync();

        Assert.Equal([(int)status["hostPid"]!], HostRun.HostsStartedSince(guardian.StartTime));
        Assert.Equal(guardian.Id, (int?)status["guardianPid"]);
        Assert.StartsWith("SubZeroDev.WindowManager.Guardian.", (string?)status["guardianPipe"], StringComparison.Ordinal);
        Assert.NotEqual(guardian.Id, (int)status["hostPid"]!);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S1_3_a_second_guardian_exits_with_2()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.Start(HostRun.GuardianExe);
        var status = await run.WaitForStatusAsync();
        var hostPid = (int)status["hostPid"]!;

        using var second = Process.Start(new ProcessStartInfo(HostRun.GuardianExe)
        {
            UseShellExecute = false,
            Environment = { ["SUBZERODEV_WM_INSTANCE"] = run.Instance, ["SUBZERODEV_WM_HOST_EXE"] = HostRun.HostExe },
        })!;
        var secondStarted = second.StartTime;
        await second.WaitForExitAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, second.ExitCode);
        Assert.Empty(HostRun.HostsStartedSince(secondStarted));
        using (var host = Process.GetProcessById(hostPid))
        {
            Assert.False(host.HasExited);
        }

        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S1_4_and_S1_5_first_run_writes_settings_and_reserves_a_bottom_panel()
    {
        HostRun.RequireDesktop();
        var before = Interactive.PrimaryWorkArea();
        using var run = HostRun.Create();
        run.Start(HostRun.HostExe);

        var status = await run.WaitForStatusAsync();
        var during = Interactive.PrimaryWorkArea();

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(run.DataRoot, "settings.json")))!;
        Assert.Equal(1, (int)settings["schemaVersion"]!);
        Assert.Equal("subzerodev.default", (string?)settings["activePreset"]);

        var panel = Assert.Single(status["panels"]!.AsArray())!;
        Assert.Equal("bottom", (string?)panel["edge"]);
        Assert.Equal("reserve", (string?)panel["behaviour"]);
        var scale = (double)panel["scale"]!;
        var thickness = (int)Math.Round(40 * scale, MidpointRounding.AwayFromZero);
        Assert.Equal(thickness, (int)panel["thicknessPx"]!);
        var window = Interactive.WindowRect((nint)(long)panel["handle"]!);
        Assert.Equal(thickness, window.Bottom - window.Top);
        Assert.Equal(before.Bottom, window.Bottom);
        Assert.Equal(before.Bottom, (int)panel["bounds"]!["bottom"]!);
        Assert.Equal(before.Bottom - thickness, during.Bottom);
        Assert.Equal("subzerodev.default", (string?)status["theme"]!["id"]);

        Assert.Equal(0, await run.StopAsync());
        Assert.Equal(before, Interactive.PrimaryWorkArea());
    }

    [Fact]
    public async Task S1_5_a_panel_refuses_WM_CLOSE_and_keeps_its_space()
    {
        HostRun.RequireDesktop();
        var before = Interactive.PrimaryWorkArea();
        using var run = HostRun.Create();
        run.Start(HostRun.HostExe);
        var status = await run.WaitForStatusAsync();
        var handle = (nint)(long)status["panels"]![0]!["handle"]!;
        var reserved = Interactive.PrimaryWorkArea();

        Interactive.RequestClose(handle);
        await Task.Delay(1000, TestContext.Current.CancellationToken);

        Assert.True(Interactive.IsAlive(handle));
        Assert.Equal(reserved, Interactive.PrimaryWorkArea());
        Assert.Equal(0, await run.StopAsync());
        Assert.Equal(before, Interactive.PrimaryWorkArea());
    }

    [Fact]
    public async Task S1_6_a_user_theme_copy_changes_the_template_root_background()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var copy = run.CopyBuiltInTheme();
        var panelXaml = Path.Combine(copy, "Panel.xaml");
        File.WriteAllText(panelXaml, File.ReadAllText(panelXaml).Replace(
            "Background=\"{ThemeResource Wm.Brush.surface}\"", "Background=\"#FF123456\"", StringComparison.Ordinal));
        run.Start(HostRun.HostExe);

        var status = await run.WaitForStatusAsync();

        Assert.Equal("User", (string?)status["theme"]!["source"]);
        Assert.Equal("#FF123456", (string?)status["panels"]![0]!["background"]);
        Assert.Equal(0, await run.StopAsync());
    }

    public static TheoryData<string> BrokenXaml => new()
    {
        // Malformed XML, caught before the XAML parser runs.
        "<ResourceDictionary><Style",
        // Well-formed XML that XamlReader.Load rejects: the root has no such property.
        """
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation" NoSuchProperty="1" />
        """,
    };

    [Theory]
    [MemberData(nameof(BrokenXaml))]
    public async Task S1_7_broken_xaml_names_the_file_falls_back_and_logs_once(string broken)
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var copy = run.CopyBuiltInTheme();
        var panelXaml = Path.Combine(copy, "Panel.xaml");
        File.WriteAllText(panelXaml, broken);
        run.Start(HostRun.HostExe);

        var status = await run.WaitForStatusAsync();
        Assert.Equal(0, await run.StopAsync());

        var theme = status["theme"]!;
        var error = Assert.Single(theme["errors"]!.AsArray())!;
        Assert.Equal("XamlParse", (string?)error["code"]);
        Assert.Equal(panelXaml, (string?)error["file"], ignoreCase: true);
        Assert.Equal("BuiltIn", (string?)theme["source"]);
        Assert.NotNull((string?)status["panels"]![0]!["background"]);

        var log = Assert.Single(Directory.GetFiles(Path.Combine(run.DataRoot, "logs"), "host-*.log"));
        var line = Assert.Single(File.ReadAllLines(log), l => l.Contains("XamlParse", StringComparison.Ordinal));
        Assert.Contains(panelXaml, line, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task S1_8_a_missing_plugin_shows_a_placeholder_in_the_missing_state()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDataFile(Path.Combine("layouts", "subzerodev.default.json"), """
            {
              "schemaVersion": 1,
              "id": "subzerodev.default",
              "displayName": "Test",
              "panels": [
                {
                  "id": "0c7b8a52-8f8e-4d5e-9e35-6b1f7d1a0001",
                  "monitor": "primary",
                  "placement": { "edge": "bottom" },
                  "thickness": 40,
                  "behaviour": "reserve",
                  "zones": {
                    "start": [ { "id": "0c7b8a52-8f8e-4d5e-9e35-6b1f7d1a0002", "plugin": "test.nonexistent", "contribution": "clock" } ]
                  }
                }
              ]
            }
            """);
        run.Start(HostRun.HostExe);

        await run.WaitForStatusAsync();
        // Every placed instance shows Starting first; the catalog lookup then settles it on Missing.
        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true
            && s["panels"]![0]!["placeholders"]!.AsArray().Any(p => (string?)p!["reason"] == "Missing"), TimeSpan.FromSeconds(5));

        var placeholder = Assert.Single(status["panels"]![0]!["placeholders"]!.AsArray())!;
        Assert.Equal("test.nonexistent", (string?)placeholder["plugin"]);
        Assert.Equal("Missing", (string?)placeholder["reason"]);
        Assert.Equal("Missing", (string?)placeholder["state"]);
        Assert.Equal(0, await run.StopAsync());
    }
}

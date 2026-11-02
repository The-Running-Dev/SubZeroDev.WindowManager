using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S17: replacement mode. The shell value is written under a scratch <c>HKCU</c> key that stands in for
/// both hives, and under the test hook the guardian records Explorer starts and exit requests in
/// <c>shell-actions.log</c> instead of doing them, so the real shell is never touched.
/// </summary>
public sealed class ShellTests
{
    private const string RegistryRootVariable = "SUBZERODEV_WM_REGISTRY_ROOT";
    private const string TestKeyRoot = @"Software\SubZeroDev.WindowManager.Tests";
    private const string WinlogonKey = @"HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    private const string Probe = "samples.probe";
    private const string NeedsExplorer = "samples.needsexplorer";
    private const string CoexistenceOnly = "samples.coexistenceonly";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Theory]
    [InlineData(null)]
    [InlineData(@"C:\Tools\other-shell.exe /quiet")]
    public async Task S17_1_shell_on_records_what_was_there_and_off_puts_it_back(string? previous)
    {
        using var run = HostRun.Create();
        var key = UseTestRegistry(run);
        try
        {
            if (previous is not null)
            {
                using var winlogon = Registry.CurrentUser.CreateSubKey($@"{key}\{WinlogonKey}");
                winlogon.SetValue("Shell", previous);
            }

            Assert.Equal(0, await RunGuardianAsync(run, "--shell", "on"));
            Assert.Equal(0, await RunGuardianAsync(run, "--shell", "on"));
            Assert.Equal($"\"{Path.GetFullPath(HostRun.GuardianExe)}\" --mode replacement", ShellValue(key), ignoreCase: true);
            var state = ShellState(run)!;
            Assert.True((bool)state["registered"]!);
            Assert.Equal(previous is not null, (bool)state["previous"]!["present"]!);
            Assert.Equal(previous, (string?)state["previous"]!["value"]);

            Assert.Equal(0, await RunGuardianAsync(run, "--shell", "off"));
            Assert.Equal(previous, ShellValue(key));
            Assert.False((bool)ShellState(run)!["registered"]!);
        }
        finally
        {
            DeleteTestRegistry(key);
        }
    }

    [Theory]
    [InlineData(@"HKCU\Software\Microsoft\Windows\CurrentVersion\Policies\System")]
    [InlineData(@"HKLM\Software\Microsoft\Windows\CurrentVersion\Policies\System")]
    [InlineData(@"HKLM\SOFTWARE\Microsoft\Windows Embedded\Shell Launcher")]
    public async Task S17_2_an_overriding_policy_or_Shell_Launcher_exits_5_and_writes_nothing(string overriding)
    {
        using var run = HostRun.Create();
        var key = UseTestRegistry(run);
        try
        {
            using (var policy = Registry.CurrentUser.CreateSubKey($@"{key}\{overriding}"))
            {
                policy.SetValue("Shell", @"C:\Kiosk\kiosk.exe");
            }

            Assert.Equal(5, await RunGuardianAsync(run, "--shell", "on"));
            Assert.Null(ShellValue(key));
            Assert.Null(ShellState(run));
        }
        finally
        {
            DeleteTestRegistry(key);
        }
    }

    [Fact]
    public async Task S17_3_replacement_mode_reaches_the_host_and_plugins_and_turns_away_plugins_that_need_Explorer()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Probe);
        run.CopyPlugin(HostRun.TestPluginDirectory, NeedsExplorer, null, manifest => manifest["requiresExplorer"] = true);
        run.CopyPlugin(HostRun.TestPluginDirectory, CoexistenceOnly, null, manifest => manifest["modes"] = new JsonArray("coexistence"));
        run.WriteDefaultLayout((Guid.NewGuid(), Probe, "probe"), (Guid.NewGuid(), NeedsExplorer, "probe"), (Guid.NewGuid(), CoexistenceOnly, "probe"));
        run.Start(HostRun.GuardianExe, "--mode", "replacement");

        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && (string?)Plugin(s, Probe)?["state"] == "Running"
            && (string?)Plugin(s, NeedsExplorer)?["state"] == "Incompatible" && (string?)Plugin(s, CoexistenceOnly)?["state"] == "Incompatible", Startup);
        Assert.Equal("replacement", (string?)status["mode"]);
        Assert.Contains("needs Explorer", (string?)Plugin(status, NeedsExplorer)!["problem"]);
        Assert.Contains("does not support", (string?)Plugin(status, CoexistenceOnly)!["problem"]);
        var modeLog = Path.Combine(run.DataRoot, "plugin-data", Probe, "mode.log");
        await WaitUntilAsync(() => File.Exists(modeLog), run, "the probe's mode.log");
        Assert.Equal(["replacement"], File.ReadAllLines(modeLog));

        // Exiting never leaves the user without a shell: Explorer takes over for the rest of the session.
        Assert.Equal(0, await run.StopAsync());
        Assert.Equal(["explorer"], Actions(run));
    }

    [Fact]
    public async Task S17_4_a_spent_restart_budget_starts_Explorer_before_waiting_on_the_user()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteSettings(run, maxRestarts: 0);
        var guardian = run.Start(HostRun.GuardianExe, "--mode", "replacement");
        var status = await run.WaitForStatusAsync();
        using (var host = System.Diagnostics.Process.GetProcessById((int)status["hostPid"]!))
        {
            host.Kill();
            host.WaitForExit();
        }

        await WaitUntilAsync(() => (string?)run.ReadGuardian()?["state"] == "idle", run, "the guardian to go idle");
        Assert.Equal(["explorer"], Actions(run));

        File.WriteAllText(Path.Combine(run.HookDirectory, "dismiss"), string.Empty);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await guardian.WaitForExitAsync(cancel.Token);
        Assert.Equal(3, guardian.ExitCode);
    }

    [Fact]
    public async Task S17_4_Shift_held_at_sign_in_starts_Explorer_and_no_host()
    {
        using var run = HostRun.Create();
        Hook(run, "shift-held", string.Empty);
        var since = DateTime.Now;

        Assert.Equal(0, await RunGuardianAsync(run, "--mode", "replacement"));
        Assert.Equal(["explorer"], Actions(run));
        Assert.Empty(HostRun.HostsStartedSince(since));
    }

    [Fact]
    public async Task S17_5_another_shell_makes_the_guardian_exit_6_unless_told_to_take_over()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        Hook(run, "shell-owner", "4242");
        var since = DateTime.Now;

        Assert.Equal(6, await RunGuardianAsync(run, "--mode", "replacement"));
        Assert.Empty(Actions(run));
        Assert.Empty(HostRun.HostsStartedSince(since));

        run.Start(HostRun.GuardianExe, "--mode", "replacement", "--take-over");
        var status = await run.WaitForStatusAsync();
        Assert.Equal("replacement", (string?)status["mode"]);
        Assert.Equal(["take-over"], Actions(run));
        Assert.Equal(0, await run.StopAsync());
        Assert.Equal(["take-over", "explorer"], Actions(run));
    }

    [Fact]
    public async Task S17_6_restoreExplorer_unregisters_starts_Explorer_and_leaves_a_clean_journal()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var key = UseTestRegistry(run);
        try
        {
            Assert.Equal(0, await RunGuardianAsync(run, "--shell", "on"));
            Assert.NotNull(ShellValue(key));

            var guardian = run.Start(HostRun.GuardianExe, "--mode", "replacement");
            await run.WaitForStatusAsync();
            Hook(run, "invoke", string.Join("\n", "host.shell.restoreExplorer", "keyboard", new JsonObject { ["unregister"] = true }.ToJsonString()) + "\n");
            using (var cancel = new CancellationTokenSource(Startup))
            {
                await guardian.WaitForExitAsync(cancel.Token);
            }

            Assert.Equal(0, guardian.ExitCode);
            Assert.Equal(["explorer"], Actions(run));
            Assert.Null(ShellValue(key));
            Assert.False((bool)ShellState(run)!["registered"]!);
            Assert.False(File.Exists(Path.Combine(run.DataRoot, "restore-explorer.json")));
            var journal = run.ReadJournal()!;
            Assert.True((bool)journal["cleanExit"]!);
            Assert.Empty(journal["entries"]!.AsArray());
        }
        finally
        {
            DeleteTestRegistry(key);
        }
    }

    [Fact]
    public async Task S17_6_restoreExplorer_names_Explorer_in_the_shell_value_while_it_starts_and_keeps_the_registration()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var key = UseTestRegistry(run);
        try
        {
            Assert.Equal(0, await RunGuardianAsync(run, "--shell", "on"));
            var registered = ShellValue(key);

            var guardian = run.Start(HostRun.GuardianExe, "--mode", "replacement");
            await run.WaitForStatusAsync();
            Hook(run, "invoke", string.Join("\n", "host.shell.restoreExplorer", "keyboard", new JsonObject { ["unregister"] = false }.ToJsonString()) + "\n");
            using (var cancel = new CancellationTokenSource(Startup))
            {
                await guardian.WaitForExitAsync(cancel.Token);
            }

            Assert.Equal(0, guardian.ExitCode);
            Assert.Equal(["explorer"], Actions(run));
            Assert.Equal(registered, ShellValue(key));
            Assert.True((bool)ShellState(run)!["registered"]!);
            Assert.Contains("Put the shell value back after Explorer started.", run.HostLog("guardian-*.log"));
        }
        finally
        {
            DeleteTestRegistry(key);
        }
    }

    [Fact]
    public async Task S17_6_restoreExplorer_is_refused_in_coexistence_mode()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.Start(HostRun.HostExe);
        var before = (await run.WaitForStatusAsync())["lastInvoke"]?.ToJsonString();
        Hook(run, "invoke", string.Join("\n", "host.shell.restoreExplorer", "keyboard", "{}") + "\n");
        var status = await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == "host.shell.restoreExplorer"
            && last.ToJsonString() != before, TimeSpan.FromSeconds(10));
        Assert.Equal("NotInReplacementMode", (string?)status["lastInvoke"]!["code"]);
        Assert.Equal("coexistence", (string?)status["mode"]);
        Assert.False(File.Exists(Path.Combine(run.DataRoot, "restore-explorer.json")));
        Assert.Equal(0, await run.StopAsync());
    }

    private static string UseTestRegistry(HostRun run)
    {
        var key = $@"{TestKeyRoot}\{Guid.NewGuid():N}";
        run.ExtraEnvironment[RegistryRootVariable] = key;
        return key;
    }

    private static void DeleteTestRegistry(string key)
    {
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        using var root = Registry.CurrentUser.OpenSubKey(TestKeyRoot, writable: true);
        if (root is { SubKeyCount: 0, ValueCount: 0 })
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKey(TestKeyRoot, throwOnMissingSubKey: false);
        }
    }

    private static string? ShellValue(string key)
    {
        using var winlogon = Registry.CurrentUser.OpenSubKey($@"{key}\{WinlogonKey}");
        return winlogon?.GetValue("Shell") as string;
    }

    private static JsonObject? ShellState(HostRun run)
    {
        var path = Path.Combine(run.DataRoot, "shell.json");
        return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))!.AsObject() : null;
    }

    private static async Task<int> RunGuardianAsync(HostRun run, params string[] args)
    {
        var guardian = run.Start(HostRun.GuardianExe, args);
        using var cancel = new CancellationTokenSource(Startup);
        await guardian.WaitForExitAsync(cancel.Token);
        return guardian.ExitCode;
    }

    private static string[] Actions(HostRun run)
    {
        var path = Path.Combine(run.HookDirectory, "shell-actions.log");
        return File.Exists(path) ? File.ReadAllLines(path) : [];
    }

    private static void Hook(HostRun run, string name, string content)
    {
        Directory.CreateDirectory(run.HookDirectory);
        File.WriteAllText(Path.Combine(run.HookDirectory, name), content);
    }

    private static void WriteSettings(HostRun run, int maxRestarts) =>
        run.WriteDataFile("settings.json", new JsonObject
        {
            ["schemaVersion"] = 1,
            ["activePreset"] = "subzerodev.default",
            ["restartPolicy"] = new JsonObject { ["maxRestarts"] = maxRestarts, ["windowSeconds"] = 300 },
        }.ToJsonString());

    private static JsonObject? Plugin(JsonObject status, string plugin) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == plugin)?.AsObject();

    private static async Task WaitUntilAsync(Func<bool> condition, HostRun run, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Timed out waiting for {what}. Guardian: {run.ReadGuardian()?.ToJsonString()}{Environment.NewLine}"
                    + $"Guardian log:{Environment.NewLine}{run.HostLog("guardian-*.log")}{Environment.NewLine}Host log:{Environment.NewLine}{run.HostLog()}");
            }

            await Task.Delay(50);
        }
    }
}

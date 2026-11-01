using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S4: the recovery journal, the guardian's watch over the host, and the desktop given back when
/// the host dies. Checkpoint holds (<c>hook\hold-&lt;name&gt;</c>) pause the host around app-bar calls so
/// the journal can be read from this process.
/// </summary>
public sealed class RecoveryTests
{
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S4_1_the_appBar_entry_is_journalled_before_ABM_NEW_and_ended_after_ABM_REMOVE()
    {
        HostRun.RequireDesktop();
        var before = Interactive.PrimaryWorkArea();
        using var run = HostRun.Create();
        File.WriteAllText(Hook(run, "hold-before-appbar"), string.Empty);
        run.Start(HostRun.HostExe);

        await WaitUntilAsync(() => File.Exists(Hook(run, "held-before-appbar")), Startup, run, "the before-appbar checkpoint");
        var entry = Assert.Single(run.ReadJournal()!["entries"]!.AsArray())!;
        Assert.Equal("appBar", (string?)entry["kind"]);
        Assert.Equal(before, Interactive.PrimaryWorkArea());
        File.Delete(Hook(run, "hold-before-appbar"));

        var status = await run.WaitForStatusAsync();
        Assert.NotNull(Panel(status)["appBarRect"]);
        Assert.NotEqual(before, Interactive.PrimaryWorkArea());

        File.WriteAllText(Hook(run, "hold-after-remove"), string.Empty);
        await File.WriteAllTextAsync(Hook(run, "exit"), string.Empty, TestContext.Current.CancellationToken);
        await WaitUntilAsync(() => File.Exists(Hook(run, "held-after-remove")), Startup, run, "the after-remove checkpoint");
        await WaitUntilAsync(() => Interactive.PrimaryWorkArea() == before, TimeSpan.FromSeconds(2), run, "the work area to be restored");
        Assert.Equal((string?)entry["id"], (string?)Assert.Single(run.ReadJournal()!["entries"]!.AsArray())!["id"]);
        File.Delete(Hook(run, "hold-after-remove"));

        await run.Process!.WaitForExitAsync(TestContext.Current.CancellationToken);
        Assert.Equal(0, run.Process.ExitCode);
        Assert.Empty(run.ReadJournal()!["entries"]!.AsArray());
    }

    [Fact]
    public async Task S4_2_a_killed_host_is_reverted_recorded_and_restarted()
    {
        HostRun.RequireDesktop();
        var before = Interactive.PrimaryWorkArea();
        using var run = HostRun.Create();
        run.Start(HostRun.GuardianExe);
        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && Panel(s)["appBarRect"] is not null, Startup);
        var hostPid = (int)status["hostPid"]!;
        Assert.NotEqual(before, Interactive.PrimaryWorkArea());

        var killed = Kill(hostPid);
        await WaitUntilAsync(() => Interactive.PrimaryWorkArea() == before, TimeSpan.FromSeconds(5), run, "the work area to be restored");
        var record = await CrashRecordAsync(run, 1);
        Assert.Equal("hostExited", (string?)record["reason"]);
        Assert.Equal(hostPid, (int?)record["hostPid"]);
        Assert.Single(record["reverted"]!.AsArray());
        Assert.Empty(record["failures"]!.AsArray());

        var restarted = await run.WaitForAsync(s => (int)s["hostPid"]! != hostPid, TimeSpan.FromSeconds(10));
        Assert.Contains((int)restarted["hostPid"]!, HostRun.HostsStartedSince(killed));
        await run.WaitForAsync(s => (bool?)s["ready"] == true && Panel(s)["appBarRect"] is not null, Startup);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S4_3_a_normal_exit_leaves_an_empty_clean_journal()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.Start(HostRun.HostExe);
        var status = await run.WaitForStatusAsync();
        Assert.NotNull(Panel(status)["appBarRect"]);
        Assert.Single(run.ReadJournal()!["entries"]!.AsArray());

        Assert.Equal(0, await run.StopAsync());

        var journal = run.ReadJournal()!;
        Assert.Empty(journal["entries"]!.AsArray());
        Assert.True((bool)journal["cleanExit"]!);
    }

    [Fact]
    public async Task S4_4_recover_only_reverts_a_stale_journal_and_starts_no_host()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        using (var gone = Process.Start(new ProcessStartInfo("cmd.exe", "/c exit") { UseShellExecute = false, CreateNoWindow = true })!)
        {
            await gone.WaitForExitAsync(TestContext.Current.CancellationToken);
            WriteJournal(run, gone.Id, gone.StartTime, cleanExit: false, new JsonObject
            {
                ["id"] = Guid.NewGuid().ToString("D"),
                ["kind"] = "appBar",
                ["monitor"] = "test-monitor",
                ["appliedAt"] = DateTimeOffset.UtcNow.ToString("O"),
                ["payload"] = new JsonObject { ["hwnd"] = 0x7FFF0, ["edge"] = "bottom", ["thicknessPx"] = 40 },
            });
        }

        var started = DateTime.Now;
        var guardian = run.Start(HostRun.GuardianExe, "--recover-only");
        using (var cancel = new CancellationTokenSource(Startup))
        {
            await guardian.WaitForExitAsync(cancel.Token);
        }

        Assert.Equal(0, guardian.ExitCode);
        Assert.Empty(run.ReadJournal()!["entries"]!.AsArray());
        Assert.Empty(HostRun.HostsStartedSince(started));
        var record = JsonNode.Parse(File.ReadAllText(Assert.Single(run.CrashRecords())))!;
        Assert.Equal("staleJournal", (string?)record["reason"]);
        Assert.Single(record["reverted"]!.AsArray());
    }

    [Fact]
    public async Task S4_5_four_kills_in_the_window_leave_the_guardian_idle_until_dismissed()
    {
        HostRun.RequireDesktop();
        var before = Interactive.PrimaryWorkArea();
        using var run = HostRun.Create();
        var guardian = run.Start(HostRun.GuardianExe);
        var hostPid = 0;
        DateTime lastKill = default;
        for (var kill = 1; kill <= 4; kill++)
        {
            var previous = hostPid;
            var status = await run.WaitForAsync(s => (int)s["hostPid"]! != previous && (bool?)s["ready"] == true
                && Panel(s)["appBarRect"] is not null, Startup);
            hostPid = (int)status["hostPid"]!;
            lastKill = Kill(hostPid);
            await CrashRecordAsync(run, kill);
        }

        await WaitUntilAsync(() => (string?)run.ReadGuardian()?["state"] == "idle", TimeSpan.FromSeconds(10), run, "the guardian to go idle");
        Assert.Equal(before, Interactive.PrimaryWorkArea());
        await Task.Delay(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
        Assert.Empty(HostRun.HostsStartedSince(lastKill));
        Assert.False(guardian.HasExited);

        File.WriteAllText(Hook(run, "dismiss"), string.Empty);
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await guardian.WaitForExitAsync(cancel.Token);
        Assert.Equal(3, guardian.ExitCode);
    }

    [Fact]
    public async Task S4_6_a_host_that_stops_answering_pings_is_killed_after_30_s_and_recovered()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.Start(HostRun.GuardianExe);
        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && Panel(s)["appBarRect"] is not null, Startup);
        var hostPid = (int)status["hostPid"]!;
        using var host = Process.GetProcessById(hostPid);
        _ = host.Handle;

        // Let a few pings through first, so the hang starts with a recent reply.
        await Task.Delay(TimeSpan.FromSeconds(6), TestContext.Current.CancellationToken);
        var hung = DateTime.UtcNow;
        File.WriteAllText(Hook(run, "hang"), string.Empty);
        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(60)))
        {
            await host.WaitForExitAsync(cancel.Token);
        }

        var waited = DateTime.UtcNow - hung;
        Assert.InRange(waited.TotalSeconds, 24, 45);
        var record = await CrashRecordAsync(run, 1);
        Assert.Equal("hostHung", (string?)record["reason"]);
        Assert.Equal(hostPid, (int?)record["hostPid"]);
        await run.WaitForAsync(s => (int)s["hostPid"]! != hostPid && (bool?)s["ready"] == true, TimeSpan.FromSeconds(30));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S4_7_a_killed_guardian_is_relaunched_by_the_host_with_attach()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var guardian = run.Start(HostRun.GuardianExe);
        var status = await run.WaitForStatusAsync();
        var hostPid = (int)status["hostPid"]!;
        using var host = Process.GetProcessById(hostPid);

        guardian.Kill();
        await guardian.WaitForExitAsync(TestContext.Current.CancellationToken);
        var attached = await WaitForGuardianAsync(run, g => (int?)g["pid"] != guardian.Id && (string?)g["state"] == "watching",
            TimeSpan.FromSeconds(5));

        Assert.Equal<string>(["--attach", hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture)],
            attached["args"]!.AsArray().Select(a => (string)a!).ToArray());
        Assert.Equal(hostPid, (int?)attached["hostPid"]);
        Assert.False(host.HasExited);
        await run.WaitForAsync(s => (int?)s["guardianPid"] == (int)attached["pid"]!, TimeSpan.FromSeconds(5));

        using var relaunched = Process.GetProcessById((int)attached["pid"]!);
        await File.WriteAllTextAsync(Hook(run, "exit"), string.Empty, TestContext.Current.CancellationToken);
        using var cancel = new CancellationTokenSource(Startup);
        await host.WaitForExitAsync(cancel.Token);
        await relaunched.WaitForExitAsync(cancel.Token);
        Assert.Equal(0, host.ExitCode);
        Assert.Equal(0, relaunched.ExitCode);
    }

    [Fact]
    public async Task S4_8_an_unreadable_journal_is_moved_aside_and_startup_continues()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        Directory.CreateDirectory(Path.GetDirectoryName(run.JournalPath)!);
        File.WriteAllText(run.JournalPath, "{ not a journal");
        run.Start(HostRun.HostExe);

        var status = await run.WaitForStatusAsync();

        Assert.Equal("{ not a journal", File.ReadAllText(run.JournalPath + ".corrupt"));
        Assert.Contains("JournalUnreadable", run.HostLog(), StringComparison.Ordinal);
        Assert.NotNull(Panel(status)["appBarRect"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S4_9_a_failed_journal_write_shows_the_panel_as_an_overlay_without_an_app_bar()
    {
        HostRun.RequireDesktop();
        var before = Interactive.PrimaryWorkArea();
        using var run = HostRun.Create();
        WriteJournal(run, 0, DateTime.Now, cleanExit: true);
        File.SetAttributes(run.JournalPath, FileAttributes.ReadOnly);
        run.Start(HostRun.HostExe);

        var status = await run.WaitForStatusAsync();

        var panel = Panel(status);
        Assert.Equal("overlay", (string?)panel["behaviour"]);
        Assert.Equal("JournalWriteFailed", (string?)panel["reserveError"]);
        Assert.Null(panel["appBarRect"]);
        Assert.Equal(before, Interactive.PrimaryWorkArea());
        Assert.Contains("JournalWriteFailed", run.HostLog(), StringComparison.Ordinal);
        Assert.Equal(0, await run.StopAsync());
    }

    private static string Hook(HostRun run, string name) => Path.Combine(run.HookDirectory, name);

    private static JsonObject Panel(JsonObject status) => Assert.Single(status["panels"]!.AsArray())!.AsObject();

    /// <summary>Terminates the process, as Task Manager's End task would, and returns when it did.</summary>
    private static DateTime Kill(int pid)
    {
        using var process = Process.GetProcessById(pid);
        var at = DateTime.Now;
        process.Kill();
        process.WaitForExit();
        return at;
    }

    private static void WriteJournal(HostRun run, int hostPid, DateTime hostStartTime, bool cleanExit, params JsonObject[] entries)
    {
        var journal = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["hostPid"] = hostPid,
            ["hostStartTime"] = new DateTimeOffset(hostStartTime).ToString("O"),
            ["cleanExit"] = cleanExit,
            ["entries"] = new JsonArray(entries.Select(e => (JsonNode?)e).ToArray()),
        };
        Directory.CreateDirectory(Path.GetDirectoryName(run.JournalPath)!);
        File.WriteAllText(run.JournalPath, journal.ToJsonString());
    }

    private static async Task<JsonObject> CrashRecordAsync(HostRun run, int count)
    {
        await WaitUntilAsync(() => run.CrashRecords().Length >= count, TimeSpan.FromSeconds(10), run, $"crash record {count}");
        var path = run.CrashRecords()[count - 1];
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                return JsonNode.Parse(File.ReadAllText(path))!.AsObject();
            }
            catch (Exception e) when (attempt < 20 && e is IOException or System.Text.Json.JsonException)
            {
                await Task.Delay(50);
            }
        }
    }

    private static async Task<JsonObject> WaitForGuardianAsync(HostRun run, Func<JsonObject, bool> condition, TimeSpan timeout)
    {
        JsonObject? last = null;
        await WaitUntilAsync(() => (last = run.ReadGuardian()) is { } g && condition(g), timeout, run, "the guardian state");
        return last!;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout, HostRun run, string what)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"Timed out after {timeout} waiting for {what}. Guardian: {run.ReadGuardian()?.ToJsonString()}{Environment.NewLine}"
                    + $"Guardian log:{Environment.NewLine}{run.HostLog("guardian-*.log")}{Environment.NewLine}Host log:{Environment.NewLine}{run.HostLog()}");
            }

            await Task.Delay(50);
        }
    }
}

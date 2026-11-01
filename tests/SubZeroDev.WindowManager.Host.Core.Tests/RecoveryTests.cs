using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Recovery;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S4: the recovery journal, the reverter and the stale-journal procedure.</summary>
public sealed class RecoveryTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 1, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void S4_1_a_journal_round_trips_with_camel_case_keys()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 3);
        var change = DesktopChange.AppBar("DISPLAY1", 0x1234, "bottom", 40, Start);

        Assert.True(Journal.Write(path, JournalDocument.Fresh(77, Start) with { Entries = [change] }).Ok);

        Assert.Equal(Path.Combine(scratch.DataRoot, "recovery", "session-3.json"), path);
        var text = File.ReadAllText(path);
        Assert.Contains("\"monitor\": \"DISPLAY1\"", text, StringComparison.Ordinal);
        Assert.Contains("\"kind\": \"appBar\"", text, StringComparison.Ordinal);
        Assert.Contains("\"cleanExit\": false", text, StringComparison.Ordinal);
        Assert.False(File.Exists(path + ".tmp"));

        var read = Journal.Read(path);
        Assert.True(read.Ok);
        var entry = Assert.Single(read.Value!.Entries);
        Assert.Equal(change.Id, entry.Id);
        Assert.Equal(0x1234, entry.Payload["hwnd"]!.GetValue<long>());
        Assert.Equal(77, read.Value.HostPid);
    }

    [Fact]
    public void S4_8_an_absent_journal_reads_as_null_and_garbage_as_unreadable()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 1);

        var absent = Journal.Read(path);
        Assert.True(absent.Ok);
        Assert.Null(absent.Value);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        foreach (var garbage in new[] { "{ not json", "{\"schemaVersion\": 9, \"entries\": []}", "{\"schemaVersion\": 1}" })
        {
            File.WriteAllText(path, garbage);
            Assert.Equal(RecoveryErrorCode.JournalUnreadable, Journal.Read(path).Error?.Code);
        }
    }

    [Fact]
    public void S4_9_a_read_only_journal_fails_the_write()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 1);
        Assert.True(Journal.Write(path, JournalDocument.Fresh(1, Start)).Ok);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var outcome = Journal.Write(path, JournalDocument.Fresh(2, Start));

            Assert.Equal(RecoveryErrorCode.JournalWriteFailed, outcome.Error?.Code);
            Assert.False(File.Exists(path + ".tmp"));
            Assert.Equal(1, Journal.Read(path).Value!.HostPid);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void S4_2_entries_are_reverted_newest_first_and_failures_are_returned()
    {
        var first = DesktopChange.AppBar("A", 1, "bottom", 40, Start);
        var second = DesktopChange.AppBar("B", 2, "top", 40, Start.AddSeconds(1));
        var third = DesktopChange.AppBar("C", 3, "left", 40, Start.AddSeconds(2));
        var doc = JournalDocument.Fresh(1, Start) with { Entries = [first, second, third] };
        var order = new List<string>();

        var failures = Reverter.RevertAll(doc, change =>
        {
            order.Add(change.MonitorId);
            return change == second ? Recovery.Outcome.Fail(RecoveryErrorCode.RevertFailed, "refused") : Recovery.Outcome.Success;
        });

        Assert.Equal(["C", "B", "A"], order);
        var failure = Assert.Single(failures);
        Assert.Equal(second.Id, failure.Change.Id);
    }

    [Fact]
    public void S4_2_an_app_bar_entry_without_a_window_handle_cannot_be_reverted()
    {
        var change = new DesktopChange(Guid.NewGuid(), DesktopChangeKind.AppBar, "A", Start, new JsonObject { ["edge"] = "bottom" });

        Assert.Equal(RecoveryErrorCode.RevertFailed, Reverter.Revert(change).Error?.Code);
    }

    [Fact]
    public async Task S4_1_the_host_journal_records_begin_end_and_clean_exit_on_disk()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 1);
        var journal = new RecoveryJournal(path, 42, Start);
        Assert.True((await journal.StartAsync(CancellationToken.None)).Ok);

        var change = DesktopChange.AppBar("A", 9, "bottom", 40, Start);
        var begun = await journal.BeginAsync(change, CancellationToken.None);
        Assert.True(begun.Ok);
        Assert.Equal(change.Id, Assert.Single(Journal.Read(path).Value!.Entries).Id);

        Assert.True((await journal.EndAsync(change.Id, CancellationToken.None)).Ok);
        Assert.Empty(Journal.Read(path).Value!.Entries);
        Assert.Equal(CoreErrorCode.NotFound, (await journal.EndAsync(change.Id, CancellationToken.None)).Error?.Code);

        Assert.True((await journal.MarkCleanExitAsync(CancellationToken.None)).Ok);
        var final = Journal.Read(path).Value!;
        Assert.True(final.CleanExit);
        Assert.Empty(final.Entries);
    }

    [Fact]
    public async Task S4_9_a_failed_begin_is_journal_write_failed_and_leaves_no_entry()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 1);
        var journal = new RecoveryJournal(path, 42, Start);
        Assert.True((await journal.StartAsync(CancellationToken.None)).Ok);
        File.SetAttributes(path, FileAttributes.ReadOnly);
        try
        {
            var begun = await journal.BeginAsync(DesktopChange.AppBar("A", 9, "bottom", 40, Start), CancellationToken.None);

            Assert.Equal(CoreErrorCode.JournalWriteFailed, begun.Error?.Code);
            Assert.Empty(journal.Entries);
        }
        finally
        {
            File.SetAttributes(path, FileAttributes.Normal);
        }
    }

    [Fact]
    public void S4_4_a_dead_hosts_entries_are_reverted_the_journal_cleared_and_a_crash_record_written()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 1);
        var change = DesktopChange.AppBar("A", 9, "bottom", 40, Start);
        Journal.Write(path, JournalDocument.Fresh(4242, Start) with { Entries = [change] });
        var reverted = new List<Guid>();

        var report = Recoverer.Recover(scratch.DataRoot, 1, "staleJournal", hostExited: false, exitCode: null, _ => { },
            (_, _) => false, doc =>
            {
                reverted.AddRange(doc.Entries.Select(e => e.Id));
                return [];
            });

        Assert.Equal([change.Id], reverted);
        Assert.Equal(1, report.Reverted);
        Assert.Empty(Journal.Read(path).Value!.Entries);
        var record = JsonNode.Parse(File.ReadAllText(report.CrashRecordPath!))!;
        Assert.Equal("staleJournal", record["reason"]!.GetValue<string>());
        Assert.Equal(4242, record["hostPid"]!.GetValue<int>());
        Assert.Equal("appBar", record["reverted"]![0]!["kind"]!.GetValue<string>());
        Assert.StartsWith(Path.Combine(scratch.DataRoot, "logs", "crash"), report.CrashRecordPath, StringComparison.Ordinal);
    }

    [Fact]
    public void S4_4_a_live_hosts_journal_is_left_alone()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 1);
        Journal.Write(path, JournalDocument.Fresh(4242, Start) with { Entries = [DesktopChange.AppBar("A", 9, "bottom", 40, Start)] });

        var report = Recoverer.Recover(scratch.DataRoot, 1, "startup", hostExited: false, exitCode: null, _ => { },
            (_, _) => true, _ => throw new InvalidOperationException("must not revert"));

        Assert.True(report.HostAlive);
        Assert.Single(Journal.Read(path).Value!.Entries);
        Assert.Null(report.CrashRecordPath);
    }

    [Fact]
    public void S4_8_an_unreadable_journal_is_moved_aside_and_logged()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 1);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "{ not json");
        var lines = new List<string>();

        var report = Recoverer.Recover(scratch.DataRoot, 1, "startup", hostExited: false, exitCode: null, lines.Add);

        Assert.Equal(path + ".corrupt", report.CorruptPath);
        Assert.False(File.Exists(path));
        Assert.Equal("{ not json", File.ReadAllText(path + ".corrupt"));
        Assert.Contains(lines, line => line.Contains("JournalUnreadable", StringComparison.Ordinal));
    }

    [Fact]
    public void S4_2_a_watched_host_exit_always_writes_a_crash_record()
    {
        using var scratch = new Scratch();

        var report = Recoverer.Recover(scratch.DataRoot, 1, "hostExited", hostExited: true, exitCode: -1, _ => { });

        var record = JsonNode.Parse(File.ReadAllText(report.CrashRecordPath!))!;
        Assert.Equal(-1, record["exitCode"]!.GetValue<int>());
        Assert.Empty(record["reverted"]!.AsArray());
    }

    [Fact]
    public void A_host_is_alive_only_while_its_pid_and_start_time_both_match()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var started = new DateTimeOffset(self.StartTime);

        Assert.True(Recoverer.IsAlive(self.Id, started));
        Assert.False(Recoverer.IsAlive(self.Id, started.AddHours(-1)));
        Assert.False(Recoverer.IsAlive(int.MaxValue, started));
    }

    [Fact]
    public async Task The_journal_can_be_replaced_and_read_while_the_other_process_is_reading_it()
    {
        using var scratch = new Scratch();
        var path = Journal.PathForSession(scratch.DataRoot, 1);
        var first = new JournalDocument(JournalDocument.CurrentSchemaVersion, 1, DateTimeOffset.UnixEpoch, false, []);
        Assert.Null(Journal.Write(path, first).Error);

        // Windows will not rename over an open file, so the write retries until the reader lets go.
        var reader = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Assert.False(Journal.Read(path).Value!.CleanExit);
        var write = Task.Run(() => Journal.Write(path, first with { CleanExit = true }), TestContext.Current.CancellationToken);
        await Task.Delay(TimeSpan.FromMilliseconds(30), TestContext.Current.CancellationToken);
        await reader.DisposeAsync();

        Assert.Null((await write).Error);
        Assert.True(Journal.Read(path).Value!.CleanExit);
    }
}

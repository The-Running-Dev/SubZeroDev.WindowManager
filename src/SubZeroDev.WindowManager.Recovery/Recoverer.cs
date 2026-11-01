using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Recovery;

/// <summary>What one recovery pass did.</summary>
public sealed record RecoveryReport(int Reverted, IReadOnlyList<(DesktopChange Change, RecoveryError Error)> Failures,
    string? CrashRecordPath, string? CorruptPath, bool HostAlive)
{
    public static readonly RecoveryReport Nothing = new(0, [], null, null, false);
}

/// <summary>
/// The stale-journal procedure shared by guardian and host: an unreadable journal is moved aside, a live
/// host's journal is left alone, and a dead host's entries are reverted newest first, the journal is cleared
/// and a crash record is written to <c>logs\crash\</c>.
/// </summary>
public static class Recoverer
{
    private static readonly TimeSpan StartTimeTolerance = TimeSpan.FromSeconds(2);

    /// <param name="hostExited">True when the caller watched the journal's host exit; the journal is then reverted without a liveness check and a crash record is always written.</param>
    public static RecoveryReport Recover(string dataRoot, int sessionId, string reason, bool hostExited, int? exitCode, Action<string> log) =>
        Recover(dataRoot, sessionId, reason, hostExited, exitCode, log, IsAlive, Reverter.RevertAll);

    internal static RecoveryReport Recover(string dataRoot, int sessionId, string reason, bool hostExited, int? exitCode,
        Action<string> log, Func<int, DateTimeOffset, bool> isAlive,
        Func<JournalDocument, IReadOnlyList<(DesktopChange, RecoveryError)>> revertAll)
    {
        var path = Journal.PathForSession(dataRoot, sessionId);
        var read = Journal.Read(path);
        string? corruptPath = null;
        JournalDocument? doc = null;
        if (read.Error is { } unreadable)
        {
            corruptPath = MoveAside(path, log);
            log($"Recovery journal unreadable ({unreadable}); moved to {corruptPath ?? "nowhere"}.");
        }
        else
        {
            doc = read.Value;
        }

        if (!hostExited)
        {
            if (doc is null || doc.Entries.Count == 0)
            {
                return RecoveryReport.Nothing with { CorruptPath = corruptPath };
            }

            if (isAlive(doc.HostPid, doc.HostStartTime))
            {
                log($"Recovery journal belongs to running host {doc.HostPid}; left alone.");
                return RecoveryReport.Nothing with { CorruptPath = corruptPath, HostAlive = true };
            }
        }

        var failures = doc is null ? [] : revertAll(doc);
        foreach (var (change, error) in failures)
        {
            log($"Could not revert {change.Kind} {change.Id} on {change.MonitorId}: {error}");
        }

        if (doc is not null && doc.Entries.Count > 0)
        {
            if (Journal.Write(path, doc with { Entries = [] }).Error is { } writeError)
            {
                log($"Could not clear the recovery journal: {writeError}");
            }

            log($"Reverted {doc.Entries.Count - failures.Count} of {doc.Entries.Count} desktop change(s) left by host {doc.HostPid}.");
        }

        var crash = WriteCrashRecord(dataRoot, reason, doc, exitCode, failures, corruptPath, log);
        return new RecoveryReport(doc is null ? 0 : doc.Entries.Count - failures.Count, failures, crash, corruptPath, false);
    }

    /// <summary>True when the process exists and started when the journal says (within 2 s), so a reused pid is not mistaken for the host.</summary>
    public static bool IsAlive(int pid, DateTimeOffset startTime)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited
                && (new DateTimeOffset(process.StartTime) - startTime).Duration() <= StartTimeTolerance;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static string? MoveAside(string path, Action<string> log)
    {
        var target = path + ".corrupt";
        if (File.Exists(target))
        {
            target = $"{path}.{DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture)}.corrupt";
        }

        try
        {
            File.Move(path, target);
            return target;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Could not move the unreadable journal aside: {e.Message}");
            return null;
        }
    }

    private static string? WriteCrashRecord(string dataRoot, string reason, JournalDocument? doc, int? exitCode,
        IReadOnlyList<(DesktopChange Change, RecoveryError Error)> failures, string? corruptPath, Action<string> log)
    {
        var now = DateTimeOffset.UtcNow;
        var reverted = new JsonArray();
        foreach (var change in doc?.Entries ?? [])
        {
            if (!failures.Any(f => f.Change.Id == change.Id))
            {
                reverted.Add(new JsonObject
                {
                    ["id"] = change.Id.ToString(),
                    ["kind"] = JsonNamingPolicy.CamelCase.ConvertName(change.Kind.ToString()),
                    ["monitor"] = change.MonitorId,
                });
            }
        }

        var record = new JsonObject
        {
            ["timeUtc"] = now.ToString("O", CultureInfo.InvariantCulture),
            ["reason"] = reason,
            ["hostPid"] = doc?.HostPid,
            ["hostStartTime"] = doc?.HostStartTime.ToString("O", CultureInfo.InvariantCulture),
            ["exitCode"] = exitCode,
            ["reverted"] = reverted,
            ["failures"] = new JsonArray(failures.Select(f => (JsonNode)new JsonObject
            {
                ["id"] = f.Change.Id.ToString(),
                ["code"] = JsonNamingPolicy.CamelCase.ConvertName(f.Error.Code.ToString()),
                ["detail"] = f.Error.Detail,
            }).ToArray()),
            ["corruptPath"] = corruptPath,
        };

        try
        {
            var directory = Path.Combine(dataRoot, "logs", "crash");
            Directory.CreateDirectory(directory);
            var stem = now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture);
            var path = Path.Combine(directory, stem + ".json");
            for (var n = 1; File.Exists(path); n++)
            {
                path = Path.Combine(directory, $"{stem}-{n.ToString(CultureInfo.InvariantCulture)}.json");
            }

            File.WriteAllText(path, record.ToJsonString(Journal.Options));
            log($"Crash record written to {path}.");
            return path;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log($"Could not write the crash record: {e.Message}");
            return null;
        }
    }
}

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SubZeroDev.WindowManager.Recovery;

public enum DesktopChangeKind { AppBar, WorkArea }

/// <summary>
/// One reversible desktop change. An <c>appBar</c> payload holds <c>hwnd</c>, <c>edge</c> and
/// <c>thicknessPx</c>; a <c>workArea</c> payload holds the original rectangle (<c>left</c>, <c>top</c>,
/// <c>right</c>, <c>bottom</c>).
/// </summary>
public sealed record DesktopChange(Guid Id, DesktopChangeKind Kind, [property: JsonPropertyName("monitor")] string MonitorId,
    DateTimeOffset AppliedAt, JsonObject Payload)
{
    public static DesktopChange AppBar(string monitorId, nint hwnd, string edge, int thicknessPx, DateTimeOffset appliedAt) =>
        new(Guid.NewGuid(), DesktopChangeKind.AppBar, monitorId, appliedAt, new JsonObject
        {
            ["hwnd"] = (long)hwnd,
            ["edge"] = edge,
            ["thicknessPx"] = thicknessPx,
        });
}

/// <summary>The session's journal: which host wrote it, whether it exited cleanly, and the changes still applied, oldest first.</summary>
public sealed record JournalDocument(int SchemaVersion, int HostPid, DateTimeOffset HostStartTime, bool CleanExit,
    IReadOnlyList<DesktopChange> Entries)
{
    public const int CurrentSchemaVersion = 1;

    public static JournalDocument Fresh(int hostPid, DateTimeOffset hostStartTime) =>
        new(CurrentSchemaVersion, hostPid, hostStartTime, false, []);
}

public enum RecoveryErrorCode { JournalUnreadable, JournalWriteFailed, RevertFailed }

public sealed record RecoveryError(RecoveryErrorCode Code, string Detail)
{
    public override string ToString() => $"{Code}: {Detail}";
}

public readonly record struct Outcome(RecoveryError? Error)
{
    public bool Ok => Error is null;

    public static Outcome Success => default;

    public static Outcome Fail(RecoveryErrorCode code, string detail) => new(new RecoveryError(code, detail));
}

public readonly record struct Outcome<T>(T? Value, RecoveryError? Error)
{
    public bool Ok => Error is null;

    public static Outcome<T> Success(T? value) => new(value, null);

    public static Outcome<T> Fail(RecoveryErrorCode code, string detail) => new(default, new RecoveryError(code, detail));
}

/// <summary>Names shared by the guardian and the host's guardian link.</summary>
public static class GuardianNames
{
    public const string PipePrefix = "SubZeroDev.WindowManager.Guardian.";
    public const string Ping = "ping";
    public const string Pong = "pong";

    /// <summary>
    /// Written to the data folder by <c>host.shell.restoreExplorer</c> before the host exits normally:
    /// <c>{ "unregister": bool }</c>. The guardian deletes it, unregisters the shell if asked, and starts Explorer.
    /// </summary>
    public const string RestoreExplorerFile = "restore-explorer.json";

    public static string NewPipe() => PipePrefix + Guid.NewGuid().ToString("N");

    /// <summary>The pipe a guardian started with <c>--attach &lt;hostPid&gt;</c> listens on, so the host knows it without being told.</summary>
    public static string AttachPipe(int hostPid) => PipePrefix + "attach-" + hostPid.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace SubZeroDev.WindowManager.Recovery;

public enum DesktopChangeKind { AppBar, WorkArea, HiddenWindows }

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

    /// <summary>The monitor field of a change that belongs to no monitor.</summary>
    public const string AllMonitors = "all";

    /// <summary>
    /// The windows the shell has hidden for other desktops. The payload is <c>{ windows: [ { hwnd, processId,
    /// processStartTicks, threadId, className } ] }</c>, the five fields the guardian must see again before it shows one.
    /// </summary>
    public static DesktopChange HiddenWindows(IEnumerable<HiddenWindow> windows, DateTimeOffset appliedAt) =>
        new(Guid.NewGuid(), DesktopChangeKind.HiddenWindows, AllMonitors, appliedAt, new JsonObject
        {
            ["windows"] = new JsonArray([.. windows.Select(w => (JsonNode?)new JsonObject
            {
                ["hwnd"] = (long)w.Handle,
                ["processId"] = w.ProcessId,
                ["processStartTicks"] = w.ProcessStartTicks,
                ["threadId"] = w.ThreadId,
                ["className"] = w.ClassName,
            })]),
        });

    /// <summary>A work area set by the host; the payload is the rectangle to put back.</summary>
    public static DesktopChange WorkArea(string monitorId, int left, int top, int right, int bottom, DateTimeOffset appliedAt) =>
        new(Guid.NewGuid(), DesktopChangeKind.WorkArea, monitorId, appliedAt, new JsonObject
        {
            ["left"] = left,
            ["top"] = top,
            ["right"] = right,
            ["bottom"] = bottom,
        });
}

/// <summary>A window's identity as the journal records it: the handle and the four fingerprint fields.</summary>
public readonly record struct HiddenWindow(nint Handle, int ProcessId, long ProcessStartTicks, int ThreadId, string ClassName);

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

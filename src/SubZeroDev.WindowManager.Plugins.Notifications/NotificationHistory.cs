namespace SubZeroDev.WindowManager.Plugins.Notifications;

/// <summary>One notification as the history port reports it. The text lives in memory only (invariant I31).</summary>
/// <param name="Id">The id Windows gave it, as text.</param>
/// <param name="AppId">The source application's AppUserModelID.</param>
/// <param name="AppName">The source application's display name.</param>
/// <param name="Title">The first line of text.</param>
/// <param name="Body">The remaining lines, joined with a line break.</param>
/// <param name="At">When it was raised.</param>
/// <param name="Silent">Whether it asked for no sound; the real listener cannot tell, so it is always false there.</param>
public sealed record NotificationItem(string Id, string AppId, string AppName, string Title, string Body, DateTimeOffset At, bool Silent);

/// <summary>What one read of the history found, including whether the worker may read it at all.</summary>
/// <param name="Identity"><c>ok</c> when the worker has package identity, else <c>none</c>.</param>
/// <param name="Access"><c>allowed</c>, <c>denied</c> or <c>unspecified</c>, as Windows reports the listener's access.</param>
/// <param name="SwitchedOn">Whether the account's notifications switch is on.</param>
/// <param name="Items">The history, newest first; empty unless identity and access allow reading.</param>
public sealed record HistorySnapshot(string Identity, string Access, bool SwitchedOn, IReadOnlyList<NotificationItem> Items)
{
    public bool CanRead => Identity == "ok" && Access == "allowed";
}

/// <summary>A failure the plugin reports under a name from the contract's error table.</summary>
public sealed class HistoryException(string code, string message, bool retryable = false) : Exception(message)
{
    public string Code { get; } = code;

    public bool Retryable { get; } = retryable;
}

/// <summary>
/// The worker's port to Windows' notification history (second release, C4). The real implementation wraps the user
/// notification listener; tests use a fake driven from the test hook directory, so nothing reads the user's own
/// notifications (invariant I38).
/// </summary>
public interface INotificationHistory : IDisposable
{
    /// <summary>Raised, possibly on any thread, when the history may have changed. A read is the authority.</summary>
    event EventHandler? Changed;

    /// <summary>Reads the history. Only a failed read throws, as <see cref="HistoryException"/> <c>HistoryReadFailed</c>.</summary>
    Task<HistorySnapshot> ReadAsync(CancellationToken ct);

    Task DismissAsync(string id, CancellationToken ct);

    /// <summary>Removes every notification, or only those of <paramref name="appId"/>.</summary>
    Task ClearAsync(string? appId, CancellationToken ct);

    /// <summary>Starts the source application; throws <see cref="HistoryException"/> <c>ActivationFailed</c> when it cannot.</summary>
    Task ActivateAsync(string id, CancellationToken ct);
}

/// <summary>Error names from the contract's table.</summary>
public static class ErrorNames
{
    public const string IdentityMissing = "IdentityMissing";
    public const string AccessDenied = "AccessDenied";
    public const string NotificationsOff = "NotificationsOff";
    public const string HistoryReadFailed = "HistoryReadFailed";
    public const string ActivationFailed = "ActivationFailed";
}

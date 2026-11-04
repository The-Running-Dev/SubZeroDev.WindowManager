namespace SubZeroDev.WindowManager.Host.Core;

public enum CoreErrorCode
{
    Conflict,
    DocumentReadOnly,
    DocumentInvalid,
    IoFailure,
    PackageInvalid,
    PackageInUse,
    NotFound,
    InvalidState,
    DependencyMissing,
    DependencyCycle,
    HostApiMismatch,
    ModeUnsupported,
    StartFailed,
    HotkeyInUse,
    AppBarRefused,
    WindowGone,
    ForegroundDenied,
    JournalWriteFailed,
    PluginUnavailable,
    CommandNotFound,
    Timeout,
    Cancelled,
    Undeclared,
    CommandFailed,
    PluginDisabled,
    ShellRoleTaken,
    ShellOverridden,
    SessionCommandRefused,
    NotInReplacementMode,

    /// <summary>A window reference from another run of the shell; no window was touched.</summary>
    StaleWindowReference,

    /// <summary>No desktop has that id; nothing changed.</summary>
    DesktopNotFound,

    /// <summary>The only desktop cannot be removed.</summary>
    LastDesktop,

    /// <summary>The window refuses to be hidden (hung, elevated or self-showing); it stays visible.</summary>
    WindowNotMovable,

    /// <summary>A later switch replaced this one before it started.</summary>
    SwitchSuperseded,
}

public sealed record CoreError(CoreErrorCode Code, string Detail, bool Retryable);

public readonly record struct Outcome(CoreError? Error)
{
    public bool Ok => Error is null;

    public static Outcome Success => default;

    public static Outcome Fail(CoreErrorCode code, string detail, bool retryable = false) =>
        new(new CoreError(code, detail, retryable));
}

public readonly record struct Outcome<T>(T? Value, CoreError? Error)
{
    public bool Ok => Error is null;

    public static Outcome<T> Success(T value) => new(value, null);

    public static Outcome<T> Fail(CoreErrorCode code, string detail, bool retryable = false) =>
        new(default, new CoreError(code, detail, retryable));

    public static Outcome<T> Fail(CoreError error) => new(default, error);
}

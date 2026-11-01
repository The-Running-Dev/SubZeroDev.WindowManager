using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Sdk;

public enum ErrorCode
{
    PluginUnavailable = WireErrorCodes.PluginUnavailable,
    CommandNotFound = WireErrorCodes.CommandNotFound,
    Timeout = WireErrorCodes.Timeout,
    IncompatibleVersion = WireErrorCodes.IncompatibleVersion,
    InvalidView = WireErrorCodes.InvalidView,
    Cancelled = WireErrorCodes.Cancelled,
    Undeclared = WireErrorCodes.Undeclared,
    SettingsInvalid = WireErrorCodes.SettingsInvalid,
    CommandFailed = WireErrorCodes.CommandFailed,
    Unauthorised = WireErrorCodes.Unauthorised,

    /// <summary>Called before <c>initialize</c> or after <c>shutdown</c>.</summary>
    NotConnected = 2000,
}

public sealed record SdkError(ErrorCode Code, string Detail, bool Retryable);

public readonly record struct SdkResult(SdkError? Error)
{
    public bool Ok => Error is null;

    public static SdkResult Success => default;
}

public readonly record struct SdkResult<T>(T? Value, SdkError? Error)
{
    public bool Ok => Error is null;
}

/// <summary>Thrown synchronously by <see cref="Surface.SetView"/> for a tree that breaks I11; a programming error in the plugin.</summary>
public sealed class InvalidViewException(ValidationResult result)
    : Exception($"The view is invalid: {result}")
{
    public ValidationResult Result { get; } = result;
}

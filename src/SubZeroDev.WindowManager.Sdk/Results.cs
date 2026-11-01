using System.Text.Json.Nodes;
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

/// <summary>Requests to the host that answer with no value, mapped to <see cref="SdkResult"/>.</summary>
internal static class HostRequests
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    public static async Task<SdkResult> SendAsync(Connection connection, string method, object parameters, string subject,
        CancellationToken ct)
    {
        if (!connection.IsOpen)
        {
            return new(new SdkError(ErrorCode.NotConnected, "The host connection is closed.", false));
        }

        RpcMessage response;
        try
        {
            response = await connection.RequestAsync(method, parameters, RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new(new SdkError(ErrorCode.Timeout, $"The host did not answer {method} for {subject}.", true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(new SdkError(ErrorCode.Cancelled, $"{method} for {subject} was cancelled.", false));
        }
        catch (IOException e)
        {
            return new(new SdkError(ErrorCode.NotConnected, e.Message, false));
        }

        if (response.Error is { } error)
        {
            var retryable = error.Data is JsonObject data && data["retryable"] is JsonValue v && v.TryGetValue<bool>(out var r) && r;
            return new(new SdkError((ErrorCode)error.Code, error.Message, retryable));
        }

        return SdkResult.Success;
    }
}

/// <summary>Thrown synchronously by <see cref="Surface.SetView"/> for a tree that breaks I11; a programming error in the plugin.</summary>
public sealed class InvalidViewException(ValidationResult result)
    : Exception($"The view is invalid: {result}")
{
    public ValidationResult Result { get; } = result;
}

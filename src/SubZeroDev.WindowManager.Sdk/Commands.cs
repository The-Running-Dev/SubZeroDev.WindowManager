using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Sdk;

public interface ICommands
{
    /// <summary>Registers the handler for a command declared in <c>plugin.json</c>. Throws for an undeclared id.</summary>
    void Register(string commandId, Func<CommandContext, CancellationToken, Task<CommandResult>> handler);

    /// <summary>Asks the host to invoke a command, whichever plugin provides it.</summary>
    Task<SdkResult<CommandResult>> InvokeAsync(string commandId, JsonObject? args, CancellationToken ct);
}

public sealed record CommandContext(JsonObject? Args, InvocationSource Source);

internal sealed class Commands(Connection connection, IReadOnlySet<string> declared) : ICommands
{
    private static readonly TimeSpan InvokeTimeout = TimeSpan.FromSeconds(5);

    private readonly ConcurrentDictionary<string, Func<CommandContext, CancellationToken, Task<CommandResult>>> _handlers =
        new(StringComparer.Ordinal);

    public void Register(string commandId, Func<CommandContext, CancellationToken, Task<CommandResult>> handler)
    {
        ArgumentNullException.ThrowIfNull(handler);
        if (!declared.Contains(commandId))
        {
            throw new ArgumentException($"'{commandId}' is not declared under 'commands' in plugin.json.", nameof(commandId));
        }

        _handlers[commandId] = handler;
    }

    public async Task<SdkResult<CommandResult>> InvokeAsync(string commandId, JsonObject? args, CancellationToken ct)
    {
        if (!connection.IsOpen)
        {
            return new(null, new SdkError(ErrorCode.NotConnected, "The host connection is closed.", false));
        }

        RpcMessage response;
        try
        {
            // The host bounds the provider at 5 s; allow it time to answer with its own Timeout error.
            response = await connection.RequestAsync(Methods.CommandInvoke,
                new CommandInvokeParams(commandId, args, InvocationSource.Plugin), InvokeTimeout * 2, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new(null, new SdkError(ErrorCode.Timeout, $"The host did not answer for {commandId}.", true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(null, new SdkError(ErrorCode.Cancelled, $"{commandId} was cancelled.", false));
        }
        catch (IOException e)
        {
            return new(null, new SdkError(ErrorCode.NotConnected, e.Message, false));
        }

        if (response.Error is { } error)
        {
            var retryable = error.Data is JsonObject data && data["retryable"] is JsonValue v && v.TryGetValue<bool>(out var r) && r;
            return new(null, new SdkError((ErrorCode)error.Code, error.Message, retryable));
        }

        return new(response.ResultAs<CommandResult>() ?? new CommandResult(null), null);
    }

    /// <summary>Runs a <c>command.invoke</c> from the host on the callback queue and answers it.</summary>
    internal async Task ServeAsync(long id, CommandInvokeParams parameters, Action<Exception> logError)
    {
        if (!_handlers.TryGetValue(parameters.CommandId, out var handler))
        {
            connection.Fail(id, WireErrorCodes.CommandNotFound, $"'{parameters.CommandId}' has no handler.",
                new JsonObject { ["retryable"] = false });
            return;
        }

        try
        {
            var result = await handler(new CommandContext(parameters.Args, parameters.Source), CancellationToken.None).ConfigureAwait(false);
            connection.Respond(id, result ?? new CommandResult(null));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logError(exception);
            connection.Fail(id, WireErrorCodes.CommandFailed, exception.Message, new JsonObject { ["retryable"] = false });
        }
    }
}

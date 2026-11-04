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

/// <param name="Args">The command's arguments, if any.</param>
/// <param name="Source">What started the command.</param>
/// <param name="Trigger">Host API 1.1: how the user started the command; null when the host did not say.</param>
public sealed record CommandContext(JsonObject? Args, InvocationSource Source, CommandTrigger? Trigger = null);

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
            return new(null, SdkError.FromWire(error));
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
            var result = await handler(new CommandContext(parameters.Args, parameters.Source, parameters.Trigger), CancellationToken.None).ConfigureAwait(false);
            connection.Respond(id, result ?? new CommandResult(null));
        }
        catch (Exception exception) when (exception is not OutOfMemoryException)
        {
            logError(exception);
            var data = new JsonObject { ["retryable"] = (exception as CommandFailedException)?.Retryable ?? false };
            if (exception is CommandFailedException { Code: { } code })
            {
                data["code"] = code;
            }

            connection.Fail(id, WireErrorCodes.CommandFailed, exception.Message, data);
        }
    }
}

/// <summary>
/// A command handler's failure with a name the caller can act on. It travels as wire code 1009 with the name in
/// <c>data.code</c> (host API 1.1), e.g. <c>ActivationFailed</c>.
/// </summary>
/// <param name="code">The plugin's error name.</param>
/// <param name="message">Words for the user.</param>
/// <param name="retryable">Whether trying again may succeed.</param>
public sealed class CommandFailedException(string code, string message, bool retryable = false) : Exception(message)
{
    public string? Code { get; } = code;

    public bool Retryable { get; } = retryable;
}

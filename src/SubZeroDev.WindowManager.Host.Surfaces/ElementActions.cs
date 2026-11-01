using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>Where a Button or Toggle sends what the user did: a command to the router, an event to the instance's worker.</summary>
public interface IElementActions
{
    Task<Outcome<CommandResult>> InvokeCommandAsync(string commandId, JsonObject? args, InvocationSource source);

    Task<Outcome> SendEventAsync(Guid instanceId, string elementId, ElementEventKind kind, JsonNode? value, InvocationSource source,
        string? itemKey = null);
}

public sealed class ElementActions(ICommandRouter router, IContributionHost contributions) : IElementActions
{
    public Task<Outcome<CommandResult>> InvokeCommandAsync(string commandId, JsonObject? args, InvocationSource source) =>
        router.InvokeAsync(commandId, args, source, null, CancellationToken.None);

    public Task<Outcome> SendEventAsync(Guid instanceId, string elementId, ElementEventKind kind, JsonNode? value, InvocationSource source,
        string? itemKey = null) =>
        contributions.SendEventAsync(instanceId, elementId, kind, value, source, itemKey);
}

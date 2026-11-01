using System.Collections.Concurrent;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Sdk;

public interface IPopups
{
    /// <summary>The surface of a popup declared under <c>popups</c> in plugin.json. Throws for an undeclared id.</summary>
    PopupSurface Get(string popupId);
}

/// <summary>A popup the plugin owns. Its view is kept while it is closed, so it can be set before opening.</summary>
public sealed class PopupSurface : Surface
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(5);

    private readonly Connection _connection;

    internal PopupSurface(Connection connection, string popupId)
        : base(connection, "popup:" + popupId)
    {
        _connection = connection;
        PopupId = popupId;
    }

    public string PopupId { get; }

    /// <summary>Asks the host to show the popup at the anchor; it answers once the window is shown.</summary>
    public Task<SdkResult> OpenAsync(PopupAnchor anchor, CancellationToken ct) =>
        RequestAsync(Methods.PopupOpen, new PopupOpenParams(PopupId, anchor), ct);

    public Task<SdkResult> CloseAsync(CancellationToken ct) =>
        RequestAsync(Methods.PopupClose, new PopupCloseParams(PopupId), ct);

    private async Task<SdkResult> RequestAsync(string method, object parameters, CancellationToken ct)
    {
        if (!_connection.IsOpen)
        {
            return new(new SdkError(ErrorCode.NotConnected, "The host connection is closed.", false));
        }

        RpcMessage response;
        try
        {
            response = await _connection.RequestAsync(method, parameters, RequestTimeout, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new(new SdkError(ErrorCode.Timeout, $"The host did not answer {method} for {PopupId}.", true));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            return new(new SdkError(ErrorCode.Cancelled, $"{method} for {PopupId} was cancelled.", false));
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

internal sealed class Popups(Connection connection, IReadOnlySet<string> declared) : IPopups
{
    private readonly ConcurrentDictionary<string, PopupSurface> _surfaces = new(StringComparer.Ordinal);

    public PopupSurface Get(string popupId)
    {
        if (!declared.Contains(popupId))
        {
            throw new ArgumentException($"{WireErrorCodes.Undeclared} '{popupId}' is not declared under 'popups' in plugin.json.", nameof(popupId));
        }

        return _surfaces.GetOrAdd(popupId, id => new PopupSurface(connection, id));
    }

    /// <summary>The surface for an id the host names, or null when the plugin does not declare it.</summary>
    internal PopupSurface? Find(string popupId) => declared.Contains(popupId) ? Get(popupId) : null;
}

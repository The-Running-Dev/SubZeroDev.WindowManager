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
    internal PopupSurface(Connection connection, string popupId)
        : base(connection, "popup:" + popupId)
    {
        PopupId = popupId;
    }

    public string PopupId { get; }

    /// <summary>Asks the host to show the popup at the anchor; it answers once the window is shown.</summary>
    public Task<SdkResult> OpenAsync(PopupAnchor anchor, CancellationToken ct) =>
        HostRequests.SendAsync(Connection, Methods.PopupOpen, new PopupOpenParams(PopupId, anchor), PopupId, ct);

    public Task<SdkResult> CloseAsync(CancellationToken ct) =>
        HostRequests.SendAsync(Connection, Methods.PopupClose, new PopupCloseParams(PopupId), PopupId, ct);
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

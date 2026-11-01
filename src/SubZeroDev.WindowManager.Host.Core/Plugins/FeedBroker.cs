using System.Text.Json;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

public interface IFeedBroker
{
    /// <summary>The last update of a feed, or null before its first publish.</summary>
    FeedUpdateParams? Latest(string feedId);

    /// <summary>Raised off the UI thread after each publish, and when a provider stops being available.</summary>
    event EventHandler<FeedUpdateParams> Updated;
}

/// <summary>
/// Keeps the latest snapshot of each feed and sends it to the running plugins that subscribe to it. A
/// publish is accepted only for a feed the publisher declares (1007 otherwise). A subscriber that
/// becomes Running gets the latest update of each feed it subscribes to. When the provider leaves
/// Running, subscribers get <c>available: false</c> with the last snapshot kept.
/// </summary>
public sealed class FeedBroker : IFeedBroker
{
    private readonly IPluginSupervisor _supervisor;
    private readonly IPackageCatalog _catalog;
    private readonly IPluginLogs _pluginLogs;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, (string Provider, FeedUpdateParams Update)> _feeds = new(StringComparer.Ordinal);

    public FeedBroker(IPluginSupervisor supervisor, IPackageCatalog catalog, IPluginLogs pluginLogs)
    {
        _supervisor = supervisor;
        _catalog = catalog;
        _pluginLogs = pluginLogs;
        _supervisor.StatusChanged += (_, status) => OnStatusChanged(status);
    }

    public event EventHandler<FeedUpdateParams>? Updated;

    public FeedUpdateParams? Latest(string feedId)
    {
        lock (_gate)
        {
            return _feeds.TryGetValue(feedId, out var feed) ? feed.Update : null;
        }
    }

    /// <summary>The feeds published so far and their providers, for diagnostics.</summary>
    public IReadOnlyList<(string FeedId, string Provider, bool Available)> Feeds
    {
        get
        {
            lock (_gate)
            {
                return [.. _feeds.OrderBy(feed => feed.Key, StringComparer.Ordinal)
                    .Select(feed => (feed.Key, feed.Value.Provider, feed.Value.Update.Available))];
            }
        }
    }

    /// <summary>Handles a worker's notification; anything but <c>feed.publish</c> is ignored.</summary>
    public void OnNotification(string pluginId, WorkerNotification notification)
    {
        if (notification.Method != Methods.FeedPublish)
        {
            return;
        }

        FeedPublishParams? publish;
        try
        {
            publish = notification.Params.Deserialize<FeedPublishParams>(ProtocolJson.Options);
        }
        catch (JsonException e)
        {
            _pluginLogs.Write(pluginId, "host", $"{WireErrorCodes.InvalidParams} Unreadable feed.publish: {e.Message}");
            return;
        }

        if (publish is null)
        {
            return;
        }

        Publish(pluginId, publish);
    }

    public void Publish(string pluginId, FeedPublishParams publish)
    {
        ArgumentNullException.ThrowIfNull(publish);
        if (_catalog.GetManifest(pluginId) is not { } manifest || !manifest.Feeds.Contains(publish.FeedId, StringComparer.Ordinal))
        {
            _pluginLogs.Write(pluginId, "host", $"{WireErrorCodes.Undeclared} '{publish.FeedId}' is not declared under 'feeds'.");
            return;
        }

        var update = new FeedUpdateParams(publish.FeedId, true, publish.Snapshot);
        lock (_gate)
        {
            if (_feeds.TryGetValue(publish.FeedId, out var current) && current.Provider != pluginId && current.Update.Available)
            {
                _pluginLogs.Write(pluginId, "host", $"'{publish.FeedId}' is already provided by {current.Provider}; the publish is ignored.");
                return;
            }

            _feeds[publish.FeedId] = (pluginId, update);
            Send(update);
        }

        Updated?.Invoke(this, update);
    }

    private void OnStatusChanged(PluginStatus status)
    {
        if (status.State == PluginState.Running)
        {
            SendLatest(status.PluginId);
            return;
        }

        var gone = new List<FeedUpdateParams>();
        lock (_gate)
        {
            foreach (var (feedId, feed) in _feeds.ToList())
            {
                if (feed.Provider == status.PluginId && feed.Update.Available)
                {
                    var update = feed.Update with { Available = false };
                    _feeds[feedId] = (feed.Provider, update);
                    Send(update);
                    gone.Add(update);
                }
            }
        }

        foreach (var update in gone)
        {
            Updated?.Invoke(this, update);
        }
    }

    /// <summary>Gives a plugin that has just started the latest update of each feed it subscribes to.</summary>
    private void SendLatest(string pluginId)
    {
        if (_catalog.GetManifest(pluginId) is not { } manifest || _supervisor.GetChannel(pluginId) is not { } channel)
        {
            return;
        }

        lock (_gate)
        {
            foreach (var feedId in manifest.Subscribes)
            {
                if (_feeds.TryGetValue(feedId, out var feed))
                {
                    channel.Notify(Methods.FeedUpdate, feed.Update);
                }
            }
        }
    }

    /// <summary>Sends to every running subscriber; called under the gate so updates keep their order.</summary>
    private void Send(FeedUpdateParams update)
    {
        foreach (var status in _supervisor.All)
        {
            if (status.State == PluginState.Running
                && _catalog.GetManifest(status.PluginId) is { } manifest
                && manifest.Subscribes.Contains(update.FeedId, StringComparer.Ordinal)
                && _supervisor.GetChannel(status.PluginId) is { } channel)
            {
                channel.Notify(Methods.FeedUpdate, update);
            }
        }
    }
}

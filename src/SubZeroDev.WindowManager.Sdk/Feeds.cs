using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Sdk;

public interface IFeeds
{
    /// <summary>The publisher for a feed declared under <c>feeds</c> in plugin.json. Throws for an undeclared id.</summary>
    IFeedPublisher Publisher(string feedId);

    /// <summary>
    /// Calls <paramref name="onUpdate"/> on the callback queue for each update of a feed declared under
    /// <c>subscribes</c>, starting with the latest one when there is one. Throws for an undeclared id.
    /// </summary>
    IDisposable Subscribe(string feedId, Func<FeedUpdate, CancellationToken, Task> onUpdate);
}

public interface IFeedPublisher
{
    /// <summary>Sends the whole snapshot. The host keeps only the latest one. Safe from any thread.</summary>
    void Publish(JsonNode snapshot);
}

public sealed record FeedUpdate(bool Available, JsonNode? Snapshot);

internal sealed class Feeds(Connection connection, IReadOnlySet<string> published, IReadOnlySet<string> subscribed,
    Action<Func<Task>> enqueue, Action<Exception, string> logError) : IFeeds
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, FeedUpdate> _latest = new(StringComparer.Ordinal);
    private readonly List<Subscription> _subscriptions = [];

    public IFeedPublisher Publisher(string feedId)
    {
        if (!published.Contains(feedId))
        {
            throw new ArgumentException($"{WireErrorCodes.Undeclared} '{feedId}' is not declared under 'feeds' in plugin.json.", nameof(feedId));
        }

        return new FeedPublisher(connection, feedId);
    }

    public IDisposable Subscribe(string feedId, Func<FeedUpdate, CancellationToken, Task> onUpdate)
    {
        ArgumentNullException.ThrowIfNull(onUpdate);
        if (!subscribed.Contains(feedId))
        {
            throw new ArgumentException($"{WireErrorCodes.Undeclared} '{feedId}' is not declared under 'subscribes' in plugin.json.", nameof(feedId));
        }

        var subscription = new Subscription(this, feedId, onUpdate);
        lock (_gate)
        {
            _subscriptions.Add(subscription);
            if (_latest.TryGetValue(feedId, out var latest))
            {
                enqueue(() => subscription.DeliverAsync(latest, logError));
            }
        }

        return subscription;
    }

    /// <summary>Runs a <c>feed.update</c> from the host; called on the callback queue.</summary>
    internal async Task DeliverAsync(FeedUpdateParams parameters)
    {
        var update = new FeedUpdate(parameters.Available, parameters.Snapshot);
        List<Subscription> targets;
        lock (_gate)
        {
            _latest[parameters.FeedId] = update;
            targets = [.. _subscriptions.Where(subscription => subscription.FeedId == parameters.FeedId)];
        }

        foreach (var subscription in targets)
        {
            await subscription.DeliverAsync(update, logError).ConfigureAwait(false);
        }
    }

    private void Remove(Subscription subscription)
    {
        lock (_gate)
        {
            _subscriptions.Remove(subscription);
        }
    }

    private sealed class FeedPublisher(Connection connection, string feedId) : IFeedPublisher
    {
        public void Publish(JsonNode snapshot)
        {
            ArgumentNullException.ThrowIfNull(snapshot);
            connection.Notify(Methods.FeedPublish, new FeedPublishParams(feedId, snapshot.DeepClone()));
        }
    }

    private sealed class Subscription(Feeds owner, string feedId, Func<FeedUpdate, CancellationToken, Task> onUpdate) : IDisposable
    {
        private volatile bool _disposed;

        public string FeedId { get; } = feedId;

        public async Task DeliverAsync(FeedUpdate update, Action<Exception, string> logError)
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                await onUpdate(update, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception) when (exception is not OutOfMemoryException)
            {
                logError(exception, $"The {FeedId} subscriber failed.");
            }
        }

        public void Dispose()
        {
            _disposed = true;
            owner.Remove(this);
        }
    }
}

using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S7: the feed broker between a publishing plugin and its subscribers.</summary>
public sealed class FeedTests
{
    private const string Feed = "windows.running";
    private readonly FakeCatalog _catalog = new();
    private readonly FakeSupervisor _supervisor = new();
    private readonly FakeLogs _logs = new();

    [Fact]
    public void S7_7_a_publish_of_an_undeclared_feed_is_dropped_with_1007()
    {
        _catalog.Add("samples.provider", feeds: "[]");
        _catalog.Add("samples.subscriber", subscribes: $"[\"{Feed}\"]");
        _supervisor.Run("samples.provider");
        var subscriber = _supervisor.Run("samples.subscriber");
        var broker = new FeedBroker(_supervisor, _catalog, _logs);

        broker.OnNotification("samples.provider", Publish(Feed, new JsonArray()));

        Assert.Empty(subscriber.Updates);
        Assert.Null(broker.Latest(Feed));
        Assert.Contains(_logs.Lines, line => line.StartsWith("samples.provider host 1007", StringComparison.Ordinal));
    }

    [Fact]
    public void S7_7_running_subscribers_get_each_update_and_others_do_not()
    {
        _catalog.Add("samples.provider", feeds: $"[{{\"id\":\"{Feed}\"}}]");
        _catalog.Add("samples.subscriber", subscribes: $"[\"{Feed}\"]");
        _catalog.Add("samples.bystander");
        _supervisor.Run("samples.provider");
        var subscriber = _supervisor.Run("samples.subscriber");
        var bystander = _supervisor.Run("samples.bystander");
        var broker = new FeedBroker(_supervisor, _catalog, _logs);
        var raised = new List<FeedUpdateParams>();
        broker.Updated += (_, update) => raised.Add(update);

        broker.OnNotification("samples.provider", Publish(Feed, new JsonArray(new JsonObject { ["key"] = "1" })));
        broker.OnNotification("samples.provider", Publish(Feed, new JsonArray()));

        Assert.Equal(["[{\"key\":\"1\"}]", "[]"], subscriber.Updates.Select(update => update.Snapshot!.ToJsonString()));
        Assert.All(subscriber.Updates, update => Assert.True(update.Available));
        Assert.Empty(bystander.Updates);
        Assert.Equal(2, raised.Count);
        Assert.Equal("[]", broker.Latest(Feed)!.Snapshot!.ToJsonString());
    }

    [Fact]
    public void S7_7_a_subscriber_that_starts_later_gets_the_latest_update()
    {
        _catalog.Add("samples.provider", feeds: $"[{{\"id\":\"{Feed}\"}}]");
        _catalog.Add("samples.subscriber", subscribes: $"[\"{Feed}\"]");
        _supervisor.Run("samples.provider");
        var broker = new FeedBroker(_supervisor, _catalog, _logs);
        broker.OnNotification("samples.provider", Publish(Feed, new JsonArray(new JsonObject { ["key"] = "1" })));

        var subscriber = _supervisor.Run("samples.subscriber");

        var update = Assert.Single(subscriber.Updates);
        Assert.True(update.Available);
        Assert.Equal("[{\"key\":\"1\"}]", update.Snapshot!.ToJsonString());
    }

    [Theory]
    [InlineData(PluginState.Faulted)]
    [InlineData(PluginState.Stopped)]
    public void S7_7_when_the_provider_stops_running_subscribers_get_available_false_with_the_last_snapshot(PluginState state)
    {
        _catalog.Add("samples.provider", feeds: $"[{{\"id\":\"{Feed}\"}}]");
        _catalog.Add("samples.subscriber", subscribes: $"[\"{Feed}\"]");
        _supervisor.Run("samples.provider");
        var subscriber = _supervisor.Run("samples.subscriber");
        var broker = new FeedBroker(_supervisor, _catalog, _logs);
        broker.OnNotification("samples.provider", Publish(Feed, new JsonArray(new JsonObject { ["key"] = "1" })));

        _supervisor.Set("samples.provider", state);

        Assert.Equal([true, false], subscriber.Updates.Select(update => update.Available));
        Assert.Equal("[{\"key\":\"1\"}]", subscriber.Updates[^1].Snapshot!.ToJsonString());
        Assert.False(broker.Latest(Feed)!.Available);
    }

    [Fact]
    public void S7_7_a_second_provider_is_ignored_while_the_first_is_available()
    {
        _catalog.Add("samples.provider", feeds: $"[{{\"id\":\"{Feed}\"}}]");
        _catalog.Add("samples.other", feeds: $"[{{\"id\":\"{Feed}\"}}]");
        _supervisor.Run("samples.provider");
        _supervisor.Run("samples.other");
        var broker = new FeedBroker(_supervisor, _catalog, _logs);

        broker.OnNotification("samples.provider", Publish(Feed, new JsonArray(new JsonObject { ["key"] = "first" })));
        broker.OnNotification("samples.other", Publish(Feed, new JsonArray(new JsonObject { ["key"] = "second" })));

        Assert.Equal("[{\"key\":\"first\"}]", broker.Latest(Feed)!.Snapshot!.ToJsonString());
        Assert.Equal(("samples.provider", true), broker.Feeds.Select(feed => (feed.Provider, feed.Available)).Single());
    }

    [Theory]
    [InlineData("\"windows.running\"")]
    [InlineData("[1]")]
    [InlineData("[\"Not An Id\"]")]
    public void S7_7_subscribes_must_be_a_list_of_feed_ids(string subscribes)
    {
        var problems = new List<string>();

        var manifest = PluginManifestReader.Parse(FakeCatalog.Document("samples.subscriber", "[]", subscribes), problems);

        Assert.Null(manifest);
        Assert.Contains(problems, problem => problem.Contains("subscribes", StringComparison.Ordinal));
    }

    private static WorkerNotification Publish(string feedId, JsonNode snapshot) =>
        new(Methods.FeedPublish, JsonSerializer.SerializeToElement(new FeedPublishParams(feedId, snapshot), ProtocolJson.Options));

    private sealed class FakeLogs : IPluginLogs
    {
        public ConcurrentQueue<string> Lines { get; } = new();

        public void Write(string pluginId, string level, string message) => Lines.Enqueue($"{pluginId} {level} {message}");
    }

    private sealed class FakeCatalog : IPackageCatalog
    {
        private readonly Dictionary<string, PluginManifest> _manifests = new(StringComparer.Ordinal);

#pragma warning disable CS0067
        public event EventHandler<CatalogChangedEventArgs>? Changed;
#pragma warning restore CS0067

        public static JsonObject Document(string id, string feeds, string subscribes) => JsonNode.Parse($$"""
            {"manifestVersion":1,"id":"{{id}}","version":"1.0.0","displayName":"P","publisher":"T","description":"D",
             "hostApi":"^1.0","entry":{"path":"p.exe"},"modes":["coexistence"],"contributions":[],
             "feeds":{{feeds}},"subscribes":{{subscribes}}}
            """)!.AsObject();

        public void Add(string id, string feeds = "[]", string subscribes = "[]")
        {
            var problems = new List<string>();
            var manifest = PluginManifestReader.Parse(Document(id, feeds, subscribes), problems);
            Assert.True(manifest is not null, string.Join("; ", problems));
            _manifests[id] = manifest;
        }

        public IReadOnlyList<PackageRecord> All(PackageKind kind) => [];

        public PluginManifest? GetManifest(string pluginId) => _manifests.GetValueOrDefault(pluginId);

        public Task<Outcome<PackageRecord>> InstallAsync(PackageKind kind, string zipOrFolderPath, CancellationToken ct) =>
            throw new NotSupportedException();

        public Task<Outcome> RemoveAsync(PackageKind kind, string id, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class FakeSupervisor : IPluginSupervisor
    {
        private readonly Dictionary<string, PluginState> _states = new(StringComparer.Ordinal);
        private readonly Dictionary<string, FakeChannel> _channels = new(StringComparer.Ordinal);

        public IReadOnlyList<PluginStatus> All => [.. _states.Keys.Select(GetStatus)];

        public event EventHandler<PluginStatus>? StatusChanged;

        public FakeChannel Run(string pluginId)
        {
            var channel = new FakeChannel(pluginId);
            _channels[pluginId] = channel;
            Set(pluginId, PluginState.Running);
            return channel;
        }

        public void Set(string pluginId, PluginState state)
        {
            _states[pluginId] = state;
            StatusChanged?.Invoke(this, GetStatus(pluginId));
        }

        public PluginStatus GetStatus(string pluginId) =>
            new(pluginId, _states.GetValueOrDefault(pluginId, PluginState.Discovered), null, null, [], []);

        public Task<Outcome> EnsureRunningAsync(string pluginId, CancellationToken ct) => Task.FromResult(Outcome.Success);

        public Task StopAsync(string pluginId, CancellationToken ct) => Task.CompletedTask;

        public Task<Outcome> RetryAsync(string pluginId, CancellationToken ct) => Task.FromResult(Outcome.Success);

        public Task StopAllAsync(CancellationToken ct) => Task.CompletedTask;

        public IWorkerChannel? GetChannel(string pluginId) =>
            _states.GetValueOrDefault(pluginId) == PluginState.Running ? _channels.GetValueOrDefault(pluginId) : null;
    }

    private sealed class FakeChannel(string pluginId) : IWorkerChannel
    {
        public string PluginId => pluginId;

        public int ProcessId => 1;

        public List<FeedUpdateParams> Updates { get; } = [];

#pragma warning disable CS0067
        public event EventHandler<WorkerNotification>? NotificationReceived;
#pragma warning restore CS0067

        public Task<Outcome<TResult>> RequestAsync<TParams, TResult>(string method, TParams args, TimeSpan timeout, CancellationToken ct) =>
            throw new NotSupportedException();

        public Outcome Notify<TParams>(string method, TParams args)
        {
            Assert.Equal(Methods.FeedUpdate, method);
            Updates.Add((args as FeedUpdateParams)!);
            return Outcome.Success;
        }
    }
}

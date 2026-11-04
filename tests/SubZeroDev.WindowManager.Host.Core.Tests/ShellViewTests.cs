using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S25 from the plugin side: the SDK reads <c>host.windows</c> and <c>host.desktops</c> as one view (S25.8, S25.9).</summary>
public sealed class ShellViewTests
{
    private const string Desktop = "00000000-0000-0000-0000-000000000001";

    [Fact]
    public void S25_8_equal_revisions_form_a_view_at_once_and_a_lagging_feed_holds_it_back()
    {
        using var world = new World();

        world.Send(ShellFeedIds.Windows, Windows(5, Item("wk1:1:1", 1)));
        Assert.Null(world.Source.Current);

        world.Send(ShellFeedIds.Desktops, Desktops(4));
        Assert.Null(world.Source.Current);
        Assert.Empty(world.Views);

        world.Send(ShellFeedIds.Desktops, Desktops(5));
        var view = Assert.Single(world.Views);
        Assert.Equal(5, view.Revision);
        Assert.False(view.Stale);
    }

    [Fact]
    public async Task S25_8_a_feed_that_still_lags_after_the_hold_gives_a_stale_view_from_the_lower_revision()
    {
        using var world = new World();
        world.Send(ShellFeedIds.Windows, Windows(5, Item("wk1:1:1", 1)));
        world.Send(ShellFeedIds.Desktops, Desktops(4));

        await world.ReleaseHoldAsync();

        var view = Assert.Single(world.Views);
        Assert.Equal(4, view.Revision);
        Assert.True(view.Stale);
        Assert.Equal([ShellViewSource.DefaultHold], world.Waited);
    }

    [Fact]
    public async Task S25_8_a_feed_that_caught_up_during_the_hold_is_read_again_and_the_view_is_not_stale()
    {
        using var world = new World();
        world.Send(ShellFeedIds.Windows, Windows(5, Item("wk1:1:1", 1)));
        world.Send(ShellFeedIds.Desktops, Desktops(4));
        // The update is stored by the feed but its callback has not run yet.
        world.Store(ShellFeedIds.Desktops, Desktops(5));

        await world.ReleaseHoldAsync();

        var view = Assert.Single(world.Views);
        Assert.Equal(5, view.Revision);
        Assert.False(view.Stale);
    }

    [Fact]
    public async Task S25_8_an_update_that_resolves_the_lag_cancels_the_hold()
    {
        using var world = new World();
        world.Send(ShellFeedIds.Windows, Windows(5, Item("wk1:1:1", 1)));
        world.Send(ShellFeedIds.Desktops, Desktops(4));
        world.Send(ShellFeedIds.Desktops, Desktops(5));

        await world.ReleaseHoldAsync();

        var view = Assert.Single(world.Views);
        Assert.Equal(5, view.Revision);
        Assert.False(view.Stale);
        Assert.True(world.HoldCancelled);
    }

    [Fact]
    public void S25_6_an_unavailable_feed_raises_availability_and_forms_no_view_until_it_returns()
    {
        using var world = new World();
        var availability = new List<bool>();
        world.Source.AvailabilityChanged += availability.Add;
        world.Send(ShellFeedIds.Windows, Windows(1));
        world.Send(ShellFeedIds.Desktops, Desktops(1));
        Assert.Single(world.Views);

        world.SendUnavailable(ShellFeedIds.Windows);
        Assert.False(world.Source.Available);
        Assert.Single(world.Views);

        world.Send(ShellFeedIds.Windows, Windows(2));
        world.Send(ShellFeedIds.Desktops, Desktops(2));

        Assert.True(world.Source.Available);
        Assert.Equal([false, true], availability);
        Assert.Equal(2, world.Views.Count);
        Assert.Equal(2, world.Views[^1].Revision);
    }

    [Fact]
    public void S25_5_the_running_windows_follow_the_strip_rule_the_desktop_and_first_seen_order()
    {
        using var world = new World();
        world.Send(ShellFeedIds.Windows, Windows(1,
            Item("wk1:1:3", 3, firstSeen: 30),
            Item("wk1:1:1", 1, firstSeen: 10),
            Item("wk1:1:2", 2, firstSeen: 20, desktop: "other"),
            Item("wk1:1:4", 4, firstSeen: 5, desktop: "other", pinned: true),
            Item("wk1:1:5", 5, firstSeen: 6, tool: true),
            Item("wk1:1:6", 6, firstSeen: 7, cloak: "shell"),
            Item("wk1:1:7", 7, firstSeen: 8, title: ""),
            Item("wk1:1:8", 8, firstSeen: 9, tool: true, appWindow: true),
            Item("wk1:1:9", 9, firstSeen: 11, owner: "wk1:1:1"),
            Item("wk1:1:10", 10, firstSeen: 12, minimized: true)));
        world.Send(ShellFeedIds.Desktops, Desktops(1));

        var running = world.Views.Single().RunningWindows();

        Assert.Equal(["wk1:1:4", "wk1:1:8", "wk1:1:1", "wk1:1:10", "wk1:1:3"], running.Select(window => window.WindowKey));
    }

    [Fact]
    public void S25_9_a_host_error_name_reaches_the_plugin_beside_the_wire_class()
    {
        var error = new RpcError(WireErrorCodes.CommandFailed, "The window reference is from another run of the shell.",
            new JsonObject { ["retryable"] = false, ["code"] = "StaleWindowReference" });

        var sdk = SdkError.FromWire(error);

        Assert.Equal(ErrorCode.CommandFailed, sdk.Code);
        Assert.Equal("StaleWindowReference", sdk.Name);
        Assert.False(sdk.Retryable);
        Assert.Null(SdkError.FromWire(new RpcError(WireErrorCodes.Timeout, "slow")).Name);
    }

    private static JsonObject Windows(long revision, params JsonObject[] items) =>
        new() { ["revision"] = revision, ["windows"] = new JsonArray([.. items]) };

    private static JsonObject Desktops(long revision) => new()
    {
        ["revision"] = revision,
        ["current"] = Desktop,
        ["busy"] = false,
        ["desktops"] = new JsonArray(new JsonObject { ["id"] = Desktop, ["windowCount"] = 1, ["hasAttention"] = false }),
    };

    private static JsonObject Item(string key, long hwnd, long firstSeen = 1, string desktop = Desktop, bool pinned = false,
        bool tool = false, bool appWindow = false, string cloak = "none", string title = "A window", string? owner = null,
        bool minimized = false)
    {
        var item = new JsonObject
        {
            ["windowKey"] = key,
            ["hwnd"] = hwnd,
            ["processId"] = 1,
            ["title"] = title,
            ["groupKey"] = "g",
            ["monitorId"] = "m",
            ["desktopId"] = desktop,
            ["pinnedToAll"] = pinned,
            ["visible"] = true,
            ["cloak"] = cloak,
            ["shellHidden"] = false,
            ["toolWindow"] = tool,
            ["noActivate"] = false,
            ["appWindow"] = appWindow,
            ["titled"] = title.Length > 0,
            ["firstSeen"] = firstSeen,
            ["activatedAt"] = 0,
            ["active"] = false,
            ["minimized"] = minimized,
            ["attention"] = false,
        };
        if (owner is not null)
        {
            item["ownerKey"] = owner;
        }

        return item;
    }

    private sealed class World : IDisposable
    {
        private readonly FakeFeeds _feeds = new();
        private TaskCompletionSource? _hold;
        private CancellationToken _holdToken;

        public World()
        {
            Source = new ShellViewSource(_feeds, delay: (span, token) =>
            {
                Waited.Add(span);
                _holdToken = token;
                _hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                token.Register(() => _hold.TrySetCanceled(token));
                return _hold.Task;
            });
            Source.Changed += Views.Add;
        }

        public ShellViewSource Source { get; }

        public List<ShellView> Views { get; } = [];

        public List<TimeSpan> Waited { get; } = [];

        public bool HoldCancelled => _holdToken.IsCancellationRequested;

        public void Send(string feed, JsonNode snapshot)
        {
            Store(feed, snapshot);
            _feeds.Deliver(feed, new FeedUpdate(true, snapshot));
        }

        public void SendUnavailable(string feed) => _feeds.Deliver(feed, new FeedUpdate(false, null));

        /// <summary>Records the latest update without calling the subscriber, as when a callback is still queued.</summary>
        public void Store(string feed, JsonNode snapshot) => _feeds.Latests[feed] = new FeedUpdate(true, snapshot);

        public async Task ReleaseHoldAsync()
        {
            _hold?.TrySetResult();
            // The hold continues on the thread pool; give it a moment, bounded.
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            while (!timeout.IsCancellationRequested && Views.Count == 0 && !HoldCancelled)
            {
                await Task.Delay(10, CancellationToken.None);
            }

            await Task.Delay(50, CancellationToken.None);
        }

        public void Dispose() => Source.Dispose();
    }

    private sealed class FakeFeeds : IFeeds
    {
        private readonly Dictionary<string, Func<FeedUpdate, CancellationToken, Task>> _subscribers = new(StringComparer.Ordinal);

        public Dictionary<string, FeedUpdate> Latests { get; } = new(StringComparer.Ordinal);

        public IFeedPublisher Publisher(string feedId) => throw new NotSupportedException();

        public IDisposable Subscribe(string feedId, Func<FeedUpdate, CancellationToken, Task> onUpdate)
        {
            _subscribers[feedId] = onUpdate;
            return new Nothing();
        }

        public FeedUpdate? Latest(string feedId) => Latests.GetValueOrDefault(feedId);

        public void Deliver(string feedId, FeedUpdate update) => _subscribers[feedId](update, CancellationToken.None).GetAwaiter().GetResult();

        private sealed class Nothing : IDisposable
        {
            public void Dispose()
            {
            }
        }
    }
}

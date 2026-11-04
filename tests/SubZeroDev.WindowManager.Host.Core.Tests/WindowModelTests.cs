using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S25: the shared window model, its identity, its feeds and the window commands. All windows are fakes (I38).</summary>
public sealed class WindowModelTests
{
    private const long Epoch = 777;

    [Fact]
    public void S25_1_a_window_is_admitted_once_visible_and_titled_and_hiding_it_does_not_remove_it()
    {
        var world = new World();
        world.Put(10, title: "");
        world.Put(11, title: "Hidden at first", visible: false);
        world.Put(12, title: "Normal");
        world.Model.Observe(new(10, ObservationKind.Created));
        world.Model.Observe(new(11, ObservationKind.Created));
        world.Model.Observe(new(12, ObservationKind.Created));
        world.Poster.Drain();

        Assert.Equal([12], world.Handles());

        world.Put(11, title: "Hidden at first", visible: true);
        world.Model.Observe(new(11, ObservationKind.Shown));
        world.Poster.Drain();
        Assert.Equal([11, 12], world.Handles().Order());

        world.Put(11, title: "Hidden at first", visible: false);
        world.Model.Observe(new(11, ObservationKind.Hidden));
        world.Put(12, title: "Normal", minimised: true);
        world.Model.Observe(new(12, ObservationKind.Minimised));
        world.Put(10, title: "Cloaked now", cloak: CloakState.Shell);
        world.Model.Observe(new(10, ObservationKind.Cloaked));
        world.Poster.Drain();

        var snapshot = world.Model.Current;
        Assert.Equal([10, 11, 12], world.Handles().Order());
        Assert.False(snapshot.Windows.Single(w => w.Key.Handle == 11).Visible);
        Assert.True(snapshot.Windows.Single(w => w.Key.Handle == 12).Minimised);
        Assert.Equal(CloakState.Shell, snapshot.Windows.Single(w => w.Key.Handle == 10).Cloak);

        world.Model.Observe(new(12, ObservationKind.Destroyed));
        world.Poster.Drain();
        Assert.Equal([10, 11], world.Handles().Order());
    }

    [Fact]
    public void S25_1_a_window_whose_handle_stops_being_a_window_is_dropped_at_the_next_scan()
    {
        var world = new World();
        world.Put(20, title: "Gone soon");
        world.Model.Observe(new(20, ObservationKind.Created));
        world.Poster.Drain();
        world.Facts.Remove(20);

        world.Model.Observe(new(0, ObservationKind.Rescan));
        world.Poster.Drain();

        Assert.Empty(world.Model.Current.Windows);
    }

    [Fact]
    public void S25_1_never_admitted_windows_cost_no_revision()
    {
        var world = new World();
        world.Put(30, title: "");
        world.Model.Observe(new(30, ObservationKind.Created));
        world.Poster.Drain();

        Assert.Equal(0, world.Model.Current.Revision);
        Assert.Empty(world.Changes);
    }

    [Fact]
    public void S25_2_a_reused_handle_gets_a_new_generation_and_the_old_key_is_gone()
    {
        var world = new World();
        world.Put(40, title: "First", process: 100);
        world.Model.Observe(new(40, ObservationKind.Created));
        world.Poster.Drain();
        var oldKey = world.KeyOf(40);

        world.Put(40, title: "Second", process: 200);
        world.Model.Observe(new(0, ObservationKind.Rescan));
        world.Poster.Drain();
        var newKey = world.KeyOf(40);

        Assert.NotEqual(oldKey, newKey);
        Assert.Equal(CoreErrorCode.WindowGone, world.Model.Resolve(Ref(oldKey)).Error!.Code);
        Assert.Equal(CoreErrorCode.StaleWindowReference, world.Model.Resolve(new WindowReference(Epoch + 1, newKey.Generation).ToString()).Error!.Code);
        Assert.Equal((nint)40, world.Model.Resolve(Ref(newKey)).Value!.Handle);
        Assert.Equal("Second", world.Model.ResolveHandle(40).Value!.Managed!.Title);
    }

    [Fact]
    public void S25_2_handle_reuse_churn_never_resolves_an_old_key_to_a_new_window()
    {
        var world = new World();
        var kept = new List<WindowKey>();
        for (var i = 0; i < 500; i++)
        {
            world.Put(50 + (i % 5), title: "Window " + i, process: 1000 + i);
            world.Model.Observe(new(50 + (i % 5), ObservationKind.Created));
            world.Model.Observe(new(0, ObservationKind.Rescan));
            world.Poster.Drain();
            kept.Add(world.KeyOf(50 + (i % 5)));
        }

        var live = world.Model.Current.Windows.Select(w => w.Key).ToHashSet();
        var wrong = 0;
        foreach (var key in kept)
        {
            var resolved = world.Model.Resolve(Ref(key));
            if (live.Contains(key))
            {
                Assert.True(resolved.Ok);
            }
            else if (resolved.Ok || resolved.Error!.Code != CoreErrorCode.WindowGone)
            {
                wrong++;
            }
        }

        Assert.Equal(0, wrong);
        Assert.Equal(500, kept.Select(k => k.Generation).Distinct().Count());
    }

    [Fact]
    public void S25_2_a_resolve_that_finds_a_replaced_window_drops_the_entry_without_touching_the_new_window()
    {
        var world = new World();
        world.Put(60, title: "Old", process: 1);
        world.Model.Observe(new(60, ObservationKind.Created));
        world.Poster.Drain();
        var key = world.KeyOf(60);
        world.Put(60, title: "New", process: 2);

        var resolved = world.Model.Resolve(Ref(key));
        world.Poster.Drain();

        Assert.Equal(CoreErrorCode.WindowGone, resolved.Error!.Code);
        Assert.Empty(world.Model.Current.Windows);
        Assert.Equal((nint)60, world.Model.ResolveHandle(60).Value!.Handle);
        Assert.Null(world.Model.ResolveHandle(60).Value!.Managed);
    }

    [Theory]
    [InlineData("wk1:5:9", true, 5L, 9L)]
    [InlineData("wk1:5", false, 0L, 0L)]
    [InlineData("wk2:5:9", false, 0L, 0L)]
    [InlineData("wk1:a:9", false, 0L, 0L)]
    [InlineData("wk1:-5:9", false, 0L, 0L)]
    [InlineData("12345", false, 0L, 0L)]
    public void S25_2_references_round_trip_and_malformed_text_is_rejected(string text, bool ok, long epoch, long generation)
    {
        Assert.Equal(ok, WindowReference.TryParse(text, out var reference));
        if (ok)
        {
            Assert.Equal(new WindowReference(epoch, generation), reference);
            Assert.Equal(text, reference.ToString());
        }
    }

    [Fact]
    public void S25_3_the_feed_carries_facts_only_and_no_eligibility_field()
    {
        var world = new World();
        world.Put(70, title: "Tool", tool: true, exe: "C:\\Apps\\Tool.exe");
        world.Put(71, title: "Cloaked", cloak: CloakState.App);
        world.Model.Observe(new(70, ObservationKind.Created));
        world.Model.Observe(new(71, ObservationKind.Created));
        world.Poster.Drain();

        var feed = WindowFeeds.Windows(world.Model.Current, Epoch);
        var items = feed["windows"]!.AsArray().Select(n => n!.AsObject()).ToList();

        Assert.Equal(2, items.Count);
        string[] required =
        [
            "windowKey", "hwnd", "processId", "title", "groupKey", "monitorId", "desktopId", "pinnedToAll", "visible", "cloak",
            "shellHidden", "toolWindow", "noActivate", "appWindow", "titled", "firstSeen", "activatedAt", "active", "minimized",
            "attention", "progress",
        ];
        foreach (var item in items)
        {
            foreach (var name in required)
            {
                Assert.True(item.ContainsKey(name), name);
            }

            Assert.DoesNotContain(item, p => p.Key.Contains("eligible", StringComparison.OrdinalIgnoreCase));
        }

        Assert.Equal("app", items[1]["cloak"]!.GetValue<string>());
        Assert.True(items[0]["toolWindow"]!.GetValue<bool>());
        Assert.Equal("C:\\Apps\\Tool.exe", items[0]["exe"]!.GetValue<string>());
        Assert.Equal("c:\\apps\\tool.exe", items[0]["groupKey"]!.GetValue<string>());
        Assert.Equal(Ref(world.KeyOf(70)), items[0]["windowKey"]!.GetValue<string>());
    }

    [Fact]
    public async Task S25_8_both_feeds_are_published_together_with_the_same_revision()
    {
        var world = new World();
        var published = new List<(string Feed, JsonNode? Body)>();
        using var service = new WindowService(world.Model, world.Tracker, (feed, body) => published.Add((feed, body)));
        world.Put(80, title: "A");
        world.Tracker.Handles = [80];

        await world.StartAsync(service);
        world.Poster.Drain();
        Assert.Equal(["host.windows", "host.desktops"], published.Select(p => p.Feed));
        Assert.Equal(1, published[0].Body!["revision"]!.GetValue<long>());
        Assert.Equal(1, published[1].Body!["revision"]!.GetValue<long>());

        world.Put(81, title: "B");
        world.Tracker.Sink!(new WindowObservation(81, ObservationKind.Created));
        world.Poster.Drain();

        Assert.Equal(4, published.Count);
        Assert.Equal(published[2].Body!["revision"]!.GetValue<long>(), published[3].Body!["revision"]!.GetValue<long>());
        Assert.Equal(2, published[3].Body!["revision"]!.GetValue<long>());
        Assert.Equal(2, published[3].Body!["desktops"]!.AsArray()[0]!["windowCount"]!.GetValue<int>());
    }

    [Fact]
    public void S25_8_a_burst_of_observations_publishes_once()
    {
        var world = new World();
        for (var i = 0; i < 20; i++)
        {
            world.Put(90 + i, title: "W" + i);
            world.Model.Observe(new(90 + i, ObservationKind.Created));
        }

        world.Poster.Drain();

        Assert.Single(world.Changes);
        Assert.Equal(20, world.Model.Current.Windows.Count);
    }

    [Fact]
    public async Task S25_10_a_clean_start_publishes_revision_one_before_the_tracker_starts()
    {
        var world = new World();
        var order = new List<string>();
        world.Tracker.OnStart = () => order.Add("tracker");
        using var service = new WindowService(world.Model, world.Tracker, (feed, _) => order.Add(feed));
        world.Put(1, title: "Top");
        world.Put(2, title: "Bottom");
        world.Tracker.Handles = [1, 2];

        await world.StartAsync(service);

        Assert.Equal(["host.windows", "host.desktops", "tracker"], order);
        Assert.Equal([2, 1], world.Model.Current.Windows.Select(w => (int)w.Key.Handle));
    }

    [Fact]
    public async Task S25_10_an_empty_start_still_publishes_revision_one()
    {
        var world = new World();
        using var service = new WindowService(world.Model, world.Tracker, (_, _) => { });

        await world.StartAsync(service);

        Assert.Equal(1, world.Model.Current.Revision);
    }

    [Fact]
    public void S25_5_the_active_window_follows_the_first_releases_rule()
    {
        var world = new World();
        world.Put(100, title: "A");
        world.Put(101, title: "B");
        world.Put(102, title: "Panel", tool: true, ownProcess: true);
        world.Put(103, title: "");
        foreach (var h in new[] { 100, 101 })
        {
            world.Model.Observe(new(h, ObservationKind.Created));
        }

        world.Model.Observe(new(100, ObservationKind.Foreground));
        world.Poster.Drain();
        Assert.Equal([100], world.ActiveHandles());

        world.Model.Observe(new(102, ObservationKind.Foreground));
        world.Poster.Drain();
        Assert.Equal([100], world.ActiveHandles());

        world.Model.Observe(new(103, ObservationKind.Foreground));
        world.Poster.Drain();
        Assert.Empty(world.ActiveHandles());

        world.Model.Observe(new(101, ObservationKind.Foreground));
        world.Poster.Drain();
        Assert.Equal([101], world.ActiveHandles());
        Assert.True(world.Model.Current.Windows.Single(w => w.Key.Handle == 101).ActivatedAt
                    > world.Model.Current.Windows.Single(w => w.Key.Handle == 100).ActivatedAt);
    }

    [Fact]
    public void S25_5_a_flash_sets_attention_until_the_window_is_activated()
    {
        var world = new World();
        world.Put(110, title: "Flasher");
        world.Put(111, title: "Other");
        world.Model.Observe(new(110, ObservationKind.Created));
        world.Model.Observe(new(111, ObservationKind.Created));
        world.Model.Observe(new(111, ObservationKind.Foreground));
        world.Model.Observe(new(110, ObservationKind.Flashed));
        world.Poster.Drain();
        Assert.True(world.Model.Current.Windows.Single(w => w.Key.Handle == 110).Attention);

        world.Model.Observe(new(110, ObservationKind.Foreground));
        world.Poster.Drain();
        Assert.False(world.Model.Current.Windows.Single(w => w.Key.Handle == 110).Attention);
    }

    [Fact]
    public void S25_5_a_redraw_flashes_only_a_background_window_with_an_unchanged_title()
    {
        var world = new World();
        world.Put(120, title: "Same");
        world.Put(121, title: "Fg");
        world.Model.Observe(new(120, ObservationKind.Created));
        world.Model.Observe(new(121, ObservationKind.Created));
        world.Model.Observe(new(121, ObservationKind.Foreground));
        world.Model.Observe(new(121, ObservationKind.Redrawn));
        world.Poster.Drain();
        Assert.False(world.Model.Current.Windows.Single(w => w.Key.Handle == 121).Attention);

        world.Put(120, title: "Changed");
        world.Model.Observe(new(120, ObservationKind.Redrawn));
        world.Poster.Drain();
        Assert.False(world.Model.Current.Windows.Single(w => w.Key.Handle == 120).Attention);

        world.Model.Observe(new(120, ObservationKind.TitleChanged));
        world.Model.Observe(new(120, ObservationKind.Redrawn));
        world.Poster.Drain();
        Assert.True(world.Model.Current.Windows.Single(w => w.Key.Handle == 120).Attention);
    }

    [Fact]
    public void S25_5_an_owned_window_publishes_its_owner_and_the_shell_and_host_panels_are_never_admitted()
    {
        var world = new World();
        world.Put(130, title: "Parent");
        world.Put(131, title: "Child", owner: 130);
        world.Put(132, title: "Desktop", shell: true);
        world.Put(133, title: "Host panel", tool: true, ownProcess: true);
        world.Put(134, title: "Host settings", ownProcess: true);
        foreach (var h in new[] { 130, 131, 132, 133, 134 })
        {
            world.Model.Observe(new(h, ObservationKind.Created));
        }

        world.Poster.Drain();

        Assert.Equal([130, 131, 134], world.Handles().Order());
        Assert.Equal(world.KeyOf(130), world.Model.Current.Windows.Single(w => w.Key.Handle == 131).Owner);
    }

    [Fact]
    public void S25_11_the_scope_filter_keeps_other_windows_out_of_the_registry()
    {
        var world = new World(scope: f => f.Title.StartsWith("szd-", StringComparison.Ordinal));
        world.Put(140, title: "szd-1 normal");
        world.Put(141, title: "Somebody's real window");
        world.Model.Observe(new(140, ObservationKind.Created));
        world.Model.Observe(new(141, ObservationKind.Created));
        world.Poster.Drain();

        Assert.Equal([140], world.Handles());
    }

    [Fact]
    public void S25_11_hundreds_of_windows_publish_in_bounded_work()
    {
        var world = new World();
        for (var i = 0; i < 600; i++)
        {
            world.Put(1000 + i, title: "W" + i);
            world.Model.Observe(new(1000 + i, ObservationKind.Created));
        }

        world.Poster.Drain();
        var before = world.Facts.Reads;
        world.Model.Observe(new(0, ObservationKind.Rescan));
        world.Poster.Drain();

        Assert.Equal(600, world.Model.Current.Windows.Count);
        Assert.Single(world.Changes);
        Assert.Equal(600, world.Facts.Reads - before);
    }

    [Fact]
    public async Task S25_7_activate_by_key_reports_foreground_or_requested_and_never_claims_more()
    {
        var world = new World();
        world.Put(150, title: "A");
        world.Model.Observe(new(150, ObservationKind.Created));
        world.Poster.Drain();
        var foreground = new FakeForeground { Result = ActivationResult.Requested };
        var args = new JsonObject { ["windowKey"] = Ref(world.KeyOf(150)) };

        var requested = await WindowCommands.ActivateAsync(world.Model, foreground, args, TestContext.Current.CancellationToken);
        foreground.Result = ActivationResult.Foreground;
        var confirmed = await WindowCommands.ActivateAsync(world.Model, foreground, args, TestContext.Current.CancellationToken);

        Assert.Equal("requested", requested.Value!.Value!["outcome"]!.GetValue<string>());
        Assert.Equal("foreground", confirmed.Value!.Value!["outcome"]!.GetValue<string>());
        Assert.Equal([(nint)150, 150], foreground.Activated);
    }

    [Fact]
    public async Task S25_7_a_stale_or_gone_key_has_no_side_effect()
    {
        var world = new World();
        world.Put(160, title: "A");
        world.Model.Observe(new(160, ObservationKind.Created));
        world.Poster.Drain();
        var key = world.KeyOf(160);
        var foreground = new FakeForeground();

        var stale = await WindowCommands.ActivateAsync(world.Model, foreground,
            new JsonObject { ["windowKey"] = new WindowReference(Epoch + 1, key.Generation).ToString() }, TestContext.Current.CancellationToken);
        world.Facts.Remove(160);
        var gone = await WindowCommands.MinimizeAsync(world.Model, foreground,
            new JsonObject { ["windowKey"] = Ref(key) }, TestContext.Current.CancellationToken);

        Assert.Equal(CoreErrorCode.StaleWindowReference, stale.Error!.Code);
        Assert.Equal(CoreErrorCode.WindowGone, gone.Error!.Code);
        Assert.Empty(foreground.Activated);
        Assert.Empty(foreground.Minimised);
    }

    [Fact]
    public async Task S25_7_a_window_that_closes_during_activation_is_gone_and_leaves_no_entry()
    {
        var world = new World();
        world.Put(170, title: "Closing");
        world.Model.Observe(new(170, ObservationKind.Created));
        world.Poster.Drain();
        var foreground = new FakeForeground { OnActivate = () => world.Facts.Remove(170), Fail = CoreErrorCode.WindowGone };

        var result = await WindowCommands.ActivateAsync(world.Model, foreground,
            new JsonObject { ["windowKey"] = Ref(world.KeyOf(170)) }, TestContext.Current.CancellationToken);
        world.Poster.Drain();

        Assert.Equal(CoreErrorCode.WindowGone, result.Error!.Code);
        Assert.Empty(world.Model.Current.Windows);
    }

    [Fact]
    public async Task S25_7_the_legacy_hwnd_form_reaches_the_current_holder_or_an_unmanaged_window_and_returns_no_result()
    {
        var world = new World();
        world.Put(180, title: "Held");
        world.Model.Observe(new(180, ObservationKind.Created));
        world.Poster.Drain();
        world.Put(181, title: "", visible: false);
        var foreground = new FakeForeground();

        var held = await WindowCommands.ActivateAsync(world.Model, foreground, new JsonObject { ["hwnd"] = 180 }, TestContext.Current.CancellationToken);
        var unmanaged = await WindowCommands.ActivateAsync(world.Model, foreground, new JsonObject { ["hwnd"] = 181 }, TestContext.Current.CancellationToken);
        var absent = await WindowCommands.ActivateAsync(world.Model, foreground, new JsonObject { ["hwnd"] = 182 }, TestContext.Current.CancellationToken);

        Assert.True(held.Ok);
        Assert.Null(held.Value!.Value);
        Assert.True(unmanaged.Ok);
        Assert.Equal(CoreErrorCode.WindowGone, absent.Error!.Code);
        Assert.Equal([(nint)180, 181], foreground.Activated);
    }

    [Fact]
    public async Task S25_7_a_command_needs_exactly_one_of_the_two_forms()
    {
        var world = new World();
        var foreground = new FakeForeground();

        var neither = await WindowCommands.ActivateAsync(world.Model, foreground, new JsonObject(), TestContext.Current.CancellationToken);
        var both = await WindowCommands.MinimizeAsync(world.Model, foreground,
            new JsonObject { ["hwnd"] = 1, ["windowKey"] = "wk1:1:1" }, TestContext.Current.CancellationToken);

        Assert.Equal(CoreErrorCode.CommandFailed, neither.Error!.Code);
        Assert.Equal(CoreErrorCode.CommandFailed, both.Error!.Code);
    }

    private static string Ref(WindowKey key) => new WindowReference(Epoch, key.Generation).ToString();

    private sealed class World
    {
        public World(Func<WindowFacts, bool>? scope = null)
        {
            Model = new WindowModel(Facts, Poster, Epoch, scope);
            Model.Changed += (_, snapshot) => Changes.Add(snapshot);
            Tracker = new FakeTracker(Facts);
        }

        public FakeFacts Facts { get; } = new();

        public FakePoster Poster { get; } = new();

        public WindowModel Model { get; }

        public FakeTracker Tracker { get; }

        public List<WindowSnapshot> Changes { get; } = [];

        public void Put(int handle, string title, bool visible = true, bool minimised = false, CloakState cloak = CloakState.None,
            bool tool = false, bool ownProcess = false, bool shell = false, int process = 1, int owner = 0, string? exe = null) =>
            Facts.Items[handle] = new WindowFacts(new WindowFingerprint(process, process * 10L, 1, "Cls"), title, exe, null, owner, "\\\\.\\DISPLAY1",
                visible, cloak, tool, false, false, minimised, ownProcess, shell);

        /// <summary>Starts the service; the seed runs on the fake UI queue, so it is drained here.</summary>
        public async Task StartAsync(WindowService service)
        {
            var started = service.StartAsync(TestContext.Current.CancellationToken);
            Poster.Drain();
            await started;
        }

        public WindowKey KeyOf(int handle) => Model.Current.Windows.Single(w => w.Key.Handle == handle).Key;

        public int[] Handles() => [.. Model.Current.Windows.Select(w => (int)w.Key.Handle)];

        public int[] ActiveHandles() => [.. Model.Current.Windows.Where(w => w.Active).Select(w => (int)w.Key.Handle)];
    }

    private sealed class FakeFacts : IWindowFacts
    {
        public Dictionary<int, WindowFacts> Items { get; } = [];

        public int Reads { get; private set; }

        public void Remove(int handle) => Items.Remove(handle);

        public WindowFacts? Read(nint handle)
        {
            Reads++;
            return Items.GetValueOrDefault((int)handle);
        }
    }

    private sealed class FakePoster : IUiPoster
    {
        private readonly Queue<Action> _queue = new();

        public void Post(Action action) => _queue.Enqueue(action);

        public void Drain()
        {
            while (_queue.TryDequeue(out var action))
            {
                action();
            }
        }
    }

    private sealed class FakeTracker(FakeFacts facts) : IWindowTracker
    {
        public IReadOnlyList<nint> Handles { get; set; } = [];

        public Action<WindowObservation>? Sink { get; private set; }

        public Action? OnStart { get; set; }

        public Task<IReadOnlyList<nint>> EnumerateAsync(CancellationToken ct) => Task.FromResult(Handles);

        public WindowFingerprint? Capture(nint handle) => facts.Items.TryGetValue((int)handle, out var f) ? f.Fingerprint : null;

        public void Start(Action<WindowObservation> sink)
        {
            Sink = sink;
            OnStart?.Invoke();
        }

        public void Stop() => Sink = null;
    }

    private sealed class FakeForeground : IForegroundService
    {
        public List<nint> Activated { get; } = [];

        public List<nint> Minimised { get; } = [];

        public ActivationResult Result { get; set; } = ActivationResult.Foreground;

        public Action? OnActivate { get; set; }

        public CoreErrorCode? Fail { get; set; }

        public void AllowForeground(int processId)
        {
        }

        public Task<Outcome> ActivateWindowAsync(nint hwnd, CancellationToken ct)
        {
            Activated.Add(hwnd);
            return Task.FromResult(Outcome.Success);
        }

        public Task<Outcome> MinimizeWindowAsync(nint hwnd, CancellationToken ct)
        {
            Minimised.Add(hwnd);
            return Task.FromResult(Outcome.Success);
        }

        public Task<Outcome<ActivationResult>> ActivateConfirmedAsync(nint hwnd, CancellationToken ct)
        {
            Activated.Add(hwnd);
            OnActivate?.Invoke();
            return Task.FromResult(Fail is { } code
                ? Outcome<ActivationResult>.Fail(code, "failed")
                : Outcome<ActivationResult>.Success(Result));
        }
    }
}

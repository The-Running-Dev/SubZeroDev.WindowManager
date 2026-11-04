using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>Slice S26: virtual desktops over the shared window model. Every window, journal and effect is a fake (I38, S26.10).</summary>
public sealed class VirtualDesktopTests
{
    private const long Epoch = 4242;

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task S26_1_a_switch_is_atomic_in_both_feeds()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        world.Put(3, "C");
        world.Put(4, "D");
        await world.StartAsync();
        var first = world.Set.CurrentId;
        var second = world.Set.Create("Two").Value;
        Assert.True((await world.Pump(world.Set.MoveAsync(world.KeyOf(3), second, Ct))).Ok);
        Assert.True((await world.Pump(world.Set.MoveAsync(world.KeyOf(4), second, Ct))).Ok);
        world.Changes.Clear();
        world.Feeds.Clear();

        var during = new List<(bool Busy, Guid Current)>();
        world.Effects.Before = _ =>
        {
            during.Add((world.Model.Desktops.Busy, world.Model.Desktops.Current));
            world.Poster.Drain();
        };

        var switched = await world.Pump(world.Set.SwitchAsync(second, Ct));

        Assert.True(switched.Ok);
        Assert.Equal([(true, first)], during);
        Assert.All(world.Changes, AssertEveryWindowOnExactlyOneSide);
        var busy = world.Changes.First();
        Assert.True(busy.DesktopsOrDefault.Busy);
        Assert.Equal(first, busy.DesktopsOrDefault.Current);
        Assert.Equal(second, busy.DesktopsOrDefault.Switching!.To);
        var last = world.Changes.Last();
        Assert.False(last.DesktopsOrDefault.Busy);
        Assert.Equal(second, last.DesktopsOrDefault.Current);
        Assert.Equal([3, 4], last.Windows.Where(w => w.Visible).Select(w => (int)w.Key.Handle).Order());
        Assert.Equal([1, 2], last.Windows.Where(w => w.ShellHidden).Select(w => (int)w.Key.Handle).Order());

        // Both feeds come from one snapshot, back to back, with the same revision.
        Assert.Equal(0, world.Feeds.Count % 2);
        for (var i = 0; i < world.Feeds.Count; i += 2)
        {
            Assert.Equal(WindowFeeds.WindowsFeedId, world.Feeds[i].Id);
            Assert.Equal(WindowFeeds.DesktopsFeedId, world.Feeds[i + 1].Id);
            Assert.Equal((long)world.Feeds[i].Node!["revision"]!, (long)world.Feeds[i + 1].Node!["revision"]!);
        }

        var desktops = world.Feeds.Last().Node!.AsObject();
        Assert.Equal(second.ToString("D"), (string)desktops["current"]!);
        Assert.False((bool)desktops["busy"]!);
        Assert.Null(desktops["switching"]);
        Assert.Equal([2, 2], desktops["desktops"]!.AsArray().Select(d => (int)d!["windowCount"]!));
    }

    [Fact]
    public async Task S26_1_a_pinned_window_is_on_every_desktop_and_counted_on_none()
    {
        using var world = new World();
        world.Put(1, "Pinned");
        world.Put(2, "Plain");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        Assert.True((await world.Pump(world.Set.PinToAllAsync(world.KeyOf(1), true, Ct))).Ok);

        Assert.True((await world.Pump(world.Set.SwitchAsync(second, Ct))).Ok);

        var snapshot = world.Model.Current;
        Assert.True(snapshot.Windows.Single(w => w.Key.Handle == 1).Visible);
        Assert.True(snapshot.Windows.Single(w => w.Key.Handle == 1).PinnedToAll);
        Assert.True(snapshot.Windows.Single(w => w.Key.Handle == 2).ShellHidden);
        var counts = WindowFeeds.Desktops(snapshot)["desktops"]!.AsArray().Select(d => (int)d!["windowCount"]!).ToArray();
        Assert.Equal([1, 0], counts);
    }

    [Fact]
    public async Task S26_2_a_hidden_window_stays_listed_with_its_desktop_and_a_closed_one_leaves()
    {
        using var world = new World();
        world.Put(1, "Here");
        world.Put(2, "Away");
        await world.StartAsync();
        var first = world.Set.CurrentId;
        var second = world.Set.Create(null).Value;
        Assert.True((await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct))).Ok);

        var windows = WindowFeeds.Windows(world.Model.Current, Epoch)["windows"]!.AsArray();
        var away = windows.Single(w => (string)w!["title"]! == "Away")!;
        Assert.True((bool)away["shellHidden"]!);
        Assert.False((bool)away["visible"]!);
        Assert.Equal(second.ToString("D"), (string)away["desktopId"]!);
        Assert.Equal(first.ToString("D"), (string)windows.Single(w => (string)w!["title"]! == "Here")!["desktopId"]!);

        // The window closes while it is hidden: the tracker says so and the model drops it.
        world.Facts.Remove(2);
        world.Model.Observe(new WindowObservation(2, ObservationKind.Destroyed));
        world.Poster.Drain();

        Assert.Equal([1], world.Handles());
        Assert.Equal([1, 0], WindowFeeds.Desktops(world.Model.Current)["desktops"]!.AsArray().Select(d => (int)d!["windowCount"]!));
    }

    [Fact]
    public async Task S26_3_a_move_writes_the_journal_before_it_hides_and_files_the_window_on_the_target()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create("Two").Value;
        world.Order.Clear();

        var moved = await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));

        Assert.True(moved.Ok);
        Assert.Equal("journal", world.Order.First());
        Assert.Contains("hide:2", world.Order);
        Assert.True(world.Order.IndexOf("journal") < world.Order.IndexOf("hide:2"));
        var window = world.Model.Current.Windows.Single(w => w.Key.Handle == 2);
        Assert.Equal(second, window.DesktopId);
        Assert.True(window.ShellHidden);
        Assert.Equal([2], world.Journal.Handles());
    }

    [Fact]
    public async Task S26_3_a_move_to_a_missing_desktop_or_a_stale_window_changes_nothing()
    {
        using var world = new World();
        world.Put(1, "A");
        await world.StartAsync();
        var key = world.KeyOf(1);

        var missing = await world.Pump(world.Set.MoveAsync(key, Guid.NewGuid(), Ct));
        Assert.Equal(CoreErrorCode.DesktopNotFound, missing.Error!.Code);

        var stale = await DesktopCommands.MoveAsync(world.Set, world.Model,
            new JsonObject { ["windowKey"] = new WindowReference(Epoch + 1, key.Generation).ToString(), ["id"] = "new" }, Ct);
        Assert.Equal(CoreErrorCode.StaleWindowReference, stale.Error!.Code);

        var gone = await DesktopCommands.MoveAsync(world.Set, world.Model,
            new JsonObject { ["windowKey"] = new WindowReference(Epoch, key.Generation + 50).ToString(), ["id"] = world.Set.CurrentId.ToString("D") }, Ct);
        Assert.Equal(CoreErrorCode.WindowGone, gone.Error!.Code);

        Assert.Single(world.Set.Desktops);
        Assert.Empty(world.Effects.Plans);
        Assert.Empty(world.Journal.Writes);
    }

    [Fact]
    public async Task S26_3_a_move_to_new_creates_the_desktop_and_hides_the_window()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();

        var result = await world.Pump(DesktopCommands.MoveAsync(world.Set, world.Model,
            new JsonObject { ["windowKey"] = Ref(world.KeyOf(2)), ["id"] = "new" }, Ct));

        Assert.True(result.Ok);
        var created = Guid.Parse((string)result.Value!.Value!["id"]!);
        Assert.Equal(2, world.Set.Desktops.Count);
        Assert.Equal(created, world.Set.Desktops[1].Id);
        Assert.Equal(created, world.Model.Current.Windows.Single(w => w.Key.Handle == 2).DesktopId);
        Assert.True(world.Model.Current.Windows.Single(w => w.Key.Handle == 2).ShellHidden);
    }

    [Fact]
    public async Task S26_3_a_window_closed_before_it_is_filed_leaves_no_desktop_behind()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var key = world.KeyOf(2);

        // The window dies while the journal is being written: after the lookup, before the re-file.
        world.Journal.OnWrite = () =>
        {
            world.Facts.Remove(2);
            world.Model.Observe(new WindowObservation(2, ObservationKind.Destroyed));
            world.Poster.Drain();
        };

        var result = await world.Pump(world.Set.MoveToNewAsync(key, Ct));

        Assert.Equal(CoreErrorCode.WindowGone, result.Error!.Code);
        Assert.Single(world.Set.Desktops);
        Assert.Empty(world.Effects.Plans);
        Assert.Empty(world.Journal.Handles());
        Assert.Equal([1], world.Handles());
    }

    [Fact]
    public async Task S26_3_a_window_closed_after_it_is_filed_but_before_it_is_hidden_is_cleaned_up()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        world.Effects.Before = _ =>
        {
            world.Facts.Remove(2);
            world.Model.Observe(new WindowObservation(2, ObservationKind.Destroyed));
        };

        var result = await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));

        Assert.Equal(CoreErrorCode.WindowGone, result.Error!.Code);
        Assert.Equal([1], world.Handles());
        Assert.Empty(world.Model.HiddenWindows());
        Assert.Empty(world.Journal.Handles());
        Assert.Equal(2, world.Set.Desktops.Count);
    }

    [Fact]
    public async Task S26_3_a_window_that_refuses_to_hide_is_not_movable_and_stays_visible()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "Elevated");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        world.Effects.Refuse.Add(2);

        var result = await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));

        Assert.Equal(CoreErrorCode.WindowNotMovable, result.Error!.Code);
        var window = world.Model.Current.Windows.Single(w => w.Key.Handle == 2);
        Assert.True(window.Visible);
        Assert.False(window.ShellHidden);
        Assert.Equal(world.Set.CurrentId, window.DesktopId);
        Assert.Single(world.Diagnostics);
        Assert.Equal(CoreErrorCode.WindowNotMovable, world.Diagnostics[0].Code);

        // A second attempt is refused up front and logs nothing more.
        var again = await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
        Assert.Equal(CoreErrorCode.WindowNotMovable, again.Error!.Code);
        Assert.Single(world.Diagnostics);
    }

    [Fact]
    public async Task S26_4_a_window_that_closes_during_a_switch_is_dropped_and_the_next_window_is_activated()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        world.Put(3, "C");
        world.Put(4, "D");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(3), second, Ct));
        await world.Pump(world.Set.MoveAsync(world.KeyOf(4), second, Ct));

        // D was the last active window of the target; it closes while the switch runs.
        world.Effects.Before = _ =>
        {
            world.Facts.Remove(4);
            world.Model.Observe(new WindowObservation(4, ObservationKind.Destroyed));
        };

        var switched = await world.Pump(world.Set.SwitchAsync(second, Ct));

        Assert.True(switched.Ok);
        Assert.Equal(second, world.Set.CurrentId);
        Assert.Equal([1, 2, 3], world.Handles().Order());
        Assert.DoesNotContain(world.Model.Current.Windows, w => w.Key.Handle == 4);
        Assert.Equal([(nint)3], world.Foreground.Activated);
        Assert.Equal([1, 2], world.Journal.Handles().Order());
        Assert.DoesNotContain(4, world.Journal.Handles());
    }

    [Fact]
    public async Task S26_4_a_window_destroyed_while_the_effects_run_is_never_marked_stuck()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        world.Effects.Before = _ =>
        {
            world.Facts.Remove(2);
            world.Model.Observe(new WindowObservation(2, ObservationKind.Destroyed));
        };

        Assert.True((await world.Pump(world.Set.SwitchAsync(second, Ct))).Ok);

        Assert.Empty(world.Diagnostics);
        Assert.Equal([1], world.Handles());
        Assert.False(world.Model.Current.Windows.Single().Visible);
    }

    [Fact]
    public async Task S26_4_a_completed_switch_stays_completed_when_the_activation_finds_the_window_gone()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
        world.Foreground.Fail = CoreErrorCode.WindowGone;

        var switched = await world.Pump(world.Set.SwitchAsync(second, Ct));

        Assert.True(switched.Ok);
        Assert.Equal(second, world.Set.CurrentId);
        Assert.False(world.Model.Desktops.Busy);
    }

    [Fact]
    public async Task S26_4_a_switch_requested_while_one_runs_is_held_and_only_the_latest_applies()
    {
        using var world = new World();
        world.Put(1, "A");
        await world.StartAsync();
        var first = world.Set.CurrentId;
        var second = world.Set.Create(null).Value;
        var third = world.Set.Create(null).Value;
        world.Effects.Gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        var running = world.Set.SwitchAsync(second, Ct);
        var held = world.Set.SwitchAsync(third, Ct);
        var latest = world.Set.SwitchAsync(first, Ct);

        // The held one is replaced as soon as the later request arrives.
        var superseded = await held;
        Assert.Equal(CoreErrorCode.SwitchSuperseded, superseded.Error!.Code);

        world.Effects.Gate.SetResult();
        Assert.True((await world.Pump(running)).Ok);
        Assert.True((await world.Pump(latest)).Ok);
        Assert.Equal(first, world.Set.CurrentId);
        Assert.DoesNotContain(world.Effects.Plans, p => p.To == third);
    }

    [Fact]
    public async Task S26_4_switching_to_the_current_desktop_does_nothing()
    {
        using var world = new World();
        world.Put(1, "A");
        await world.StartAsync();

        Assert.True((await world.Pump(world.Set.SwitchAsync(world.Set.CurrentId, Ct))).Ok);

        Assert.Empty(world.Effects.Plans);
        Assert.Empty(world.Journal.Writes);
        var unknown = await world.Pump(world.Set.SwitchAsync(Guid.NewGuid(), Ct));
        Assert.Equal(CoreErrorCode.DesktopNotFound, unknown.Error!.Code);
    }

    [Fact]
    public async Task S26_5_the_journal_names_every_window_before_any_is_hidden_then_narrows_to_what_is_hidden()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        world.Put(3, "Elevated");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        world.Effects.Refuse.Add(3);
        var before = new List<int>();
        world.Effects.Before = _ => before.AddRange(world.Journal.Handles());
        world.Order.Clear();

        Assert.True((await world.Pump(world.Set.SwitchAsync(second, Ct))).Ok);

        // A crash at the hides finds all three named; once the elevated one refused, only the two that are hidden remain.
        Assert.Equal([1, 2, 3], before.Order());
        Assert.Equal([1, 2], world.Journal.Handles().Order());
        Assert.True(world.Order.IndexOf("journal") < world.Order.IndexOf("hide:1"));
    }

    [Fact]
    public async Task S26_5_a_journal_write_failure_stops_the_switch_before_any_hide()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var first = world.Set.CurrentId;
        var second = world.Set.Create(null).Value;
        world.Journal.Fail = true;

        var switched = await world.Pump(world.Set.SwitchAsync(second, Ct));

        Assert.Equal(CoreErrorCode.JournalWriteFailed, switched.Error!.Code);
        Assert.Empty(world.Effects.Plans);
        Assert.Equal(first, world.Set.CurrentId);
        Assert.False(world.Model.Desktops.Busy);
        Assert.All(world.Model.Current.Windows, w => Assert.True(w.Visible));
    }

    [Fact]
    public async Task S26_5_after_a_relaunch_every_window_is_on_the_first_desktop_and_old_references_are_stale()
    {
        string store;
        string oldReference;
        Guid firstDesktop;
        using (var world = new World())
        {
            world.Put(1, "A");
            world.Put(2, "B");
            await world.StartAsync();
            var second = world.Set.Create("Two").Value;
            firstDesktop = world.Set.CurrentId;
            await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
            oldReference = Ref(world.KeyOf(2));
            store = world.StorePath;
            await world.Pump(world.Set.SwitchAsync(second, Ct));
            await world.Store.SaveAsync(world.Set.Desktops, world.Set.CurrentId, Ct);
            world.KeepStore = true;
        }

        // The second world owns the directory from here and removes it.
        using var again = new World(store, epoch: Epoch + 1);
        again.Put(1, "A");
        again.Put(2, "B");
        await again.StartAsync();

        // The desktops come back; membership does not, and the shell starts on the first desktop.
        Assert.Equal(2, again.Set.Desktops.Count);
        Assert.Equal(firstDesktop, again.Set.CurrentId);
        Assert.All(again.Model.Current.Windows, w => Assert.Equal(firstDesktop, w.DesktopId));
        Assert.Equal(CoreErrorCode.StaleWindowReference, again.Model.Resolve(oldReference).Error!.Code);
    }

    [Fact]
    public void S26_5_the_guardian_shows_only_hidden_windows_whose_five_fingerprint_fields_match()
    {
        var entries = new[]
        {
            new Recovery.HiddenWindow(10, 100, 1000, 7, "Cls"),
            new Recovery.HiddenWindow(11, 100, 1000, 7, "Cls"),
            new Recovery.HiddenWindow(12, 100, 1000, 7, "Cls"),
            new Recovery.HiddenWindow(13, 100, 1000, 7, "Cls"),
            new Recovery.HiddenWindow(14, 100, 1000, 7, "Cls"),
            new Recovery.HiddenWindow(15, 100, 1000, 7, "Cls"),
        };
        var payload = Recovery.DesktopChange.HiddenWindows(entries, DateTimeOffset.UtcNow).Payload;
        var port = new FakeShowPort
        {
            [10] = new Recovery.WindowProbe(100, 1000, 7, "Cls", Visible: false),
            [11] = new Recovery.WindowProbe(100, 1000, 7, "Cls", Visible: true),
            [12] = new Recovery.WindowProbe(999, 1000, 7, "Cls", Visible: false),
            [13] = new Recovery.WindowProbe(100, 5, 7, "Cls", Visible: false),
            [14] = new Recovery.WindowProbe(100, 1000, 8, "Cls", Visible: false),
            [15] = new Recovery.WindowProbe(100, 1000, 7, "Other", Visible: false),
        };

        var shown = Recovery.HiddenWindowRestorer.Restore(payload, port);

        Assert.Equal([(nint)10], shown);
        Assert.Equal([(nint)10], port.Shown);
    }

    [Fact]
    public void S26_5_a_window_that_is_gone_is_skipped_and_the_revert_reports_success()
    {
        var payload = Recovery.DesktopChange.HiddenWindows([new Recovery.HiddenWindow(77, 1, 1, 1, "Cls")], DateTimeOffset.UtcNow).Payload;
        var port = new FakeShowPort();

        var outcome = Recovery.Reverter.RevertHiddenWindows(payload, port);

        Assert.True(outcome.Ok);
        Assert.Empty(port.Shown);
    }

    [Fact]
    public async Task S26_5_the_shell_showing_everything_on_exit_empties_the_journal()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.SwitchAsync(second, Ct));
        Assert.Equal([1, 2], world.Journal.Handles().Order());

        await world.Pump(world.Set.RestoreAllAsync());

        Assert.Empty(world.Journal.Handles());
        Assert.Equal(1, world.Effects.ShowAllCalls);
        Assert.All(world.Model.Current.Windows, w => Assert.False(w.ShellHidden));
    }

    [Fact]
    public async Task S26_6_the_host_commands_switch_by_direction_and_wrap()
    {
        using var world = new World();
        world.Put(1, "A");
        await world.StartAsync();
        var first = world.Set.CurrentId;
        var second = world.Set.Create(null).Value;

        Assert.True((await world.Pump(DesktopCommands.SwitchAsync(world.Set, new JsonObject { ["direction"] = "next" }, Ct))).Ok);
        Assert.Equal(second, world.Set.CurrentId);
        Assert.True((await world.Pump(DesktopCommands.SwitchAsync(world.Set, new JsonObject { ["direction"] = "next" }, Ct))).Ok);
        Assert.Equal(first, world.Set.CurrentId);
        Assert.True((await world.Pump(DesktopCommands.SwitchAsync(world.Set, new JsonObject { ["direction"] = "previous" }, Ct))).Ok);
        Assert.Equal(second, world.Set.CurrentId);

        var neither = await world.Pump(DesktopCommands.SwitchAsync(world.Set, new JsonObject(), Ct));
        Assert.Equal(CoreErrorCode.CommandFailed, neither.Error!.Code);
        var both = await world.Pump(DesktopCommands.SwitchAsync(world.Set,
            new JsonObject { ["direction"] = "next", ["id"] = first.ToString("D") }, Ct));
        Assert.Equal(CoreErrorCode.CommandFailed, both.Error!.Code);
    }

    [Fact]
    public async Task S26_6_create_with_switch_to_lands_on_the_new_desktop_and_remove_keeps_the_last_one()
    {
        using var world = new World();
        world.Put(1, "A");
        await world.StartAsync();
        var first = world.Set.CurrentId;

        var created = await world.Pump(DesktopCommands.CreateAsync(world.Set, new JsonObject { ["name"] = "Work", ["switchTo"] = true }, Ct));

        Assert.True(created.Ok);
        var id = Guid.Parse((string)created.Value!.Value!["id"]!);
        Assert.Equal(id, world.Set.CurrentId);
        Assert.Equal("Work", world.Set.Desktops[1].Name);

        var removed = await world.Pump(DesktopCommands.RemoveAsync(world.Set, new JsonObject { ["id"] = id.ToString("D") }, Ct));
        Assert.True(removed.Ok);
        Assert.Equal(first, world.Set.CurrentId);
        Assert.Single(world.Set.Desktops);

        var last = await world.Pump(DesktopCommands.RemoveAsync(world.Set, new JsonObject { ["id"] = first.ToString("D") }, Ct));
        Assert.Equal(CoreErrorCode.LastDesktop, last.Error!.Code);
        var missing = await world.Pump(DesktopCommands.RemoveAsync(world.Set, new JsonObject { ["id"] = Guid.NewGuid().ToString("D") }, Ct));
        Assert.Equal(CoreErrorCode.DesktopNotFound, missing.Error!.Code);
    }

    [Fact]
    public async Task S26_6_removing_a_desktop_moves_its_windows_to_the_previous_one_and_shows_them_when_it_was_current()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var first = world.Set.CurrentId;
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
        await world.Pump(world.Set.SwitchAsync(second, Ct));

        Assert.True((await world.Pump(world.Set.RemoveAsync(second, Ct))).Ok);

        Assert.Equal(first, world.Set.CurrentId);
        Assert.Single(world.Set.Desktops);
        Assert.All(world.Model.Current.Windows, w =>
        {
            Assert.Equal(first, w.DesktopId);
            Assert.True(w.Visible);
            Assert.False(w.ShellHidden);
        });
        Assert.Empty(world.Journal.Handles());
    }

    [Fact]
    public async Task S26_6_close_posts_a_request_and_a_stale_reference_does_nothing()
    {
        using var world = new World();
        world.Put(1, "A");
        await world.StartAsync();
        var key = world.KeyOf(1);

        var stale = await DesktopCommands.CloseAsync(world.Effects, world.Model,
            new JsonObject { ["windowKey"] = new WindowReference(Epoch + 9, key.Generation).ToString() }, Ct);
        Assert.Equal(CoreErrorCode.StaleWindowReference, stale.Error!.Code);
        Assert.Empty(world.Effects.Closed);

        var closed = await DesktopCommands.CloseAsync(world.Effects, world.Model, new JsonObject { ["windowKey"] = Ref(key) }, Ct);
        Assert.True(closed.Ok);
        Assert.Equal([(nint)1], world.Effects.Closed);
    }

    [Fact]
    public async Task S26_2_a_window_that_shows_itself_on_another_desktop_is_hidden_again_and_asks_for_attention()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
        world.Effects.Plans.Clear();

        // The application shows itself: the tracker reports it visible.
        world.Facts.Items[2] = world.Facts.Items[2] with { Visible = true };
        world.Model.Observe(new WindowObservation(2, ObservationKind.Shown));
        world.Poster.Drain();
        await world.Pump(Task.Delay(20, Ct));

        var window = world.Model.Current.Windows.Single(w => w.Key.Handle == 2);
        Assert.True(window.ShellHidden);
        Assert.False(window.Visible);
        Assert.True(window.Attention);
        Assert.Single(world.Effects.Plans);
        Assert.Equal([(nint)2], world.Effects.Plans[0].Hide.Select(p => p.Key.Handle));
    }

    [Fact]
    public async Task S26_2_the_shells_own_shows_are_not_mistaken_for_a_window_showing_itself()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var first = world.Set.CurrentId;
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
        world.Effects.Plans.Clear();

        // The switch shows B; the tracker reports it right away, while the effects are still running.
        world.Effects.Before = plan =>
        {
            if (plan.Show.Count > 0)
            {
                world.Facts.Items[2] = world.Facts.Items[2] with { Visible = true };
                world.Model.Observe(new WindowObservation(2, ObservationKind.Shown));
                world.Poster.Drain();
            }
        };

        Assert.True((await world.Pump(world.Set.SwitchAsync(second, Ct))).Ok);
        await world.Pump(Task.Delay(20, Ct));

        Assert.Single(world.Effects.Plans);
        Assert.False(world.Model.Current.Windows.Single(w => w.Key.Handle == 2).Attention);
        Assert.Equal(first, world.Model.Current.Windows.Single(w => w.Key.Handle == 1).DesktopId);
    }

    [Fact]
    public async Task S26_10_a_hundred_windows_switch_in_one_plan_and_one_published_step()
    {
        using var world = new World();
        for (var i = 1; i <= 100; i++)
        {
            world.Put(i, "W" + i);
        }

        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        world.Changes.Clear();
        world.Effects.Plans.Clear();

        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.True((await world.Pump(world.Set.SwitchAsync(second, Ct))).Ok);
        watch.Stop();

        Assert.Single(world.Effects.Plans);
        Assert.Equal(100, world.Effects.Plans[0].Hide.Count);
        Assert.Equal(100, world.Journal.Handles().Count);
        Assert.True(watch.ElapsedMilliseconds < 2000, $"The switch took {watch.ElapsedMilliseconds} ms.");
        Assert.All(world.Changes, AssertEveryWindowOnExactlyOneSide);
        Assert.Equal(second, world.Changes.Last().DesktopsOrDefault.Current);
    }

    [Fact]
    public void S26_desktops_json_absent_is_one_desktop_and_invalid_is_one_desktop_with_a_problem_and_the_file_untouched()
    {
        var directory = Path.Combine(Path.GetTempPath(), "szd-desktops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "desktops.json");
            var absent = new DesktopStore(path).Load();
            Assert.Single(absent.Desktops);
            Assert.Equal("Desktop 1", absent.Desktops[0].Name);
            Assert.Null(absent.Problem);

            File.WriteAllText(path, "{ \"schemaVersion\": 1, \"desktops\": [] }");
            var invalid = new DesktopStore(path).Load();
            Assert.Single(invalid.Desktops);
            Assert.Contains("desktops.json", invalid.Problem);
            Assert.Equal("{ \"schemaVersion\": 1, \"desktops\": [] }", File.ReadAllText(path));

            File.WriteAllText(path, "not json at all");
            Assert.NotNull(new DesktopStore(path).Load().Problem);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task S26_desktops_json_round_trips_and_an_invalid_file_is_kept_beside_the_first_save()
    {
        var directory = Path.Combine(Path.GetTempPath(), "szd-desktops-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var path = Path.Combine(directory, "desktops.json");
            File.WriteAllText(path, "broken");
            var store = new DesktopStore(path);
            var loaded = store.Load();
            Assert.NotNull(loaded.Problem);

            var two = new DesktopInfo(Guid.NewGuid(), null);
            var saved = await store.SaveAsync([loaded.Desktops[0], two], two.Id, Ct);

            Assert.True(saved.Ok);
            Assert.Equal("broken", File.ReadAllText(path + ".invalid"));
            var back = new DesktopStore(path).Load();
            Assert.Null(back.Problem);
            Assert.Equal([loaded.Desktops[0].Id, two.Id], back.Desktops.Select(d => d.Id));
            Assert.Null(back.Desktops[1].Name);
            Assert.Equal(two.Id, back.LastCurrent);
            Assert.False(File.Exists(path + ".tmp"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static void AssertEveryWindowOnExactlyOneSide(WindowSnapshot snapshot)
    {
        var current = snapshot.DesktopsOrDefault.Current;
        foreach (var window in snapshot.Windows.Where(w => !w.PinnedToAll))
        {
            Assert.Equal(window.DesktopId == current, window.Visible);
        }
    }

    [Fact]
    public async Task S26_4_a_window_that_will_not_show_stays_hidden_and_in_the_journal()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
        world.Effects.NotShow.Add(2);

        var switched = await world.Pump(world.Set.SwitchAsync(second, Ct));

        Assert.True(switched.Ok);
        var window = world.Model.Current.Windows.Single(w => w.Key.Handle == 2);
        Assert.True(window.ShellHidden);
        Assert.False(window.Visible);
        Assert.Contains(2, world.Journal.Handles());
    }

    [Fact]
    public async Task S26_5_a_failed_show_on_exit_keeps_the_journal_naming_the_hidden_windows()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
        world.Effects.ShowAllFails = true;

        await world.Pump(world.Set.RestoreAllAsync());

        Assert.Equal([2], world.Journal.Handles());
        Assert.True(world.Model.Current.Windows.Single(w => w.Key.Handle == 2).ShellHidden);
        Assert.Single(world.Diagnostics);
    }

    [Fact]
    public async Task S26_4_a_gone_window_is_skipped_for_the_next_most_recent_one()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        world.Put(3, "C");
        world.Put(4, "D");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(3), second, Ct));
        await world.Pump(world.Set.MoveAsync(world.KeyOf(4), second, Ct));
        world.Foreground.GoneHandles.Add(3);
        world.Foreground.GoneHandles.Add(4);

        var switched = await world.Pump(world.Set.SwitchAsync(second, Ct));

        // Whichever is tried first reports gone; the other is tried next rather than the search stopping.
        Assert.True(switched.Ok);
        Assert.Equal([3, 4], world.Foreground.Activated.Select(h => (int)h).Order());
    }

    [Fact]
    public async Task S26_6_activating_a_window_on_another_desktop_switches_to_it_first()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var first = world.Set.CurrentId;
        var second = world.Set.Create(null).Value;
        await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));
        world.Foreground.Activated.Clear();

        var onThisDesktop = await world.Pump(WindowCommands.ActivateAsync(world.Model, world.Foreground,
            new JsonObject { ["windowKey"] = Ref(world.KeyOf(1)) }, Ct, world.Set.RevealAsync));
        Assert.True(onThisDesktop.Ok);
        Assert.Equal(first, world.Set.CurrentId);

        var onAnother = await world.Pump(WindowCommands.ActivateAsync(world.Model, world.Foreground,
            new JsonObject { ["windowKey"] = Ref(world.KeyOf(2)) }, Ct, world.Set.RevealAsync));

        Assert.True(onAnother.Ok);
        Assert.Equal(second, world.Set.CurrentId);
        Assert.True(world.Model.Current.Windows.Single(w => w.Key.Handle == 2).Visible);
        Assert.Equal((nint)2, world.Foreground.Activated.Last());
    }

    [Fact]
    public async Task S26_3_a_hide_that_lands_before_the_effects_fail_is_undone()
    {
        using var world = new World();
        world.Put(1, "A");
        world.Put(2, "B");
        await world.StartAsync();
        var second = world.Set.Create(null).Value;
        world.Effects.HideThenFail = true;

        var moved = await world.Pump(world.Set.MoveAsync(world.KeyOf(2), second, Ct));

        Assert.False(moved.Ok);
        var window = world.Model.Current.Windows.Single(w => w.Key.Handle == 2);
        Assert.True(window.Visible);
        Assert.False(window.ShellHidden);
        Assert.Equal(world.Set.CurrentId, window.DesktopId);
        Assert.Empty(world.Journal.Handles());
    }

    private static string Ref(WindowKey key) => new WindowReference(Epoch, key.Generation).ToString();

    private sealed class World : IDisposable
    {
        private readonly string _directory;

        public World(string? storePath = null, long epoch = Epoch)
        {
            _directory = storePath is null ? Path.Combine(Path.GetTempPath(), "szd-desktops-" + Guid.NewGuid().ToString("N")) : Path.GetDirectoryName(storePath)!;
            StorePath = storePath ?? Path.Combine(_directory, "desktops.json");
            Store = new DesktopStore(StorePath);
            Model = new WindowModel(Facts, Poster, epoch);
            Model.Changed += (_, snapshot) => Changes.Add(snapshot);
            Effects = new FakeEffects(Facts, Order);
            Journal = new FakeJournal(Order);
            Set = new DesktopSet(Model, Effects, Journal, Foreground, Store, Diagnostics.Add);
            Tracker = new FakeTracker(Facts);
            Service = new WindowService(Model, Tracker, (id, node) => Feeds.Add((id, node)));
        }

        public string StorePath { get; }

        public bool KeepStore { get; set; }

        public DesktopStore Store { get; }

        public FakeFacts Facts { get; } = new();

        public FakePoster Poster { get; } = new();

        public WindowModel Model { get; }

        public FakeTracker Tracker { get; }

        public WindowService Service { get; }

        public FakeEffects Effects { get; }

        public FakeJournal Journal { get; }

        public FakeForeground Foreground { get; } = new();

        public DesktopSet Set { get; }

        public List<string> Order { get; } = [];

        public List<WindowSnapshot> Changes { get; } = [];

        public List<(string Id, JsonNode? Node)> Feeds { get; } = [];

        public List<CoreError> Diagnostics { get; } = [];

        public void Put(int handle, string title) =>
            Facts.Items[handle] = new WindowFacts(new WindowFingerprint(handle, handle * 10L, 1, "Cls"), title, null, null, 0, "\\\\.\\DISPLAY1",
                true, CloakState.None, false, false, false, false, false, false);

        public async Task StartAsync()
        {
            Tracker.Handles = [.. Facts.Items.Keys.Select(k => (nint)k)];
            var started = Service.StartAsync(Ct);
            Poster.Drain();
            await started;
        }

        public WindowKey KeyOf(int handle) => Model.Current.Windows.Single(w => w.Key.Handle == handle).Key;

        public int[] Handles() => [.. Model.Current.Windows.Select(w => (int)w.Key.Handle)];

        /// <summary>Waits for a task while serving the fake UI queue, as the dispatcher would.</summary>
        public async Task<T> Pump<T>(Task<T> task)
        {
            await Pump((Task)task);
            return await task;
        }

        public async Task Pump(Task task)
        {
            var guard = System.Diagnostics.Stopwatch.StartNew();
            while (!task.IsCompleted)
            {
                Poster.Drain();
                await Task.Delay(1, Ct);
                Assert.True(guard.Elapsed < TimeSpan.FromSeconds(10), "The operation did not finish.");
            }

            Poster.Drain();
            await task;
        }

        public void Dispose()
        {
            Set.Dispose();
            if (!KeepStore && Directory.Exists(_directory))
            {
                try
                {
                    Directory.Delete(_directory, recursive: true);
                }
                catch (IOException)
                {
                }
            }
        }
    }

    private sealed class FakeEffects(FakeFacts facts, List<string> order) : IWindowEffects
    {
        public List<SwitchPlan> Plans { get; } = [];

        public HashSet<int> Refuse { get; } = [];

        public List<nint> Closed { get; } = [];

        public Action<SwitchPlan>? Before { get; set; }

        public TaskCompletionSource? Gate { get; set; }

        public int ShowAllCalls { get; private set; }

        /// <summary>Windows a show is asked for and fails on: they stay hidden.</summary>
        public HashSet<int> NotShow { get; } = [];

        /// <summary>The next plan hides what it names and then reports a failure, as a hide that landed before the thread faulted.</summary>
        public bool HideThenFail { get; set; }

        public bool ShowAllFails { get; set; }

        public async Task<Outcome<EffectsResult>> ApplyAsync(SwitchPlan plan, CancellationToken ct)
        {
            Plans.Add(plan);
            Before?.Invoke(plan);
            if (Gate is { } gate)
            {
                await gate.Task;
            }

            var stuck = new List<WindowKey>();
            var gone = new List<WindowKey>();
            var notShown = new List<WindowKey>();
            if (HideThenFail)
            {
                HideThenFail = false;
                foreach (var planned in plan.Hide)
                {
                    facts.Items[(int)planned.Key.Handle] = facts.Items[(int)planned.Key.Handle] with { Visible = false };
                }

                return Outcome<EffectsResult>.Fail(CoreErrorCode.CommandFailed, "The effects thread faulted.");
            }

            foreach (var planned in plan.Hide)
            {
                if (!Matches(planned))
                {
                    gone.Add(planned.Key);
                }
                else if (Refuse.Contains((int)planned.Key.Handle))
                {
                    stuck.Add(planned.Key);
                }
                else
                {
                    order.Add("hide:" + planned.Key.Handle);
                    facts.Items[(int)planned.Key.Handle] = facts.Items[(int)planned.Key.Handle] with { Visible = false };
                }
            }

            foreach (var planned in plan.Show)
            {
                if (!Matches(planned))
                {
                    gone.Add(planned.Key);
                }
                else if (NotShow.Contains((int)planned.Key.Handle))
                {
                    notShown.Add(planned.Key);
                }
                else
                {
                    order.Add("show:" + planned.Key.Handle);
                    facts.Items[(int)planned.Key.Handle] = facts.Items[(int)planned.Key.Handle] with { Visible = true };
                }
            }

            return Outcome<EffectsResult>.Success(new EffectsResult(stuck, gone) { NotShown = notShown });
        }

        public Task<Outcome> ShowAllAsync(CancellationToken ct)
        {
            ShowAllCalls++;
            if (ShowAllFails)
            {
                return Task.FromResult(Outcome.Fail(CoreErrorCode.CommandFailed, "One window would not show."));
            }

            foreach (var handle in facts.Items.Keys.ToList())
            {
                facts.Items[handle] = facts.Items[handle] with { Visible = true };
            }

            return Task.FromResult(Outcome.Success);
        }

        public Task<Outcome> CloseAsync(PlannedWindow window, CancellationToken ct)
        {
            Closed.Add(window.Key.Handle);
            return Task.FromResult(Outcome.Success);
        }

        private bool Matches(PlannedWindow planned) =>
            facts.Items.TryGetValue((int)planned.Key.Handle, out var f) && f.Fingerprint == planned.Fingerprint;
    }

    private sealed class FakeJournal(List<string> order) : IRecoveryJournal
    {
        public List<IReadOnlyList<Recovery.HiddenWindow>> Writes { get; } = [];

        public IReadOnlyList<Recovery.HiddenWindow> Entry { get; private set; } = [];

        public bool Fail { get; set; }

        public Action? OnWrite { get; set; }

        public List<int> Handles() => [.. Entry.Select(h => (int)h.Handle)];

        public Task<Outcome<Guid>> BeginAsync(Recovery.DesktopChange change, CancellationToken ct) =>
            Task.FromResult(Outcome<Guid>.Success(Guid.NewGuid()));

        public Task<Outcome> EndAsync(Guid changeId, CancellationToken ct) => Task.FromResult(Outcome.Success);

        public Task<Outcome> ReplaceAsync(Recovery.DesktopChangeKind kind, Recovery.DesktopChange? change, CancellationToken ct)
        {
            if (Fail)
            {
                return Task.FromResult(Outcome.Fail(CoreErrorCode.IoFailure, "The disk is full.", retryable: true));
            }

            order.Add("journal");
            Entry = change is null ? [] : Recovery.HiddenWindowRestorer.Windows(change.Payload);
            Writes.Add(Entry);
            OnWrite?.Invoke();
            return Task.FromResult(Outcome.Success);
        }

        public Task<Outcome> MarkCleanExitAsync(CancellationToken ct) => Task.FromResult(Outcome.Success);
    }

    private sealed class FakeShowPort : IDictionaryLike, Recovery.IWindowShowPort
    {
        private readonly Dictionary<nint, Recovery.WindowProbe> _probes = [];

        public List<nint> Shown { get; } = [];

        public Recovery.WindowProbe this[int handle]
        {
            set => _probes[handle] = value;
        }

        public Recovery.WindowProbe? Probe(nint handle) => _probes.TryGetValue(handle, out var probe) ? probe : null;

        public bool ShowInPlace(nint handle)
        {
            Shown.Add(handle);
            return true;
        }
    }

    private interface IDictionaryLike;

    private sealed class FakeFacts : IWindowFacts
    {
        public Dictionary<int, WindowFacts> Items { get; } = [];

        public void Remove(int handle) => Items.Remove(handle);

        public WindowFacts? Read(nint handle) => Items.GetValueOrDefault((int)handle);
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

        public Task<IReadOnlyList<nint>> EnumerateAsync(CancellationToken ct) => Task.FromResult(Handles);

        public WindowFingerprint? Capture(nint handle) => facts.Items.TryGetValue((int)handle, out var f) ? f.Fingerprint : null;

        public void Start(Action<WindowObservation> sink)
        {
        }

        public void Stop()
        {
        }
    }

    private sealed class FakeForeground : IForegroundService
    {
        public List<nint> Activated { get; } = [];

        public CoreErrorCode? Fail { get; set; }

        /// <summary>Handles that report WindowGone when activated, while the model still lists them.</summary>
        public HashSet<int> GoneHandles { get; } = [];

        public void AllowForeground(int processId)
        {
        }

        public Task<Outcome> ActivateWindowAsync(nint hwnd, CancellationToken ct)
        {
            Activated.Add(hwnd);
            if (GoneHandles.Contains((int)hwnd))
            {
                return Task.FromResult(Outcome.Fail(CoreErrorCode.WindowGone, "gone"));
            }

            return Task.FromResult(Fail is { } code ? Outcome.Fail(code, "failed") : Outcome.Success);
        }

        public Task<Outcome> MinimizeWindowAsync(nint hwnd, CancellationToken ct) => Task.FromResult(Outcome.Success);

        public Task<Outcome<ActivationResult>> ActivateConfirmedAsync(nint hwnd, CancellationToken ct)
        {
            Activated.Add(hwnd);
            return Task.FromResult(Outcome<ActivationResult>.Success(ActivationResult.Foreground));
        }
    }
}

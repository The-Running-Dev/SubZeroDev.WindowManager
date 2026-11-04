using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S26: the shell's own virtual desktops. Every window here is a test-hook synthetic one and the hook's window
/// effects hide and show only those (S26.10, I38), so no test hides, shows or moves a window it did not create. Keys are
/// injected with the test marker only once the status shows the hook installed.
/// </summary>
public sealed class VirtualDesktopEndToEndTests
{
    private const string Desktops = "subzerodev.desktops";
    private const string Launcher = "subzerodev.launcher";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S26_1_2_3_a_window_moved_to_another_desktop_is_hidden_stays_in_the_model_and_returns_on_the_switch_back()
    {
        HostRun.RequireDesktop();
        using var run = Start(withPlugin: true);
        var status = await run.WaitForAsync(Ready, Startup);
        var first = Guid.Parse((string)status["desktops"]!["current"]!);

        await SyntheticAsync(run, "add", 3);
        status = await run.WaitForAsync(s => (int?)s["windowModel"]!["synthetic"] == 3
            && s["windowModel"]!["firstSynthetic"]!.AsArray().Count == 3, Startup);
        var windows = (int)status["windowModel"]!["windows"]!;
        var key = (string)status["windowModel"]!["firstSynthetic"]![0]!["key"]!;

        var created = await InvokeAsync(run, "host.desktops.create", new JsonObject { ["switchTo"] = false });
        Assert.True((bool)created["ok"]!, created.ToJsonString());
        status = await run.WaitForAsync(s => (int)s["desktops"]!["count"]! == 2, Reaction);
        var second = status["desktops"]!["ids"]!.AsArray().Select(i => Guid.Parse((string)i!)).Single(id => id != first);

        // A bad target is refused before anything is filed or hidden.
        var refused = await InvokeAsync(run, "host.window.moveToDesktop", new JsonObject { ["windowKey"] = key, ["id"] = Guid.NewGuid().ToString("D") });
        Assert.False((bool)refused["ok"]!, refused.ToJsonString());
        status = await run.WaitForStatusAsync();
        Assert.Equal(0, (int)status["windowModel"]!["syntheticHidden"]!);

        var moved = await InvokeAsync(run, "host.window.moveToDesktop", new JsonObject { ["windowKey"] = key, ["id"] = second.ToString("D") });
        Assert.True((bool)moved["ok"]!, moved.ToJsonString());
        status = await run.WaitForAsync(s => (int)s["windowModel"]!["syntheticHidden"]! == 1, Reaction);

        // Hidden for another desktop is not gone: still in the model, filed on the second desktop, marked shell-hidden (I40).
        Assert.Equal(windows, (int)status["windowModel"]!["windows"]!);
        var entry = status["windowModel"]!["firstSynthetic"]!.AsArray().Single(w => (string?)w!["key"] == key)!;
        Assert.Equal(second.ToString("D"), (string?)entry["desktop"]);
        Assert.True((bool)entry["shellHidden"]!);

        // Switching over swaps the two sets; switching back restores them.
        var switched = await InvokeAsync(run, "host.desktops.switch", new JsonObject { ["id"] = second.ToString("D") });
        Assert.True((bool)switched["ok"]!, switched.ToJsonString());
        status = await run.WaitForAsync(s => (string?)s["desktops"]!["current"] == second.ToString("D")
            && (int)s["windowModel"]!["syntheticHidden"]! == 2, Reaction);
        Assert.Equal(windows, (int)status["windowModel"]!["windows"]!);

        await InvokeAsync(run, "host.desktops.switch", new JsonObject { ["id"] = first.ToString("D") });
        status = await run.WaitForAsync(s => (string?)s["desktops"]!["current"] == first.ToString("D")
            && (int)s["windowModel"]!["syntheticHidden"]! == 1, Reaction);

        // A window the application closes while it is hidden leaves the model within two seconds.
        await SyntheticAsync(run, "close", 1);
        await run.WaitForAsync(s => (int)s["windowModel"]!["windows"]! == windows - 1
            && s["windowModel"]!["firstSynthetic"]!.AsArray().All(w => (string?)w!["key"] != key), TimeSpan.FromSeconds(2));
        Assert.Null(((await run.WaitForStatusAsync())["desktops"]!["problem"]));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S26_4_a_window_that_will_not_hide_follows_the_user_and_is_never_taken_for_gone()
    {
        HostRun.RequireDesktop();
        using var run = Start(withPlugin: false);
        await run.WaitForAsync(Ready, Startup);
        await SyntheticAsync(run, "add", 3);
        var status = await run.WaitForAsync(s => (int?)s["windowModel"]!["synthetic"] == 3, Startup);
        var windows = (int)status["windowModel"]!["windows"]!;
        await SyntheticAsync(run, "refuse", 1);

        await InvokeAsync(run, "host.desktops.create", new JsonObject { ["switchTo"] = true });
        status = await run.WaitForAsync(s => (int)s["desktops"]!["count"]! == 2
            && (int)s["windowModel"]!["syntheticHidden"]! == 2, Reaction);

        // The refusing window stayed shown and was kept in the model; the two that agreed to hide are hidden.
        Assert.Equal(windows, (int)status["windowModel"]!["windows"]!);
        Assert.Null(status["desktops"]!["problem"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S26_6_the_desktop_chords_work_without_the_plugin_and_win_tab_waits_for_it()
    {
        HostRun.RequireDesktop();
        using var without = Start(withPlugin: false);
        var status = await without.WaitForAsync(Ready, Startup);
        var first = (string)status["desktops"]!["current"]!;

        // Win+Ctrl+D creates a desktop and switches to it; Win+Ctrl+Left goes back. Both are host commands.
        Interactive.PressMarked(Interactive.LeftWin, Interactive.Control, 'D');
        status = await without.WaitForAsync(s => (int)s["desktops"]!["count"]! == 2 && (string?)s["desktops"]!["current"] != first, Reaction);
        Interactive.PressMarked(Interactive.LeftWin, Interactive.Control, Interactive.Left);
        await without.WaitForAsync(s => (string?)s["desktops"]!["current"] == first, Reaction);

        // With no desktops plugin running, Win+Tab is not swallowed and nothing is dispatched for it (I35).
        var events = (long)status["keyHook"]!["events"]!;
        Interactive.PressMarked(Interactive.LeftWin, Interactive.Tab);
        status = await without.WaitForAsync(s => (long)s["keyHook"]!["events"]! >= events + 4, Reaction);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        status = await without.WaitForStatusAsync();
        Assert.DoesNotContain(status["dispatches"]!.AsArray(), d => ((string?)d)!.Contains("desktops.openOverview", StringComparison.Ordinal));
        Assert.Equal(0, await without.StopAsync());

        // With it running, the same chord reaches its command.
        using var with = Start(withPlugin: true);
        await with.WaitForAsync(Ready, Startup);
        Interactive.PressMarked(Interactive.LeftWin, Interactive.Tab);
        await with.WaitForAsync(s => s["dispatches"]!.AsArray().Any(d => ((string?)d)!.Contains("desktops.openOverview", StringComparison.Ordinal)), Reaction);
        Assert.Equal(0, await with.StopAsync());
    }

    [Fact]
    public async Task S26_10_switching_a_hundred_windows_is_quick_and_touches_only_the_windows_the_test_made()
    {
        HostRun.RequireDesktop();
        using var run = Start(withPlugin: false);
        await run.WaitForAsync(Ready, Startup);

        // Cloaked, so the model tracks and publishes them and no strip draws a hundred buttons.
        File.WriteAllText(Path.Combine(run.HookDirectory, "synthetic-windows"),
            new JsonObject { ["op"] = "add", ["count"] = 100, ["strip"] = false }.ToJsonString());
        await run.WaitForAsync(s => (int?)s["windowModel"]!["synthetic"] == 100, Startup);

        var timer = Stopwatch.StartNew();
        await InvokeAsync(run, "host.desktops.create", new JsonObject { ["switchTo"] = true });
        var status = await run.WaitForAsync(s => (int)s["windowModel"]!["syntheticHidden"]! == 100
            && (int)s["desktops"]!["count"]! == 2, TimeSpan.FromSeconds(10));
        timer.Stop();

        Assert.True(timer.Elapsed < TimeSpan.FromSeconds(5), $"Hiding a hundred windows took {timer.Elapsed.TotalMilliseconds:F0} ms.");
        Assert.Null(status["desktops"]!["problem"]);
        Assert.Equal(0, await run.StopAsync());
    }

    private static HostRun Start(bool withPlugin)
    {
        var run = HostRun.Create();
        if (withPlugin)
        {
            run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"), (Guid.NewGuid(), Desktops, "desktops"));
        }
        else
        {
            run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"));
        }

        run.Start(HostRun.HostExe, "--mode", "replacement");
        return run;
    }

    private static bool Ready(JsonObject status) =>
        (bool?)status["ready"] == true
        && (bool?)status["keyHook"]?["installed"] == true
        && status["desktops"] is JsonObject
        && status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == Launcher && (string?)p["state"] == "Running");

    // The hook takes one file at a time and runs an invoke before a synthetic-window request in the same tick, so a request
    // must be taken before the next step is sent.
    private static async Task SyntheticAsync(HostRun run, string op, int count)
    {
        var path = Path.Combine(run.HookDirectory, "synthetic-windows");
        File.WriteAllText(path, new JsonObject { ["op"] = op, ["count"] = count }.ToJsonString());
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (File.Exists(path))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the hook to take the synthetic-windows request.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        // The request is then queued to the UI thread; the status shows its effect only after that has run.
        await Task.Delay(300, TestContext.Current.CancellationToken);
    }

    private static async Task<JsonObject> InvokeAsync(HostRun run, string command, JsonObject args)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (File.Exists(Path.Combine(run.HookDirectory, "invoke")))
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for the previous invoke to be taken.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        var before = (await run.WaitForStatusAsync())["lastInvoke"]?.ToJsonString();
        File.WriteAllText(Path.Combine(run.HookDirectory, "invoke"), $"{command}\nkeyboard\n{args.ToJsonString()}\n");
        var status = await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == command
            && last.ToJsonString() != before, Reaction);
        return status["lastInvoke"]!.AsObject();
    }
}

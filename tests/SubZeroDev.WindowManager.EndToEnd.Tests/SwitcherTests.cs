using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S31: the optional themed Alt+Tab. Every window here is a test-hook synthetic one (invariant I38) and the keys are
/// injected with the test marker once the status shows the hook installed, so nothing reaches Windows or another program.
/// </summary>
public sealed class SwitcherTests
{
    private const string Switcher = "subzerodev.switcher";
    private const string Launcher = "subzerodev.launcher";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S31_1_alt_tab_opens_the_switcher_on_the_second_window_steps_on_tab_and_switches_when_alt_is_released()
    {
        HostRun.RequireDesktop();
        using var run = Start(withPlugin: true);
        await run.WaitForAsync(s => Ready(s, withPlugin: true), Startup);
        await SyntheticAsync(run, "add", 3);
        await run.WaitForAsync(s => (int?)s["windowModel"]!["synthetic"] == 3, Startup);

        Interactive.HoldMarked(Interactive.Alt, Interactive.Tab);
        Interactive.ReleaseMarked(Interactive.Tab);
        var status = await run.WaitForAsync(s => Items(s).Count >= 3 && Menu(s)["open"]?.GetValue<bool>() == true, Reaction);
        var entries = Items(status);

        // The first step selects the second entry: the window before the one in front.
        Assert.Contains(Dispatches(status), d => d.StartsWith("send switcher.step ", StringComparison.Ordinal) && d.EndsWith(" Hotkey", StringComparison.Ordinal));
        Assert.Equal([false, true, false], entries.Take(3).Select(e => e["active"]?.GetValue<bool>() == true));

        // A second Tab moves on; Shift+Tab moves back.
        Interactive.HoldMarked(Interactive.Tab);
        Interactive.ReleaseMarked(Interactive.Tab);
        status = await run.WaitForAsync(s => Items(s).Count >= 3 && Items(s)[2]["active"]?.GetValue<bool>() == true, Reaction);
        var target = (string)Items(status)[2]["itemKey"]!;

        // Releasing Alt switches to the chosen window and closes the switcher.
        Interactive.ReleaseMarked(Interactive.Alt);
        status = await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() != true
            && Active(s, target), Reaction);
        Assert.Contains(Dispatches(status), d => d.StartsWith("send switcher.commit ", StringComparison.Ordinal));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S31_1_escape_cancels_and_releasing_alt_afterwards_switches_nothing()
    {
        HostRun.RequireDesktop();
        using var run = Start(withPlugin: true);
        await run.WaitForAsync(s => Ready(s, withPlugin: true), Startup);
        await SyntheticAsync(run, "add", 3);
        await run.WaitForAsync(s => (int?)s["windowModel"]!["synthetic"] == 3, Startup);

        Interactive.HoldMarked(Interactive.Alt, Interactive.Tab);
        Interactive.ReleaseMarked(Interactive.Tab);
        await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() == true && Items(s).Count >= 3, Reaction);

        Interactive.PressMarked(Interactive.Escape);
        await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() != true, Reaction);
        Interactive.ReleaseMarked(Interactive.Alt);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        var status = await run.WaitForStatusAsync();
        Assert.Contains(Dispatches(status), d => d.StartsWith("send switcher.cancel ", StringComparison.Ordinal));
        Assert.DoesNotContain(Dispatches(status), d => d.StartsWith("send switcher.commit ", StringComparison.Ordinal));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S31_2_without_the_switcher_alt_tab_is_not_taken_and_nothing_is_dispatched()
    {
        HostRun.RequireDesktop();
        using var run = Start(withPlugin: false);
        var status = await run.WaitForAsync(s => Ready(s, withPlugin: false), Startup);

        var events = (long)status["keyHook"]!["events"]!;
        Interactive.PressMarked(Interactive.Alt, Interactive.Tab);
        await run.WaitForAsync(s => (long)s["keyHook"]!["events"]! >= events + 4, Reaction);
        await Task.Delay(500, TestContext.Current.CancellationToken);

        status = await run.WaitForStatusAsync();
        Assert.DoesNotContain(Dispatches(status), d => d.Contains("switcher.", StringComparison.Ordinal));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public void S31_1_the_shell_layout_places_the_switcher()
    {
        var layout = Path.Combine(Path.GetDirectoryName(HostRun.HostExe)!, "content", "layouts", "subzerodev.shell.json");
        var placed = JsonNode.Parse(File.ReadAllText(layout))!["panels"]!.AsArray()
            .SelectMany(panel => panel!["zones"]!.AsObject().SelectMany(zone => zone.Value!.AsArray()))
            .Any(item => (string?)item!["plugin"] == Switcher && (string?)item["contribution"] == "switcher");
        Assert.True(placed, "The default layout does not place subzerodev.switcher.");
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private static HostRun Start(bool withPlugin)
    {
        var run = HostRun.Create();
        if (withPlugin)
        {
            run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"), (Guid.NewGuid(), Switcher, "switcher"));
        }
        else
        {
            run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"));
        }

        run.Start(HostRun.HostExe, "--mode", "replacement");
        return run;
    }

    /// <summary>Ready, with the hook installed and (when wanted) the plugin running: only then may a test inject keys.</summary>
    private static bool Ready(JsonObject status, bool withPlugin) =>
        (bool?)status["ready"] == true
        && (bool?)status["keyHook"]?["installed"] == true
        && status["windowModel"] is JsonObject
        && status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == Launcher && (string?)p["state"] == "Running")
        && (!withPlugin || status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == Switcher && (string?)p["state"] == "Running"));

    private static bool Active(JsonObject status, string key) =>
        status["windowModel"]!["firstSynthetic"]!.AsArray().Any(w => (string?)w!["key"] == key && (bool?)w["active"] == true);

    private static List<string> Dispatches(JsonObject status) => [.. status["dispatches"]!.AsArray().Select(d => (string)d!)];

    private static JsonObject Menu(JsonObject status) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == Switcher && (string?)p["popup"] == "switcher")?.AsObject() ?? [];

    private static List<JsonObject> Items(JsonObject status) =>
        Menu(status)["items"]?.AsArray().Select(i => i!.AsObject()).ToList() ?? [];

    // The hook takes one file at a time, so a request must be taken before the next step is sent.
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

        await Task.Delay(300, TestContext.Current.CancellationToken);
    }
}

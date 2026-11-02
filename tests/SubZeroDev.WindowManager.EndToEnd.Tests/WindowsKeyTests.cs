using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S21: the Windows key, the session commands and the power menu. Under the test hook the Windows-key hook
/// acts only on keys a test injects with its marker and swallows every one of them, so no key reaches Windows;
/// keys are injected only once the status shows the hook installed. The session commands are recorded in
/// <c>desktop-actions.log</c> instead of done, and the hook file <c>session-refuse</c> refuses the ones it names.
/// </summary>
public sealed class WindowsKeyTests
{
    private const string Launcher = "subzerodev.launcher";
    private const string Session = "subzerodev.session";
    private const string ThisPc = "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S21_1_the_lone_windows_key_and_ctrl_esc_open_the_launcher_and_an_unbound_chord_does_nothing()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        await run.WaitForAsync(KeysReady, Startup);

        Interactive.PressMarked(Interactive.LeftWin);
        await run.WaitForAsync(s => LauncherOpens(s) == 1, Reaction);

        Interactive.PressMarked(Interactive.Control, Interactive.Escape);
        await run.WaitForAsync(s => LauncherOpens(s) == 2, Reaction);

        var before = (long)(await run.WaitForStatusAsync())["keyHook"]!["events"]!;
        Interactive.PressMarked(Interactive.LeftWin, 'X');
        var status = await run.WaitForAsync(s => (long)s["keyHook"]!["events"]! >= before + 4, Reaction);
        await Task.Delay(500, TestContext.Current.CancellationToken);
        status = await run.WaitForStatusAsync();
        Assert.Equal(2, LauncherOpens(status));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S21_2_the_bound_windows_shortcuts_reach_their_commands()
    {
        HostRun.RequireDesktop();
        using var fixture = RunningWindowsTests.FixtureWindows.Open();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"), (Guid.NewGuid(), Session, "power"));
        Hook(run, "desktop-show-pids", Interactive.ProcessOf(fixture["normal"]) + "\n");
        run.Start(HostRun.HostExe, "--mode", "replacement");
        await run.WaitForAsync(KeysReady, Startup);

        Interactive.PressMarked(Interactive.LeftWin, 'R');
        await run.WaitForAsync(s => LauncherOpens(s) == 1, Reaction);

        Interactive.PressMarked(Interactive.LeftWin, 'E');
        await WaitUntilAsync(() => DesktopActions(run).Contains($"files-open {ThisPc}"), "Win+E to open This PC");

        Interactive.PressMarked(Interactive.LeftWin, 'M');
        string[] shown = ["normal", "plain"];
        await WaitUntilAsync(() => shown.All(k => Interactive.IsMinimised(fixture[k])), "Win+M to minimise the fixture windows");
        foreach (var kind in shown)
        {
            Assert.Contains($"desktop-minimize-all 0x{fixture[kind]:X}", DesktopActions(run));
        }

        // Win+D with every window already minimised has nothing to act on; the dispatch is what counts here.
        Interactive.PressMarked(Interactive.LeftWin, 'D');
        var status = await run.WaitForAsync(s => Dispatches(s).Contains("host host.desktop.show Hotkey"), Reaction);
        Assert.Contains("host host.files.open Hotkey", Dispatches(status));
        Assert.Contains("host host.desktop.minimizeAll Hotkey", Dispatches(status));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S21_3_the_hook_answers_while_the_ui_thread_is_busy()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        await run.WaitForAsync(KeysReady, Startup);
        var statusFile = Path.Combine(run.HookDirectory, "status.json");

        Hook(run, "block", "4000\n");
        await WaitUntilAsync(() => !File.Exists(Path.Combine(run.HookDirectory, "block")), "the block to be taken");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        var frozen = File.GetLastWriteTimeUtc(statusFile);

        var pressed = Stopwatch.StartNew();
        Interactive.PressMarked(Interactive.LeftWin, 'E');
        await WaitUntilAsync(() => DesktopActions(run).Contains($"files-open {ThisPc}"), "Win+E while the UI thread is busy");
        var took = pressed.Elapsed;

        // The status is written from the UI thread, so an unchanged file shows it was still asleep.
        Assert.Equal(frozen, File.GetLastWriteTimeUtc(statusFile));
        Assert.True(took < TimeSpan.FromSeconds(1), $"Win+E took {took.TotalMilliseconds:F0} ms with the UI thread busy.");

        var status = await run.WaitForAsync(s => (long)s["keyHook"]!["events"]! >= 4, TimeSpan.FromSeconds(10));
        Assert.InRange((double)status["keyHook"]!["maxAnswerMs"]!, 0, 50);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S21_4_the_session_commands_run_and_a_refusal_is_reported()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        await run.WaitForAsync(KeysReady, Startup);

        foreach (var (command, line) in new[]
        {
            ("host.session.lock", "session-lock"),
            ("host.session.sleep", "session-sleep"),
            ("host.session.signOut", "session-signOut"),
            ("host.session.restart", "session-restart"),
            ("host.session.shutdown", "session-shutdown"),
        })
        {
            var status = await InvokeAsync(run, command);
            Assert.True((bool)status["lastInvoke"]!["ok"]!, status["lastInvoke"]!.ToJsonString());
            Assert.Contains(line, DesktopActions(run));
        }

        Hook(run, "session-refuse", "shutdown\n");
        var refused = await InvokeAsync(run, "host.session.shutdown");
        Assert.False((bool)refused["lastInvoke"]!["ok"]!);
        Assert.Equal("SessionCommandRefused", (string?)refused["lastInvoke"]!["code"]);
        Assert.Contains("session-shutdown refused", DesktopActions(run));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S21_5_the_power_menu_offers_five_actions_and_asks_before_ending_the_session()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Session, "power"), (Guid.NewGuid(), Launcher, "launcher"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        var status = await run.WaitForAsync(s => KeysReady(s) && PowerButton(s)?["bounds"] is JsonObject, Startup);

        // Open the menu from the keyboard: focus the panel, check the host holds the foreground, then press Enter.
        File.WriteAllText(Path.Combine(run.HookDirectory, "focus-panel"), (string)status["panels"]![0]!["id"]!);
        await run.WaitForAsync(s => (string?)s["panels"]![0]!["focused"] == "open", Reaction);
        Assert.Equal(HostPid(status), Interactive.ForegroundProcessId());
        Interactive.Press(Interactive.Enter);
        status = await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() == true && Items(s).Count == 5, Reaction);
        Assert.Equal(["lock", "sleep", "signOut", "restart", "shutdown"], Items(status).Select(i => (string?)i["id"]));
        Assert.Equal(["Lock", "Sleep", "Sign out", "Restart", "Shut down"], Items(status).Select(i => (string?)i["label"]));

        // Restart asks first; Cancel goes back to the menu and nothing happened.
        status = await ClickItemAsync(run, status, "restart", s => Items(s).Any(i => (string?)i["id"] == "confirm"));
        Assert.Contains(Texts(status), t => t.StartsWith("Restart now?", StringComparison.Ordinal));
        status = await ClickItemAsync(run, status, "cancel", s => Items(s).Count == 5);
        Assert.DoesNotContain(DesktopActions(run), line => line.StartsWith("session-", StringComparison.Ordinal));

        // A refused restart says so in the menu.
        Hook(run, "session-refuse", "restart\n");
        status = await ClickItemAsync(run, status, "restart", s => Items(s).Any(i => (string?)i["id"] == "confirm"));
        status = await ClickItemAsync(run, status, "confirm", s => Texts(s).Any(t => t.StartsWith("Restart did not happen", StringComparison.Ordinal)));
        Assert.Contains("session-restart refused", DesktopActions(run));

        // Lock acts at once and closes the menu.
        await ClickItemAsync(run, status, "lock", s => Menu(s)["open"]?.GetValue<bool>() != true);
        Assert.Contains("session-lock", DesktopActions(run));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S21_7_alongside_Explorer_there_is_no_windows_key_hook_and_no_power_menu()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"), (Guid.NewGuid(), Session, "power"));
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true
            && s["plugins"]!.AsArray().Any(p => (string?)p!["id"] == Launcher && (string?)p["state"] == "Running"), Startup);
        Assert.Null(status["keyHook"]);
        Assert.DoesNotContain(status["plugins"]!.AsArray(), p => (string?)p!["id"] == Session && (string?)p["state"] == "Running");
        Assert.Equal(0, await run.StopAsync());
    }

    private static HostRun Start()
    {
        var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"), (Guid.NewGuid(), Session, "power"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        return run;
    }

    /// <summary>Ready, with the hook installed and both plugins running: only then may a test inject keys.</summary>
    private static bool KeysReady(JsonObject status) =>
        (bool?)status["ready"] == true
        && (bool?)status["keyHook"]?["installed"] == true
        && new[] { Launcher, Session }.All(id =>
            status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == id && (string?)p["state"] == "Running"));

    private static List<string> Dispatches(JsonObject status) => [.. status["dispatches"]!.AsArray().Select(d => (string)d!)];

    private static int LauncherOpens(JsonObject status) =>
        Dispatches(status).Count(d => d.StartsWith("send launcher.open ", StringComparison.Ordinal) && d.EndsWith(" Hotkey", StringComparison.Ordinal));

    private static JsonObject? PowerButton(JsonObject status) =>
        status["contributions"]!.AsArray().FirstOrDefault(c => (string?)c!["plugin"] == Session)?["interactive"]?.AsArray()
            .FirstOrDefault(e => (string?)e!["id"] == "open")?.AsObject();

    private static int HostPid(JsonObject status) => Interactive.ProcessOf((nint)(long)status["panels"]![0]!["handle"]!);

    private static JsonObject Menu(JsonObject status) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == Session && (string?)p["popup"] == "menu")?.AsObject() ?? [];

    private static List<JsonObject> Items(JsonObject status) =>
        Menu(status)["items"]?.AsArray().Select(i => i!.AsObject()).ToList() ?? [];

    private static List<string> Texts(JsonObject status) =>
        Menu(status)["texts"]?.AsArray().Select(t => (string?)t ?? string.Empty).ToList() ?? [];

    /// <summary>Clicks a menu item, only once the menu itself is the window under the point.</summary>
    private static async Task<JsonObject> ClickItemAsync(HostRun run, JsonObject status, string id, Func<JsonObject, bool> then)
    {
        var bounds = Items(status).Single(i => (string?)i["id"] == id)["bounds"]!;
        var x = ((int)bounds["left"]! + (int)bounds["right"]!) / 2;
        var y = ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2;
        Assert.True(Interactive.IsWindowAt(x, y, (long)Menu(status)["hwnd"]!), $"The power menu is not under the {id} item.");
        Interactive.Click(x, y);
        return await run.WaitForAsync(then, Reaction);
    }

    private static async Task<JsonObject> InvokeAsync(HostRun run, string command)
    {
        await WaitUntilAsync(() => !File.Exists(Path.Combine(run.HookDirectory, "invoke")), "the previous invoke to be taken");
        var before = (await run.WaitForStatusAsync())["lastInvoke"]?.ToJsonString();
        Hook(run, "invoke", $"{command}\nkeyboard\n{{}}\n");
        return await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == command
            && last.ToJsonString() != before, Reaction);
    }

    private static string[] DesktopActions(HostRun run)
    {
        var path = Path.Combine(run.HookDirectory, "desktop-actions.log");
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static void Hook(HostRun run, string name, string content)
    {
        Directory.CreateDirectory(run.HookDirectory);
        File.WriteAllText(Path.Combine(run.HookDirectory, name), content);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for {what}.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

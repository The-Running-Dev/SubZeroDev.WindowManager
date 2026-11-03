using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S24: notifications. Under the test hook the plugin reads <c>notifications.json</c> in the hook directory rather
/// than the user's real notifications (invariant I38) and records what the shell does to <c>desktop-actions.log</c>;
/// the test hook status carries the notification feeds' snapshots, which only a test sees. The Windows-key hook acts
/// only on keys a test injects with its marker, so Win+N never reaches Windows; there is no negative case here for that
/// reason (an unswallowed Win+N would open the real notification centre), and the host tests pin it instead.
/// </summary>
public sealed partial class NotificationsTests
{
    private const string Notifications = "subzerodev.notifications";
    private const string Launcher = "subzerodev.launcher";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusCadence = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task S24_2_the_history_feed_lists_the_notifications_newest_first_and_follows_changes_without_writing_them_down()
    {
        HostRun.RequireDesktop();
        var title = $"title-{Guid.NewGuid():N}";
        var body = $"body-{Guid.NewGuid():N}";
        using var run = Start(Fake(NewItem("n1", "app.one", "One", "older", minutesAgo: 10), NewItem("n2", "app.two", "Two", title, body, minutesAgo: 1)));

        var status = await run.WaitForAsync(s => History(s)?.AsArray().Count == 2, Startup);
        var history = History(status)!.AsArray();
        Assert.Equal(["n2", "n1"], history.Select(i => (string?)i!["id"]));
        var newest = history[0]!;
        Assert.Equal("app.two", (string?)newest["appId"]);
        Assert.Equal("Two", (string?)newest["appName"]);
        Assert.Equal(title, (string?)newest["title"]);
        Assert.Equal(body, (string?)newest["body"]);
        Assert.False((bool?)newest["read"]);
        Assert.False((bool?)newest["silent"]);
        Assert.True(DateTimeOffset.TryParse((string?)newest["at"], out _));
        Assert.Equal(2, (int?)NotificationStatus(status)!["unread"]);

        var changed = Stopwatch.StartNew();
        WriteFake(run, Fake(NewItem("n3", "app.one", "One", "third", minutesAgo: 0), NewItem("n2", "app.two", "Two", title, body, minutesAgo: 1)));
        status = await run.WaitForAsync(s => History(s)?.AsArray().Count == 2 && (string?)History(s)![0]!["id"] == "n3", Reaction);
        Assert.True(changed.Elapsed < TimeSpan.FromMilliseconds(500) + StatusCadence,
            $"The history took {changed.Elapsed.TotalMilliseconds:F0} ms to show the change.");

        Assert.Equal(0, await run.StopAsync());

        // I31: no notification text reaches a file or a log.
        var leaks = Directory.EnumerateFiles(run.DataRoot, "*", SearchOption.AllDirectories)
            .Where(path => File.ReadAllText(path).Contains(title, StringComparison.Ordinal) || File.ReadAllText(path).Contains(body, StringComparison.Ordinal))
            .ToList();
        Assert.True(leaks.Count == 0, $"Notification text was written to {string.Join(", ", leaks)}.");
        var state = Path.Combine(run.DataRoot, "plugin-data", Notifications, "state.json");
        if (File.Exists(state))
        {
            Assert.DoesNotContain(title, File.ReadAllText(state), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task S24_3_a_new_notification_pops_up_without_taking_focus_stacks_to_the_limit_and_expires()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(NewItem("old", "app.one", "One", "already there", minutesAgo: 30)), Bell(popupSeconds: 3));
        await run.WaitForAsync(s => History(s)?.AsArray().Count == 1, Startup);

        // The history that was there at the start does not pop up.
        await Task.Delay(700, TestContext.Current.CancellationToken);
        Assert.NotEqual(true, (await run.WaitForStatusAsync())["popups"]!.AsArray().FirstOrDefault(p => IsToast(p!))?["open"]?.GetValue<bool>());

        WriteFake(run, Fake(NewItem("old", "app.one", "One", "already there", minutesAgo: 30), NewItem("a", "app.two", "Two", "fresh one", minutesAgo: 0)));
        var status = await run.WaitForAsync(s => Toast(s)["open"]?.GetValue<bool>() == true && Texts(Toast(s)).Contains("fresh one"), Reaction);
        Assert.Contains("fresh one", Texts(Toast(status)));

        // Non-activating, on the bottom-right corner of the primary work area.
        var hwnd = (nint)(long)Toast(status)["hwnd"]!;
        Assert.NotEqual(0, GetWindowLongPtrW(hwnd, -20) & 0x08000000);
        var rect = Interactive.WindowRect(hwnd);
        var workArea = Interactive.PrimaryWorkArea();
        Assert.InRange(rect.Right, workArea.Right - 60, workArea.Right);
        Assert.InRange(rect.Bottom, workArea.Bottom - 60, workArea.Bottom);

        // It leaves after popupSeconds.
        await run.WaitForAsync(s => Toast(s)["open"]?.GetValue<bool>() != true, TimeSpan.FromSeconds(3) + Reaction);

        // Four at once stack up to maxVisible (3), the newest on top.
        WriteFake(run, Fake(
            NewItem("old", "app.one", "One", "already there", minutesAgo: 30),
            NewItem("a", "app.two", "Two", "fresh one", minutesAgo: 5),
            NewItem("b", "app.two", "Two", "stack b", minutesAgo: 4),
            NewItem("c", "app.two", "Two", "stack c", minutesAgo: 3),
            NewItem("d", "app.two", "Two", "stack d", minutesAgo: 2),
            NewItem("e", "app.two", "Two", "stack e", minutesAgo: 1)));
        status = await run.WaitForAsync(s => Toast(s)["open"]?.GetValue<bool>() == true && Texts(Toast(s)).Contains("stack e"), Reaction);
        var shown = Texts(Toast(status)).Where(t => t.StartsWith("stack ", StringComparison.Ordinal)).ToList();
        Assert.Equal(["stack e", "stack d", "stack c"], shown);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S24_3_do_not_disturb_keeps_the_pop_ups_away_but_not_the_history()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(NewItem("old", "app.one", "One", "already there", minutesAgo: 30)), Bell(popupSeconds: 4));
        await run.WaitForAsync(s => History(s)?.AsArray().Count == 1, Startup);

        await InvokeAsync(run, "host.notifications.setDoNotDisturb", new JsonObject { ["on"] = true });
        await run.WaitForAsync(s => (bool?)NotificationStatus(s)?["doNotDisturb"] == true, Reaction);

        WriteFake(run, Fake(NewItem("old", "app.one", "One", "already there", minutesAgo: 30), NewItem("quiet", "app.two", "Two", "while quiet", minutesAgo: 0)));
        var status = await run.WaitForAsync(s => History(s)?.AsArray().Count == 2, Reaction);
        Assert.Equal("quiet", (string?)History(status)![0]!["id"]);
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.NotEqual(true, Toast(await run.WaitForStatusAsync())["open"]?.GetValue<bool>());

        await InvokeAsync(run, "host.notifications.setDoNotDisturb", new JsonObject { ["on"] = false });
        await run.WaitForAsync(s => (bool?)NotificationStatus(s)?["doNotDisturb"] == false, Reaction);
        WriteFake(run, Fake(NewItem("old", "app.one", "One", "already there", minutesAgo: 30), NewItem("quiet", "app.two", "Two", "while quiet", minutesAgo: 1),
            NewItem("loud", "app.two", "Two", "after quiet", minutesAgo: 0)));
        status = await run.WaitForAsync(s => Toast(s)["open"]?.GetValue<bool>() == true && Texts(Toast(s)).Contains("after quiet"), Reaction);
        Assert.Contains("after quiet", Texts(Toast(status)));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S24_4_the_centre_lists_dismisses_clears_and_opens_notifications()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(
            NewItem("i1", "app.one", "One", "first", minutesAgo: 4),
            NewItem("i2", "app.two", "Two", "second", minutesAgo: 3),
            NewItem("i3", "app.two", "Two", "third", minutesAgo: 2),
            NewItem("i4", "app.three", "Three", "fourth", minutesAgo: 1, activationFails: true)));
        var status = await run.WaitForAsync(s => History(s)?.AsArray().Count == 4, Startup);

        // Opening the centre lists the history, and marks it read.
        await InvokeAsync(run, "notifications.openCentre", []);
        status = await run.WaitForAsync(s => Centre(s)["open"]?.GetValue<bool>() == true && Texts(Centre(s)).Contains("fourth"), Reaction);
        Assert.Equal(["fourth", "third", "second", "first"], Texts(Centre(status)).Where(t => t is "first" or "second" or "third" or "fourth"));
        Assert.Equal("Notifications", Texts(Centre(status)).First(t => t == "Notifications"));
        await run.WaitForAsync(s => (int?)NotificationStatus(s)?["unread"] == 0, Reaction);

        await InvokeAsync(run, "notifications.dismiss", new JsonObject { ["id"] = "i1" });
        status = await run.WaitForAsync(s => History(s)?.AsArray().Count == 3, Reaction);
        Assert.Contains("notification-dismiss i1", DesktopActions(run));
        Assert.DoesNotContain(History(status)!.AsArray(), i => (string?)i!["id"] == "i1");

        // A failed activation is reported in the centre and leaves it open.
        var failed = await InvokeAsync(run, "notifications.activate", new JsonObject { ["id"] = "i4" });
        Assert.False((bool?)failed["lastInvoke"]!["ok"]);
        status = await run.WaitForAsync(s => Texts(Centre(s)).Contains("That application could not be started."), Reaction);
        Assert.True(Centre(status)["open"]!.GetValue<bool>());
        Assert.DoesNotContain(DesktopActions(run), line => line.StartsWith("notification-activate i4", StringComparison.Ordinal));

        // Clearing one application leaves the rest.
        await InvokeAsync(run, "notifications.clear", new JsonObject { ["appId"] = "app.two" });
        status = await run.WaitForAsync(s => History(s)?.AsArray().Count == 1, Reaction);
        Assert.Contains("notification-clear app.two", DesktopActions(run));
        Assert.Equal("i4", (string?)History(status)![0]!["id"]);

        // Opening an application starts it by its id and closes the centre.
        WriteFake(run, Fake(NewItem("i5", "app.five", "Five", "fifth", minutesAgo: 0)));
        await run.WaitForAsync(s => History(s)?.AsArray().Count == 1 && (string?)History(s)![0]!["id"] == "i5", Reaction);
        await InvokeAsync(run, "notifications.activate", new JsonObject { ["id"] = "i5" });
        await run.WaitForAsync(s => Centre(s)["open"]?.GetValue<bool>() != true, Reaction);
        Assert.Contains("notification-activate i5 app.five", DesktopActions(run));

        // Clearing everything empties the history; the centre then says so.
        WriteFake(run, Fake(NewItem("i6", "app.six", "Six", "sixth", minutesAgo: 0), NewItem("i7", "app.seven", "Seven", "seventh", minutesAgo: 0)));
        await run.WaitForAsync(s => History(s)?.AsArray().Count == 2, Reaction);
        await InvokeAsync(run, "notifications.clear", []);
        await run.WaitForAsync(s => History(s)?.AsArray().Count == 0, Reaction);
        Assert.Contains("notification-clear all", DesktopActions(run));
        await InvokeAsync(run, "notifications.openCentre", []);
        await run.WaitForAsync(s => Texts(Centre(s)).Contains("No new notifications"), Reaction);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S24_5_each_missing_step_is_named_by_the_status_feed_the_setup_page_and_the_bell()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)));
        var status = await run.WaitForAsync(s => NotificationStatus(s) is not null, Startup);
        var ready = NotificationStatus(status)!;
        Assert.Equal("ok", (string?)ready["identity"]);
        Assert.Equal("allowed", (string?)ready["access"]);
        Assert.True((bool?)ready["switchedOn"]);
        Assert.Null(ready["error"]);

        foreach (var (identity, access, switchedOn, code, step) in new (string, string, bool, string, string)[]
        {
            ("missing", "allowed", true, "IdentityMissing", "The notification helper is not registered."),
            ("ok", "denied", true, "AccessDenied", "Notification access is not allowed."),
            ("ok", "allowed", false, "NotificationsOff", "Notifications are switched off for your account."),
        })
        {
            WriteFake(run, Fake(identity, access, switchedOn, NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)));
            status = await run.WaitForAsync(s => (string?)NotificationStatus(s)?["error"] == code, Reaction);
            Assert.Equal(identity, (string?)NotificationStatus(status)!["identity"]);
            Assert.Equal(access, (string?)NotificationStatus(status)!["access"]);
            Assert.Equal(switchedOn, (bool?)NotificationStatus(status)!["switchedOn"]);
            status = await run.WaitForAsync(s => (string?)Bell(s)?["tooltip"] == "Notifications need setting up", Reaction);

            await InvokeAsync(run, "notifications.openCentre", []);
            status = await run.WaitForAsync(s => Setup(s)["open"]?.GetValue<bool>() == true && Texts(Setup(s)).Contains(step), Reaction);
            Assert.DoesNotContain(Texts(Centre(status)), t => t == "No new notifications");
            if (Setup(status)["open"]!.GetValue<bool>())
            {
                await InvokeAsync(run, "notifications.openCentre", [], "keyboard");
            }

            await run.WaitForAsync(s => Setup(s)["open"]?.GetValue<bool>() != true, Reaction);
        }

        // A read that fails after a good one keeps the history, names the code and recovers by itself.
        WriteFake(run, Fake(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)));
        await run.WaitForAsync(s => NotificationStatus(s)?["error"] is null && History(s)?.AsArray().Count == 1, Reaction);
        WriteFake(run, new JsonObject { ["readFails"] = true, ["items"] = new JsonArray(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)) });
        status = await run.WaitForAsync(s => (string?)NotificationStatus(s)?["error"] == "HistoryReadFailed", Reaction);
        Assert.Single(History(status)!.AsArray());
        WriteFake(run, Fake(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)));
        await run.WaitForAsync(s => NotificationStatus(s)?["error"] is null, Reaction);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S24_6_win_n_opens_the_centre_while_the_plugin_runs()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)));
        await run.WaitForAsync(KeysReady, Startup);

        Interactive.PressMarked(Interactive.LeftWin, 'N');
        var status = await run.WaitForAsync(s => Centre(s)["open"]?.GetValue<bool>() == true && Texts(Centre(s)).Contains("hello"), Reaction);
        Assert.Contains(Dispatches(status), d => d.StartsWith("send notifications.openCentre ", StringComparison.Ordinal) && d.EndsWith(" Hotkey", StringComparison.Ordinal));
        Assert.Contains("hello", Texts(Centre(status)));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S24_6_a_chord_from_settings_json_reaches_the_command_and_the_table_is_read_from_the_file()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteFake(run, Fake(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)));
        run.WriteDataFile("settings.json", new JsonObject
        {
            ["schemaVersion"] = 1,
            ["activePreset"] = "subzerodev.default",
            ["windowsKeys"] = new JsonObject { ["Win+B"] = "notifications.openCentre" },
        }.ToJsonString());
        run.WriteDefaultLayout((Guid.NewGuid(), Notifications, "bell"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        await run.WaitForAsync(KeysReady, Startup);

        Interactive.PressMarked(Interactive.LeftWin, 'B');
        var status = await run.WaitForAsync(s => Centre(s)["open"]?.GetValue<bool>() == true && Texts(Centre(s)).Contains("hello"), Reaction);
        Assert.Contains(Dispatches(status), d => d.StartsWith("send notifications.openCentre ", StringComparison.Ordinal) && d.EndsWith(" Hotkey", StringComparison.Ordinal));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S24_7_a_crashed_worker_faults_the_bell_leaves_the_rest_running_and_republishes_the_history_on_restart()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteFake(run, Fake(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)));
        run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"), (Guid.NewGuid(), Notifications, "bell"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        var status = await run.WaitForAsync(s => History(s)?.AsArray().Count == 1 && PluginOf(s, Launcher)["state"]?.GetValue<string>() == "Running", Startup);
        var hostPid = (int)status["hostPid"]!;
        var launcherPid = (int)PluginOf(status, Launcher)["pid"]!;
        var workerPid = (int)PluginOf(status, Notifications)["pid"]!;

        using (var worker = Process.GetProcessById(workerPid))
        {
            worker.Kill();
        }

        status = await run.WaitForAsync(s => PluginOf(s, Notifications)["state"]?.GetValue<string>() == "Faulted", Reaction);
        Assert.Equal("Running", PluginOf(status, Launcher)["state"]!.GetValue<string>());
        Assert.Equal(launcherPid, (int)PluginOf(status, Launcher)["pid"]!);
        Assert.Equal(hostPid, (int)status["hostPid"]!);

        // The supervisor restarts it; the history is published again by the new worker.
        WriteFake(run, Fake(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1), NewItem("n2", "app.one", "One", "after the crash", minutesAgo: 0)));
        status = await run.WaitForAsync(s => PluginOf(s, Notifications)["state"]?.GetValue<string>() == "Running"
            && (int?)PluginOf(s, Notifications)["pid"] is { } pid && pid != workerPid && History(s)?.AsArray().Count == 2, TimeSpan.FromSeconds(20));
        Assert.Equal(launcherPid, (int)PluginOf(status, Launcher)["pid"]!);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S24_8_an_idle_worker_costs_next_to_no_processor_time()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(NewItem("n1", "app.one", "One", "hello", minutesAgo: 1)));
        var status = await run.WaitForAsync(s => History(s)?.AsArray().Count == 1, Startup);
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        using var worker = Process.GetProcessById((int)PluginOf(status, Notifications)["pid"]!);

        var before = worker.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        await Task.Delay(TimeSpan.FromSeconds(10), TestContext.Current.CancellationToken);
        worker.Refresh();
        var percent = (worker.TotalProcessorTime - before).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;

        // The fake polls its file every 100 ms, which a real worker does not; the budget is docs/budgets.md's.
        Assert.True(percent < 2.0, $"The idle notifications worker used {percent:F2}% of one core.");
        Assert.Equal(0, await run.StopAsync());
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private static HostRun Start(JsonObject fake, JsonObject? bell = null)
    {
        var run = HostRun.Create();
        WriteFake(run, fake);
        run.WriteDefaultLayout((Guid.NewGuid(), Notifications, "bell", bell));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        return run;
    }

    private static JsonObject Bell(int popupSeconds) => new() { ["popupSeconds"] = popupSeconds };

    private static JsonObject Fake(params JsonObject[] items) => Fake("ok", "allowed", true, items);

    private static JsonObject Fake(string identity, string access, bool switchedOn, params JsonObject[] items) => new()
    {
        ["identity"] = identity,
        ["access"] = access,
        ["switchedOn"] = switchedOn,
        ["items"] = new JsonArray([.. items]),
    };

    private static JsonObject NewItem(string id, string appId, string appName, string title, string? body = null, double minutesAgo = 0, bool activationFails = false)
    {
        // The body is what the tests look for on screen, so a plain title alone is used as the body too.
        var item = new JsonObject
        {
            ["id"] = id,
            ["appId"] = appId,
            ["appName"] = appName,
            ["title"] = title,
            ["body"] = body ?? title,
            ["at"] = DateTimeOffset.UtcNow.AddMinutes(-minutesAgo).ToString("O"),
            ["silent"] = false,
        };
        if (activationFails)
        {
            item["activationFails"] = true;
        }

        return item;
    }

    /// <summary>Replaces the fake history in one move, so the plugin never reads half a file.</summary>
    private static void WriteFake(HostRun run, JsonObject fake)
    {
        Directory.CreateDirectory(run.HookDirectory);
        var path = Path.Combine(run.HookDirectory, "notifications.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, fake.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<JsonObject> InvokeAsync(HostRun run, string command, JsonObject args, string trigger = "pointer")
    {
        var before = (int?)(await run.WaitForStatusAsync())["lastInvoke"]?["count"] ?? 0;
        await File.WriteAllLinesAsync(Path.Combine(run.HookDirectory, "invoke"), [command, "keyboard", args.ToJsonString(), trigger], TestContext.Current.CancellationToken);
        return await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == command && (int?)last["count"] > before, Reaction);
    }

    private static JsonNode? Feed(JsonObject status, string id) =>
        status["feeds"]!.AsArray().FirstOrDefault(f => (string?)f!["id"] == id)?["snapshot"];

    private static JsonNode? History(JsonObject status) => Feed(status, "notifications.history");

    private static JsonObject? NotificationStatus(JsonObject status) => Feed(status, "notifications.status") as JsonObject;

    private static JsonObject PluginOf(JsonObject status, string id) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == id)?.AsObject() ?? [];

    private static JsonObject? Bell(JsonObject status) =>
        status["contributions"]!.AsArray().Where(c => (string?)c!["plugin"] == Notifications)
            .Select(c => c!["interactive"]?.AsArray().FirstOrDefault(e => (string?)e!["id"] == "bell")?.AsObject()).FirstOrDefault(e => e is not null);

    private static bool IsToast(JsonNode popup) => (string?)popup["plugin"] == Notifications && (string?)popup["popup"] == "toast";

    private static JsonObject PopupOf(JsonObject status, string popup) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == Notifications && (string?)p["popup"] == popup)?.AsObject() ?? [];

    private static JsonObject Toast(JsonObject status) => PopupOf(status, "toast");

    private static JsonObject Centre(JsonObject status) => PopupOf(status, "centre");

    private static JsonObject Setup(JsonObject status) => PopupOf(status, "setup");

    private static List<string> Texts(JsonObject popup) =>
        popup["texts"]?.AsArray().Select(t => (string?)t ?? string.Empty).ToList() ?? [];

    private static List<string> Dispatches(JsonObject status) => [.. status["dispatches"]!.AsArray().Select(d => (string)d!)];

    /// <summary>Ready, with the hook installed and the plugin running: only then may a test inject keys.</summary>
    private static bool KeysReady(JsonObject status) =>
        (bool?)status["ready"] == true
        && (bool?)status["keyHook"]?["installed"] == true
        && PluginOf(status, Notifications)["state"]?.GetValue<string>() == "Running"
        && History(status) is not null;

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

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);
}

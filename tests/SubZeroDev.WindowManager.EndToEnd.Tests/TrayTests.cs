using System.Globalization;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S19: the notification area and the app-bar answers in replacement mode. Under the test hook the host's
/// tray window and its created message carry hook-scoped names, so the user's real tray icons never come to it;
/// a <see cref="TrayClient"/> in the test process sends what <c>Shell_NotifyIcon</c> and <c>SHAppBarMessage</c>
/// would, and work-area changes are recorded in <c>desktop-actions.log</c> instead of being made.
/// </summary>
public sealed class TrayTests
{
    private const string Tray = "subzerodev.tray";
    private const uint Add = 0;
    private const uint Modify = 1;
    private const uint Delete = 2;
    private const uint SetVersion = 4;
    private const uint MessageFlag = 1;
    private const uint IconFlag = 2;
    private const uint TipFlag = 4;
    private const uint InfoFlag = 0x10;
    private const uint AppBarNew = 0;
    private const uint AppBarRemove = 1;
    private const uint AppBarQueryPos = 2;
    private const uint AppBarSetPos = 3;
    private const uint AppBarGetState = 4;
    private const uint AppBarGetTaskbarPos = 5;
    private const uint EdgeTop = 1;
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S19_1_an_icon_is_added_changed_and_deleted_on_the_panel()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        var status = await run.WaitForAsync(Live, Startup);
        using var client = TrayClient.Open((string)status["tray"]!["className"]!);
        var key = client.Key(1);

        var sent = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Assert.Equal(1, client.Notify(Add, 1, MessageFlag | IconFlag | TipFlag, "First"));
        status = await run.WaitForAsync(s => Icon(s, key) is not null && Item(s, key) is not null, TimeSpan.FromSeconds(5));
        var changedAt = (long)status["tray"]!["changedAt"]!;
        Assert.True(changedAt - sent <= 250, $"The icon reached the host {changedAt - sent} ms after it was sent.");
        Assert.Equal((uint)Environment.ProcessId, (uint)Icon(status, key)!["ownerPid"]!);
        Assert.Equal("First", (string?)Item(status, key)!["tooltip"]);
        Assert.True((bool)Item(status, key)!["hasIcon"]!);

        // A second Add of the same icon is refused, as Explorer refuses it.
        Assert.Equal(0, client.Notify(Add, 1, MessageFlag | IconFlag | TipFlag, "Again"));

        Assert.Equal(1, client.Notify(Modify, 1, TipFlag, "Second"));
        await run.WaitForAsync(s => (string?)Item(s, key)?["tooltip"] == "Second", TimeSpan.FromSeconds(5));

        Assert.Equal(1, client.Notify(Delete, 1, 0));
        await run.WaitForAsync(s => Icon(s, key) is null && Item(s, key) is null, TimeSpan.FromSeconds(5));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S19_2_a_click_and_a_menu_request_reach_the_owner_with_the_anchor_and_foreground_rights()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        var status = await run.WaitForAsync(Live, Startup);
        using var client = TrayClient.Open((string)status["tray"]!["className"]!);
        var key = client.Key(1);
        Assert.Equal(1, client.Notify(Add, 1, MessageFlag | IconFlag | TipFlag, "Versioned"));
        Assert.Equal(1, client.Notify(SetVersion, 1, 0, timeoutOrVersion: 4));
        status = await run.WaitForAsync(s => Item(s, key) is not null, TimeSpan.FromSeconds(5));

        // A click on the panel's button selects the icon, anchored where the click was.
        var bounds = Item(status, key)!["bounds"]!;
        var x = ((int)bounds["left"]! + (int)bounds["right"]!) / 2;
        var y = ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2;
        Interactive.Click(x, y);
        var select = await WaitForMessageAsync(client, m => (m.LParam & 0xFFFF) == 0x400);
        Assert.Equal(1, (int)((ulong)select.LParam >> 16 & 0xFFFF));
        Assert.InRange((short)(select.WParam & 0xFFFF), x - 2, x + 2);
        Assert.InRange((short)(select.WParam >> 16 & 0xFFFF), y - 2, y + 2);
        Assert.Contains(client.Messages, m => (m.LParam & 0xFFFF) == 0x201);
        await WaitUntilAsync(() => DesktopActions(run).Contains($"tray-allow-foreground {Environment.ProcessId}"), "tray-allow-foreground");

        // The menu request from the keyboard path, anchored where the command says.
        Hook(run, "invoke", Invoke(key, "contextMenu", 100, 200));
        var menu = await WaitForMessageAsync(client, m => (m.LParam & 0xFFFF) == 0x7B);
        Assert.Equal(100, (short)(menu.WParam & 0xFFFF));
        Assert.Equal(200, (short)(menu.WParam >> 16 & 0xFFFF));

        // An icon without a version hears only the button message, with its id in wParam.
        var legacy = client.Key(2);
        Assert.Equal(1, client.Notify(Add, 2, MessageFlag | IconFlag | TipFlag, "Legacy"));
        await run.WaitForAsync(s => Icon(s, legacy) is not null, TimeSpan.FromSeconds(5));
        await WaitUntilAsync(() => !File.Exists(Path.Combine(run.HookDirectory, "invoke")), "the menu invoke to be taken");
        Hook(run, "invoke", Invoke(legacy, "select", 0, 0));
        await WaitForMessageAsync(client, m => m.WParam == 2 && m.LParam == 0x202);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S19_3_a_balloon_shows_on_the_panel_and_times_out()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        var status = await run.WaitForAsync(Live, Startup);
        using var client = TrayClient.Open((string)status["tray"]!["className"]!);
        Assert.Equal(1, client.Notify(Add, 1, MessageFlag | IconFlag | TipFlag, "Balloons"));
        Assert.Equal(1, client.Notify(Modify, 1, InfoFlag, timeoutOrVersion: 1000, infoTitle: "Title", info: "Text"));

        await run.WaitForAsync(s => HostRun.Texts(s, Tray).Any(texts => texts.Contains("Title: Text")), TimeSpan.FromSeconds(5));
        await WaitForMessageAsync(client, m => m.LParam == 0x402);
        await run.WaitForAsync(s => !HostRun.Texts(s, Tray).Any(texts => texts.Contains("Title: Text")), TimeSpan.FromSeconds(3));
        await WaitForMessageAsync(client, m => m.LParam == 0x404);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S19_4_a_bad_payload_is_refused_and_a_dead_owner_loses_its_icons()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        var status = await run.WaitForAsync(Live, Startup);
        using var client = TrayClient.Open((string)status["tray"]!["className"]!);
        var key = client.Key(1);

        Assert.Equal(0, client.Send(1, new byte[20]));
        await WaitUntilAsync(() => run.HostLog().Contains("TrayPayloadRejected", StringComparison.Ordinal), "TrayPayloadRejected");

        Assert.Equal(1, client.Notify(Add, 1, MessageFlag | IconFlag | TipFlag, "Orphan"));
        await run.WaitForAsync(s => Icon(s, key) is not null, TimeSpan.FromSeconds(5));
        client.Close();
        await run.WaitForAsync(s => Icon(s, key) is null && Item(s, key) is null, TimeSpan.FromSeconds(2));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S19_5_apps_already_running_hear_the_tray_created_message_and_add_their_icons_again()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var id = Guid.NewGuid().ToString("N");
        Hook(run, "tray-id", id);
        var (className, createdMessage) = TrayClient.Names(id);
        using var client = TrayClient.Open(className, createdMessage);
        run.WriteDefaultLayout((Guid.NewGuid(), Tray, "tray"));
        run.Start(HostRun.HostExe, "--mode", "replacement");

        var status = await run.WaitForAsync(s => Live(s) && (bool?)s["tray"]?["announced"] == true, Startup);
        Assert.Equal(className, (string?)status["tray"]!["className"]);
        Assert.Equal(createdMessage, (string?)status["tray"]!["createdMessage"]);
        await WaitUntilAsync(() => client.HeardCreated, "the created message");

        Assert.Equal(1, client.Notify(Add, 1, MessageFlag | IconFlag | TipFlag, "Back"));
        await run.WaitForAsync(s => Item(s, client.Key(1)) is not null, TimeSpan.FromSeconds(5));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S19_6_app_bars_get_the_taskbar_and_reserve_and_release_an_edge()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        var status = await run.WaitForAsync(Live, Startup);
        await WaitUntilAsync(() => WorkAreas(run).Count > 0, "the panels' work area");
        using var client = TrayClient.Open((string)status["tray"]!["className"]!);

        var taskbar = client.AppBar(AppBarGetTaskbarPos);
        Assert.Equal(1, taskbar.Result);
        Assert.True(taskbar.Rect.Right > taskbar.Rect.Left && taskbar.Rect.Bottom > taskbar.Rect.Top, $"Empty taskbar rect {taskbar.Rect}.");
        Assert.Equal(2, client.AppBar(AppBarGetState).Result);

        Assert.Equal(1, client.AppBar(AppBarNew, callback: TrayClient.CallbackMessage + 1).Result);
        var monitor = Interactive.PrimaryMonitor();
        var proposal = (monitor.Left, monitor.Top, monitor.Right, monitor.Top + 30);
        var query = client.AppBar(AppBarQueryPos, EdgeTop, proposal);
        Assert.Equal(1, query.Result);
        Assert.Equal(30, query.Rect.Bottom - query.Rect.Top);
        Assert.True(query.Rect.Top >= monitor.Top, $"The strip {query.Rect} starts above the monitor.");
        Assert.False(Overlaps(query.Rect, taskbar.Rect), $"The strip {query.Rect} overlaps the taskbar {taskbar.Rect}.");

        var before = WorkAreas(run);
        var last = before[^1];
        Assert.Equal(1, client.AppBar(AppBarSetPos, EdgeTop, proposal).Result);
        await WaitUntilAsync(() => WorkAreas(run).Count > before.Count, "the app bar's work area");
        var reserved = WorkAreas(run)[^1];
        Assert.Equal(last.Monitor, reserved.Monitor);
        Assert.Equal(last.Area.Top + 30, reserved.Area.Top);

        var count = WorkAreas(run).Count;
        Assert.Equal(1, client.AppBar(AppBarRemove).Result);
        await WaitUntilAsync(() => WorkAreas(run).Count > count, "the released work area");
        Assert.Equal(last, WorkAreas(run)[^1]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S19_7_alongside_Explorer_there_is_no_tray_and_the_tray_item_is_turned_away()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Tray, "tray"));
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && (string?)Plugin(s, Tray)?["state"] == "Incompatible", Startup);
        Assert.Null(status["tray"]);
        Assert.Contains("does not support", (string?)Plugin(status, Tray)!["problem"]);
        Assert.Equal(0, await run.StopAsync());
    }

    private static HostRun Start()
    {
        var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Tray, "tray"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        return run;
    }

    private static bool Live(JsonObject status) =>
        (bool?)status["ready"] == true
        && (string?)Plugin(status, Tray)?["state"] == "Running"
        && (string?)status["tray"]?["state"] == "Held";

    private static JsonObject? Plugin(JsonObject status, string plugin) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == plugin)?.AsObject();

    private static JsonObject? Icon(JsonObject status, string key) =>
        status["tray"]?["icons"]?.AsArray().Select(i => i!.AsObject()).FirstOrDefault(i => (string?)i["key"] == key);

    private static JsonObject? Item(JsonObject status, string key) =>
        status["contributions"]!.AsArray()
            .Where(c => (string?)c!["plugin"] == Tray && c["reason"] is null)
            .SelectMany(c => c!["interactive"]!.AsArray().Select(e => e!.AsObject()))
            .FirstOrDefault(e => (string?)e["itemKey"] == key);

    private static string Invoke(string key, string action, int x, int y) =>
        string.Join("\n", "host.tray.invoke", "keyboard", new JsonObject { ["key"] = key, ["action"] = action, ["x"] = x, ["y"] = y }.ToJsonString()) + "\n";

    private static bool Overlaps((int Left, int Top, int Right, int Bottom) a, (int Left, int Top, int Right, int Bottom) b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    private static async Task<TrayClient.Received> WaitForMessageAsync(TrayClient client, Func<TrayClient.Received, bool> match)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (true)
        {
            var messages = client.Messages;
            foreach (var message in messages)
            {
                if (match(message))
                {
                    return message;
                }
            }

            Assert.True(DateTime.UtcNow < deadline,
                $"No matching callback among {string.Join(", ", messages.Select(m => $"{m.Message:X}/{m.WParam:X}/{m.LParam:X}"))}.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static List<(string Monitor, (int Left, int Top, int Right, int Bottom) Area)> WorkAreas(HostRun run) =>
        [.. DesktopActions(run).Where(line => line.StartsWith("work-area ", StringComparison.Ordinal)).Select(line =>
        {
            var parts = line.Split(' ');
            var edges = parts[^1].Split(',').Select(n => int.Parse(n, CultureInfo.InvariantCulture)).ToArray();
            return (string.Join(' ', parts[1..^1]), (edges[0], edges[1], edges[2], edges[3]));
        })];

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

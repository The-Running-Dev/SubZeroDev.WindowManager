using System.Globalization;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S29: grouping, the hover preview, jump lists and the progress/overlay badges of the running-windows strip.
/// Every window is a test-hook synthetic one (all of them share one executable, so they group), the jump lists come from
/// <c>jumplists.json</c> in the hook directory and a chosen entry is only recorded in <c>desktop-actions.log</c>, and
/// progress and overlays come from the <c>taskbar</c> marker file: no test reads the user's jump-list folders, starts a
/// program or uses the real taskbar (invariant I38). The badge messages of real applications are not exercised here: the
/// wire format is unverified, which is the manual check S29.3.
/// </summary>
public sealed class TaskbarDepthEndToEndTests
{
    private const string RunningWindows = "subzerodev.running-windows";
    private const string GroupKey = @"g:c:\szd\synthetic.exe";
    private const long FirstSyntheticHandle = 0x7A00_0000_0000;
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S29_1_windows_of_one_application_share_a_button_with_a_count_and_never_means_one_each()
    {
        HostRun.RequireDesktop();
        using var run = Start("always");
        await run.WaitForAsync(Ready, Startup);
        await SyntheticAsync(run, "add", 3);

        var status = await run.WaitForAsync(s => Buttons(s).Count == 1, Reaction);
        var button = Buttons(status).Single();
        Assert.Equal(GroupKey, (string?)button["itemKey"]);
        Assert.Equal("3", (string?)button["badge"]);
        Assert.Equal(3, (int)status["windowModel"]!["synthetic"]!);
        Assert.Equal(0, await run.StopAsync());

        using var apart = Start("never");
        await apart.WaitForAsync(Ready, Startup);
        await SyntheticAsync(apart, "add", 3);
        status = await apart.WaitForAsync(s => Buttons(s).Count == 3, Reaction);
        Assert.All(Buttons(status), b => Assert.True(string.IsNullOrEmpty((string?)b["badge"])));
        Assert.Equal(0, await apart.StopAsync());
    }

    [Fact]
    public async Task S29_1_whenFull_groups_only_once_there_are_more_than_eight_windows()
    {
        HostRun.RequireDesktop();
        using var run = Start("whenFull");
        await run.WaitForAsync(Ready, Startup);
        await SyntheticAsync(run, "add", 8);
        var status = await run.WaitForAsync(s => Buttons(s).Count == 8, Reaction);
        Assert.All(Buttons(status), b => Assert.DoesNotContain("g:", (string?)b["itemKey"], StringComparison.Ordinal));

        await SyntheticAsync(run, "add", 1);
        status = await run.WaitForAsync(s => Buttons(s).Count == 1, Reaction);
        Assert.Equal("9", (string?)Buttons(status).Single()["badge"]);

        await SyntheticAsync(run, "close", 1);
        await run.WaitForAsync(s => Buttons(s).Count == 8, TimeSpan.FromSeconds(5));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S29_1_labels_off_leaves_the_buttons_icon_only()
    {
        HostRun.RequireDesktop();
        using var run = Start("never", labels: false);
        await run.WaitForAsync(Ready, Startup);
        await SyntheticAsync(run, "add", 2);
        var status = await run.WaitForAsync(s => Buttons(s).Count == 2, Reaction);
        Assert.All(Buttons(status), b => Assert.True(string.IsNullOrEmpty((string?)b["label"])));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S29_4_the_jump_list_offers_a_task_then_the_readable_entries_and_choosing_one_launches_it()
    {
        HostRun.RequireDesktop();
        using var run = Start("always");
        File.WriteAllText(Path.Combine(run.HookDirectory, "jumplists.json"), new JsonObject
        {
            ["synthetic.exe"] = new JsonArray(
                new JsonObject { ["category"] = "recent", ["title"] = "notes.txt", ["path"] = @"C:\szd\notes.txt" },
                new JsonObject { ["category"] = "frequent", ["title"] = "report.docx", ["path"] = @"C:\szd\report.docx", ["args"] = "--read" }),
        }.ToJsonString());
        await run.WaitForAsync(Ready, Startup);
        await SyntheticAsync(run, "add", 2);
        await run.WaitForAsync(s => Buttons(s).Count == 1, Reaction);

        var opened = await InvokeAsync(run, "windows.openJumpList", new JsonObject { ["groupKey"] = @"c:\szd\synthetic.exe" });
        Assert.True((bool)opened["ok"]!, opened.ToJsonString());
        var status = await run.WaitForAsync(s => JumpList(s)?["open"]?.GetValue<bool>() == true
            && Entries(s).Count == 3 && (string?)JumpList(s)!["focused"] == "entry0", Reaction);
        Assert.Equal(["Open synthetic", "notes.txt", "report.docx"], Entries(status).Select(i => (string?)i["label"]));

        // Tab moves to the first readable entry; Enter chooses it, as a click does, and the list then closes.
        Assert.Equal((int)status["hostPid"]!, Interactive.ForegroundProcessId());
        Interactive.Press(Interactive.Tab);
        await run.WaitForAsync(s => (string?)JumpList(s)?["focused"] == "entry1", Reaction);
        Interactive.Press(Interactive.Enter);
        await run.WaitForAsync(s => JumpList(s)?["open"]?.GetValue<bool>() != true, Reaction);
        Assert.Contains(DesktopActions(run), line => line == @"jumplist-launch notes.txt|C:\szd\notes.txt|");
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S29_4_a_group_key_no_window_has_is_refused()
    {
        HostRun.RequireDesktop();
        using var run = Start("always");
        await run.WaitForAsync(Ready, Startup);
        var refused = await InvokeAsync(run, "windows.openJumpList", new JsonObject { ["groupKey"] = "nothing" });
        Assert.False((bool)refused["ok"]!, refused.ToJsonString());
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S29_3_progress_and_an_overlay_show_on_the_button_and_clear_again()
    {
        HostRun.RequireDesktop();
        using var run = Start("always");
        await run.WaitForAsync(Ready, Startup);
        await SyntheticAsync(run, "add", 2);
        await run.WaitForAsync(s => Buttons(s).Count == 1, Reaction);
        var first = (FirstSyntheticHandle + 1).ToString(CultureInfo.InvariantCulture);

        // The count shows until something better is known; progress outranks an overlay, which outranks the count.
        await TaskbarAsync(run, $"overlay {first} Unread");
        var status = await run.WaitForAsync(s => Buttons(s).Count == 1 && (string?)Buttons(s)[0]["badge"] == "Unread", Reaction);
        Assert.NotNull(status);

        await TaskbarAsync(run, $"progress {first} normal 0.5");
        await run.WaitForAsync(s => Buttons(s).Count == 1 && (string?)Buttons(s)[0]["badge"] == "50%", Reaction);

        await TaskbarAsync(run, $"progress {first} none");
        await run.WaitForAsync(s => Buttons(s).Count == 1 && (string?)Buttons(s)[0]["badge"] == "Unread", Reaction);

        await TaskbarAsync(run, $"overlay {first} -");
        await run.WaitForAsync(s => Buttons(s).Count == 1 && (string?)Buttons(s)[0]["badge"] == "2", Reaction);

        // A handle the model does not know is ignored.
        await TaskbarAsync(run, "progress 12345 normal 0.9");
        await Task.Delay(500, TestContext.Current.CancellationToken);
        Assert.Equal("2", (string?)Buttons(await run.WaitForStatusAsync()).Single()["badge"]);
        Assert.Equal(0, await run.StopAsync());
    }

    private static HostRun Start(string grouping, bool labels = true)
    {
        var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows", new JsonObject { ["grouping"] = grouping, ["labels"] = labels }));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        return run;
    }

    private static bool Ready(JsonObject status) =>
        (bool?)status["ready"] == true
        && (bool?)status["keyHook"]?["installed"] == true
        && status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == RunningWindows && (string?)p["state"] == "Running");

    private static List<JsonObject> Buttons(JsonObject status) =>
        [.. status["contributions"]!.AsArray()
            .Where(c => (string?)c!["plugin"] == RunningWindows && c["reason"] is null)
            .Take(1)
            .SelectMany(c => c!["interactive"]!.AsArray().Select(e => e!.AsObject()))
            .Where(e => e["itemKey"] is not null)];

    private static JsonObject? Popup(JsonObject status, string id) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == RunningWindows && (string?)p["popup"] == id)?.AsObject();


    private static JsonObject? JumpList(JsonObject status) => Popup(status, "jumplist");

    private static List<JsonObject> Entries(JsonObject status) =>
        [.. JumpList(status)!["items"]!.AsArray().Select(i => i!.AsObject()).Where(i => ((string?)i["id"])?.StartsWith("entry", StringComparison.Ordinal) == true)];

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

    // The hook takes one marker file at a time; a request is sent only once the previous one has been taken.
    private static async Task TakenAsync(string path)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        while (File.Exists(path))
        {
            Assert.True(DateTime.UtcNow < deadline, $"Timed out waiting for the hook to take {Path.GetFileName(path)}.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    private static async Task SyntheticAsync(HostRun run, string op, int count)
    {
        var path = Path.Combine(run.HookDirectory, "synthetic-windows");
        File.WriteAllText(path, new JsonObject { ["op"] = op, ["count"] = count }.ToJsonString());
        await TakenAsync(path);
        await Task.Delay(300, TestContext.Current.CancellationToken);
    }

    private static async Task TaskbarAsync(HostRun run, string line)
    {
        var path = Path.Combine(run.HookDirectory, "taskbar");
        File.WriteAllText(path, line + "\n");
        await TakenAsync(path);
    }

    private static async Task<JsonObject> InvokeAsync(HostRun run, string command, JsonObject args)
    {
        await TakenAsync(Path.Combine(run.HookDirectory, "invoke"));
        var before = (await run.WaitForStatusAsync())["lastInvoke"]?.ToJsonString();
        File.WriteAllText(Path.Combine(run.HookDirectory, "invoke"), $"{command}\nkeyboard\n{args.ToJsonString()}\n");
        var status = await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == command
            && last.ToJsonString() != before, Reaction);
        return status["lastInvoke"]!.AsObject();
    }
}

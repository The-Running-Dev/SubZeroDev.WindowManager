using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S27: quick settings. Under the test hook the plugin's radios and display are the file <c>quicksettings.json</c>
/// in the hook directory (invariant I38: a test never changes the machine's radios, brightness or settings) and every
/// accepted change is appended to <c>desktop-actions.log</c>. The Windows-key hook acts only on keys a test injects with
/// its marker, so Win+A never reaches Windows; the negative case (no provider, key passes through) is a host test.
/// </summary>
public sealed class QuickSettingsTests
{
    private const string QuickSettings = "subzerodev.quick-settings";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StatusCadence = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task S27_1_the_tiles_show_the_real_values_follow_changes_within_a_second_and_a_missing_device_has_no_tile()
    {
        HostRun.RequireDesktop();
        using var run = Start(Devices(wifi: true, bluetooth: false, brightness: null));

        var status = await run.WaitForAsync(s => Tile(s, "wifi") is not null && Tile(s, "doNotDisturb") is not null, Startup);
        Assert.Equal(true, (bool?)Tile(status, "wifi")!["value"]);
        Assert.Equal(false, (bool?)Tile(status, "bluetooth")!["value"]);
        Assert.Equal(false, (bool?)Tile(status, "airplane")!["value"]);
        Assert.Equal("toggle", (string?)Tile(status, "wifi")!["kind"]);
        Assert.Null(Tile(status, "brightness"));

        var changed = Stopwatch.StartNew();
        Write(run, Devices(wifi: false, bluetooth: false, brightness: null));
        status = await run.WaitForAsync(s => (bool?)Tile(s, "wifi")?["value"] == false, Reaction);
        Assert.True(changed.Elapsed < TimeSpan.FromSeconds(1) + StatusCadence,
            $"The tile took {changed.Elapsed.TotalMilliseconds:F0} ms to show the change.");
        Assert.Equal(true, (bool?)Tile(status, "airplane")!["value"]);

        // A display that appears gets its tile as a level.
        Write(run, Devices(wifi: false, bluetooth: false, brightness: 0.4));
        status = await run.WaitForAsync(s => Tile(s, "brightness") is not null, Reaction);
        Assert.Equal("level", (string?)Tile(status, "brightness")!["kind"]);
        Assert.InRange((double)Tile(status, "brightness")!["value"]!, 0.39, 0.41);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S27_2_a_change_goes_through_the_port_and_the_tile_shows_what_the_device_reports()
    {
        HostRun.RequireDesktop();
        using var run = Start(Devices(wifi: false, bluetooth: false, brightness: 0.5, step: 0.25));
        await run.WaitForAsync(s => Tile(s, "brightness") is not null, Startup);

        var result = await InvokeAsync(run, "quicksettings.set", new JsonObject { ["id"] = "wifi", ["value"] = true });
        Assert.True((bool?)result["ok"]);
        var status = await run.WaitForAsync(s => (bool?)Tile(s, "wifi")?["value"] == true, Reaction);
        Assert.Equal(true, (bool?)Tile(status, "wifi")!["available"]);
        Assert.Contains("quicksettings set wifi on", DesktopActions(run));

        // The display has four steps: 0.6 lands on 0.5, and the tile shows 0.5, not 0.6 (I34).
        result = await InvokeAsync(run, "quicksettings.set", new JsonObject { ["id"] = "brightness", ["value"] = 0.6 });
        Assert.True((bool?)result["ok"]);
        status = await run.WaitForAsync(s => Tile(s, "brightness") is { } tile && Math.Abs((double)tile["value"]! - 0.5) < 0.001, Reaction);
        Assert.Contains("quicksettings set brightness 0.5", DesktopActions(run));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S27_2_a_refused_change_disables_the_tile_with_a_reason_and_keeps_the_real_value()
    {
        HostRun.RequireDesktop();
        using var run = Start(Devices(wifi: true, bluetooth: false, brightness: null, refuse: ["bluetooth"]));
        await run.WaitForAsync(s => Tile(s, "bluetooth") is not null, Startup);

        var result = await InvokeAsync(run, "quicksettings.set", new JsonObject { ["id"] = "bluetooth", ["value"] = true });
        Assert.False((bool?)result["ok"]);
        Assert.Equal("CommandFailed", (string?)result["code"]);
        Assert.Equal(true, (bool?)result["retryable"]);

        var status = await run.WaitForAsync(s => (bool?)Tile(s, "bluetooth")?["available"] == false, Reaction);
        var tile = Tile(status, "bluetooth")!;
        Assert.Equal(false, (bool?)tile["value"]);
        Assert.False(string.IsNullOrWhiteSpace((string?)tile["reason"]));
        Assert.DoesNotContain(DesktopActions(run), line => line.StartsWith("quicksettings set bluetooth", StringComparison.Ordinal));

        // The other tiles are not affected.
        Assert.Equal(true, (bool?)Tile(status, "wifi")!["available"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S27_2_a_change_to_a_device_that_is_gone_fails_as_absent_and_the_tile_goes()
    {
        HostRun.RequireDesktop();
        using var run = Start(Devices(wifi: true, bluetooth: true, brightness: null));
        await run.WaitForAsync(s => Tile(s, "bluetooth") is not null, Startup);

        // The adapter is removed between the read and the change.
        Write(run, Devices(wifi: true, bluetooth: null, brightness: null));
        var result = await InvokeAsync(run, "quicksettings.set", new JsonObject { ["id"] = "bluetooth", ["value"] = false });
        Assert.False((bool?)result["ok"]);
        Assert.Equal("CommandFailed", (string?)result["code"]);
        Assert.Equal(false, (bool?)result["retryable"]);
        await run.WaitForAsync(s => Tile(s, "bluetooth") is null, Reaction);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S27_3_do_not_disturb_is_the_shells_own_switch_and_the_tile_follows_it()
    {
        HostRun.RequireDesktop();
        using var run = Start(Devices(wifi: true, bluetooth: false, brightness: null));
        var status = await run.WaitForAsync(s => Tile(s, "doNotDisturb") is not null, Startup);
        Assert.Equal(false, (bool?)Tile(status, "doNotDisturb")!["value"]);

        var result = await InvokeAsync(run, "quicksettings.set", new JsonObject { ["id"] = "doNotDisturb", ["value"] = true });
        Assert.True((bool?)result["ok"]);
        await run.WaitForAsync(s => (bool?)Tile(s, "doNotDisturb")?["value"] == true, Reaction);

        // A change made elsewhere in the shell shows too; nothing was written to the device file or log for it.
        await InvokeAsync(run, "host.notifications.setDoNotDisturb", new JsonObject { ["on"] = false });
        await run.WaitForAsync(s => (bool?)Tile(s, "doNotDisturb")?["value"] == false, Reaction);
        Assert.DoesNotContain(DesktopActions(run), line => line.Contains("doNotDisturb", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S27_5_win_a_opens_the_panel_while_the_plugin_runs()
    {
        HostRun.RequireDesktop();
        using var run = Start(Devices(wifi: true, bluetooth: false, brightness: 0.5));
        await run.WaitForAsync(KeysReady, Startup);

        Interactive.PressMarked(Interactive.LeftWin, 'A');
        var status = await run.WaitForAsync(s => Panel(s)["open"]?.GetValue<bool>() == true, Reaction);
        Assert.Contains(Dispatches(status), d => d.StartsWith("send quicksettings.open ", StringComparison.Ordinal) && d.EndsWith(" Hotkey", StringComparison.Ordinal));

        // The panel lists a control for each device that exists, and none for the one that does not.
        var items = Panel(status)["items"]!.AsArray().Select(item => (string?)item!["id"]).ToList();
        Assert.Contains("wifi", items);
        Assert.Contains("brightness", items);
        Assert.Equal(true, (bool?)Item(status, "wifi")!["checked"]);
        Assert.InRange((double)Item(status, "brightness")!["value"]!, 0.49, 0.51);

        // The chord toggles: a second press closes it.
        Interactive.PressMarked(Interactive.LeftWin, 'A');
        await run.WaitForAsync(s => Panel(s)["open"]?.GetValue<bool>() != true, Reaction);
        Assert.Equal(0, await run.StopAsync());
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private static HostRun Start(JsonObject devices)
    {
        var run = HostRun.Create();
        Write(run, devices);
        run.WriteDefaultLayout((Guid.NewGuid(), QuickSettings, "quicksettings"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        return run;
    }

    /// <summary>The fake machine: a null radio or display is absent; <paramref name="step"/> rounds an accepted brightness.</summary>
    private static JsonObject Devices(bool? wifi, bool? bluetooth, double? brightness, double step = 0, string[]? refuse = null)
    {
        var state = new JsonObject();
        if (wifi is { } w)
        {
            state["wifi"] = new JsonObject { ["present"] = true, ["value"] = w };
        }

        if (bluetooth is { } b)
        {
            state["bluetooth"] = new JsonObject { ["present"] = true, ["value"] = b };
        }

        if (brightness is { } level)
        {
            var entry = new JsonObject { ["present"] = true, ["value"] = level };
            if (step > 0)
            {
                entry["step"] = step;
            }

            state["brightness"] = entry;
        }

        if (refuse is not null)
        {
            state["refuse"] = new JsonArray([.. refuse.Select(name => (JsonNode?)name)]);
        }

        return state;
    }

    /// <summary>Replaces the fake machine in one move, so the plugin never reads half a file.</summary>
    private static void Write(HostRun run, JsonObject devices)
    {
        Directory.CreateDirectory(run.HookDirectory);
        var path = Path.Combine(run.HookDirectory, "quicksettings.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, devices.ToJsonString(), new UTF8Encoding(false));
        File.Move(temporary, path, overwrite: true);
    }

    private static async Task<JsonObject> InvokeAsync(HostRun run, string command, JsonObject args)
    {
        var before = (int?)(await run.WaitForStatusAsync())["lastInvoke"]?["count"] ?? 0;
        await File.WriteAllLinesAsync(Path.Combine(run.HookDirectory, "invoke"), [command, "keyboard", args.ToJsonString(), "pointer"], TestContext.Current.CancellationToken);
        var status = await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == command && (int?)last["count"] > before, Reaction);
        return status["lastInvoke"]!.AsObject();
    }

    /// <summary>One entry of the <c>quicksettings.state</c> feed, or null when the setting has no tile.</summary>
    private static JsonObject? Tile(JsonObject status, string id) =>
        (status["feeds"]!.AsArray().FirstOrDefault(f => (string?)f!["id"] == "quicksettings.state")?["snapshot"] as JsonArray)?
            .FirstOrDefault(tile => (string?)tile!["id"] == id)?.AsObject();

    private static JsonObject PluginOf(JsonObject status, string id) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == id)?.AsObject() ?? [];

    private static JsonObject Panel(JsonObject status) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == QuickSettings && (string?)p["popup"] == "panel")?.AsObject() ?? [];

    private static JsonObject? Item(JsonObject status, string id) =>
        Panel(status)["items"]?.AsArray().FirstOrDefault(item => (string?)item!["id"] == id)?.AsObject();

    private static List<string> Dispatches(JsonObject status) => [.. status["dispatches"]!.AsArray().Select(d => (string)d!)];

    /// <summary>Ready, with the hook installed and the plugin running: only then may a test inject keys.</summary>
    private static bool KeysReady(JsonObject status) =>
        (bool?)status["ready"] == true
        && (bool?)status["keyHook"]?["installed"] == true
        && PluginOf(status, QuickSettings)["state"]?.GetValue<string>() == "Running"
        && Tile(status, "wifi") is not null;

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
}

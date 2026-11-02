using System.Diagnostics;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S22: the volume, network and battery indicators. Under the test hook the plugin reads and writes
/// <c>indicators.json</c> in the hook directory rather than the real devices, so no test changes the user's
/// volume, mute state or network, and Settings pages are recorded in <c>desktop-actions.log</c> instead of opened.
/// Popups are opened and driven from the keyboard, each key pressed only once the host holds the foreground.
/// </summary>
public sealed class IndicatorsTests
{
    private const string Indicators = "subzerodev.indicators";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    /// <summary>The status file is written every 250 ms, so a change shows there up to that much after the panel.</summary>
    private static readonly TimeSpan StatusCadence = TimeSpan.FromMilliseconds(250);

    [Fact]
    public async Task S22_1_the_volume_item_follows_the_device_and_its_popup_changes_level_and_mute()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(level: 0.62), "volume");
        var status = await run.WaitForAsync(s => (string?)Item(s, "volume")?["label"] == "62%", Startup);
        Assert.Equal("Volume 62%", (string?)Item(status, "volume")!["tooltip"]);

        // A change made elsewhere shows within 500 ms.
        var changed = Stopwatch.StartNew();
        WriteFake(run, Fake(level: 0.30, muted: true));
        status = await run.WaitForAsync(s => (string?)Item(s, "volume")?["label"] == "Muted", Reaction);
        Assert.True(changed.Elapsed < TimeSpan.FromMilliseconds(500) + StatusCadence,
            $"The volume item took {changed.Elapsed.TotalMilliseconds:F0} ms to show the change.");
        Assert.Equal("Volume 30%, muted", (string?)Item(status, "volume")!["tooltip"]);

        status = await OpenPopupAsync(run, status, "volume", "mute");
        Assert.True((bool?)PopupItem(status, "volume", "mute")!["checked"]);
        Assert.Equal(0.30, (double)PopupItem(status, "volume", "level")!["value"]!, 3);

        // The slider moves by its step, and moving it unmutes as Windows' own does.
        status = await PressAsync(run, status, Interactive.Tab, s => (string?)Popup(s, "volume")["focused"] == "level");
        status = await PressAsync(run, status, Interactive.Right, s => Math.Abs(FakeLevel(run) - 0.32) < 0.001 && !FakeMuted(run));
        status = await run.WaitForAsync(s => (string?)Item(s, "volume")?["label"] == "32%", Reaction);
        Assert.Contains("32%", Popup(status, "volume")["texts"]!.AsArray().Select(t => (string?)t));

        // The mute toggle mutes again.
        status = await PressAsync(run, status, Interactive.Shift, Interactive.Tab, s => (string?)Popup(s, "volume")["focused"] == "mute");
        await PressAsync(run, status, Interactive.Space, s => FakeMuted(run) && (string?)Item(s, "volume")?["label"] == "Muted");
        Assert.Equal(0.32, FakeLevel(run), 3);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S22_2_the_network_item_shows_the_connection_and_its_popup_opens_network_settings()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(network: "connected"), "network");
        var status = await run.WaitForAsync(s => (string?)Item(s, "network")?["label"] == "Online", Startup);
        Assert.Equal("Connected to the internet", (string?)Item(status, "network")!["tooltip"]);

        foreach (var (network, label, tooltip) in new[]
        {
            ("limited", "Limited", "Connected, no internet"),
            ("disconnected", "Offline", "Not connected"),
        })
        {
            var changed = Stopwatch.StartNew();
            WriteFake(run, Fake(network: network));
            status = await run.WaitForAsync(s => (string?)Item(s, "network")?["label"] == label, Reaction);
            Assert.True(changed.Elapsed < TimeSpan.FromSeconds(2), $"The network item took {changed.Elapsed.TotalMilliseconds:F0} ms to show {network}.");
            Assert.Equal(tooltip, (string?)Item(status, "network")!["tooltip"]);
        }

        status = await OpenPopupAsync(run, status, "network", "settings");
        Assert.Contains("Not connected", Popup(status, "network")["texts"]!.AsArray().Select(t => (string?)t));
        await PressAsync(run, status, Interactive.Enter, s => Popup(s, "network")["open"]?.GetValue<bool>() != true);
        Assert.Contains("settings-open ms-settings:network", DesktopActions(run));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S22_3_the_battery_item_shows_the_charge_hides_without_a_battery_and_opens_battery_settings()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(battery: (80, true)), "battery", "volume");
        var status = await run.WaitForAsync(s => (string?)Item(s, "battery")?["label"] == "80%", Startup);
        Assert.Equal("80%, charging", (string?)Item(status, "battery")!["tooltip"]);
        Assert.True((bool?)Item(status, "battery")!["visible"]);

        WriteFake(run, Fake(battery: (79, false)));
        status = await run.WaitForAsync(s => (string?)Item(s, "battery")?["tooltip"] == "79% remaining", Reaction);
        Assert.Equal("79%", (string?)Item(status, "battery")!["label"]);

        status = await OpenPopupAsync(run, status, "battery", "settings");
        Assert.Contains("79% remaining", Popup(status, "battery")["texts"]!.AsArray().Select(t => (string?)t));
        await PressAsync(run, status, Interactive.Enter, s => Popup(s, "battery")["open"]?.GetValue<bool>() != true);
        Assert.Contains("settings-open ms-settings:batterysaver", DesktopActions(run));

        // Without a battery the item leaves the panel; the volume item beside it stays.
        WriteFake(run, Fake(battery: null));
        status = await run.WaitForAsync(s => (bool?)Item(s, "battery")?["visible"] == false, Reaction);
        Assert.True((bool?)Item(status, "volume")!["visible"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S22_4_the_slider_is_styled_by_the_theme_and_the_arrow_keys_move_it_by_its_step()
    {
        HostRun.RequireDesktop();
        using var run = Start(Fake(level: 0.50), "volume");
        var status = await run.WaitForAsync(s => (string?)Item(s, "volume")?["label"] == "50%", Startup);

        status = await OpenPopupAsync(run, status, "volume", "mute");
        status = await PressAsync(run, status, Interactive.Tab, s => (string?)Popup(s, "volume")["focused"] == "level");

        // Wm.Element.Slider gives it a minimum width of 160 pixels at 100% scale.
        var slider = PopupItem(status, "volume", "level")!;
        Assert.Equal("Wm.Element.Slider", (string?)slider["style"]);
        var bounds = slider["bounds"]!;
        Assert.True((int)bounds["right"]! - (int)bounds["left"]! >= 160, $"The slider is {slider.ToJsonString()}.");

        status = await PressAsync(run, status, Interactive.Left, s => Math.Abs(FakeLevel(run) - 0.48) < 0.001);
        status = await PressAsync(run, status, Interactive.Left, s => Math.Abs(FakeLevel(run) - 0.46) < 0.001);
        status = await PressAsync(run, status, Interactive.Up, s => Math.Abs(FakeLevel(run) - 0.48) < 0.001);
        await run.WaitForAsync(s => Math.Abs((double)PopupItem(s, "volume", "level")!["value"]! - 0.48) < 0.001, Reaction);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S22_5_replacement_mode_places_the_indicators_and_the_coexistence_default_does_not()
    {
        HostRun.RequireDesktop();
        using (var run = HostRun.Create())
        {
            WriteFake(run, Fake());
            run.Start(HostRun.HostExe, "--mode", "replacement");
            var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && Instances(s).Count == 3
                && Instances(s).All(c => c["interactive"]!.AsArray().Count > 0), Startup);
            Assert.Equal(["volume", "network", "battery"],
                Instances(status).Select(c => (string?)c["interactive"]![0]!["id"]).Order().Reverse());
            Assert.Equal(0, await run.StopAsync());
        }

        using (var run = HostRun.Create())
        {
            WriteFake(run, Fake());
            run.Start(HostRun.HostExe);
            var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && s["contributions"]!.AsArray().Count > 0, Startup);
            await Task.Delay(1000, TestContext.Current.CancellationToken);
            status = await run.WaitForStatusAsync();
            Assert.Empty(Instances(status));
            Assert.Equal(0, await run.StopAsync());
        }
    }

    private static HostRun Start(JsonObject fake, params string[] contributions)
    {
        var run = HostRun.Create();
        WriteFake(run, fake);
        run.WriteDefaultLayout([.. contributions.Select(c => (Guid.NewGuid(), Indicators, c))]);
        run.Start(HostRun.HostExe);
        return run;
    }

    internal static JsonObject Fake(double level = 0.5, bool muted = false, string network = "connected", (int Percent, bool Charging)? battery = null) => new()
    {
        ["volume"] = new JsonObject { ["present"] = true, ["level"] = level, ["muted"] = muted },
        ["network"] = network,
        ["battery"] = battery is { } b
            ? new JsonObject { ["present"] = true, ["percent"] = b.Percent, ["charging"] = b.Charging }
            : new JsonObject { ["present"] = false },
    };

    /// <summary>Replaces the fake readings in one move, so the plugin never reads half a file.</summary>
    internal static void WriteFake(HostRun run, JsonObject fake)
    {
        Directory.CreateDirectory(run.HookDirectory);
        var path = Path.Combine(run.HookDirectory, "indicators.json");
        var temporary = path + ".tmp";
        File.WriteAllText(temporary, fake.ToJsonString());
        File.Move(temporary, path, overwrite: true);
    }

    private static JsonObject ReadFake(HostRun run)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(Path.Combine(run.HookDirectory, "indicators.json"), FileMode.Open, FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);
                return JsonNode.Parse(stream)!.AsObject();
            }
            catch (Exception e) when (attempt < 5 && e is IOException or System.Text.Json.JsonException)
            {
                Thread.Sleep(20);
            }
        }
    }

    private static double FakeLevel(HostRun run) => (double)ReadFake(run)["volume"]!["level"]!;

    private static bool FakeMuted(HostRun run) => (bool)ReadFake(run)["volume"]!["muted"]!;

    private static List<JsonObject> Instances(JsonObject status) =>
        [.. status["contributions"]!.AsArray().Where(c => (string?)c!["plugin"] == Indicators).Select(c => c!.AsObject())];

    private static JsonObject? Item(JsonObject status, string id) =>
        Instances(status).Select(c => c["interactive"]?.AsArray().FirstOrDefault(e => (string?)e!["id"] == id)?.AsObject())
            .FirstOrDefault(e => e is not null);

    private static JsonObject Popup(JsonObject status, string popup) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == Indicators && (string?)p["popup"] == popup)?.AsObject() ?? [];

    private static JsonObject? PopupItem(JsonObject status, string popup, string id) =>
        Popup(status, popup)["items"]?.AsArray().FirstOrDefault(i => (string?)i!["id"] == id)?.AsObject();

    private static int HostPid(JsonObject status) => Interactive.ProcessOf((nint)(long)status["panels"]![0]!["handle"]!);

    /// <summary>Focuses the panel's first item, then opens its popup with Enter once the host holds the foreground.</summary>
    private static async Task<JsonObject> OpenPopupAsync(HostRun run, JsonObject status, string item, string focusedInPopup)
    {
        File.WriteAllText(Path.Combine(run.HookDirectory, "focus-panel"), (string)status["panels"]![0]!["id"]!);
        status = await run.WaitForAsync(s => (string?)s["panels"]![0]!["focused"] == item, Reaction);
        return await PressAsync(run, status, Interactive.Enter, s => Popup(s, item)["open"]?.GetValue<bool>() == true
            && (string?)Popup(s, item)["focused"] == focusedInPopup);
    }

    /// <summary>Presses the keys only while the host holds the foreground, so they never reach another application.</summary>
    private static Task<JsonObject> PressAsync(HostRun run, JsonObject status, ushort key, Func<JsonObject, bool> then) =>
        PressAsync(run, status, [key], then);

    private static Task<JsonObject> PressAsync(HostRun run, JsonObject status, ushort modifier, ushort key, Func<JsonObject, bool> then) =>
        PressAsync(run, status, [modifier, key], then);

    private static async Task<JsonObject> PressAsync(HostRun run, JsonObject status, ushort[] keys, Func<JsonObject, bool> then)
    {
        Assert.Equal(HostPid(status), Interactive.ForegroundProcessId());
        Interactive.Press(keys);
        return await run.WaitForAsync(then, Reaction);
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
}

internal static partial class Interactive
{
    public const ushort Left = 0x25;
    public const ushort Right = 0x27;
}

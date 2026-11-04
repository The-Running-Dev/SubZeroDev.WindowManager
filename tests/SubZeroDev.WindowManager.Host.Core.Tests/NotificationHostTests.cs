using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core.Tests;

/// <summary>
/// Slice S24, host side: popup placement in the manifest, the trigger on <c>command.invoke</c>, the
/// <c>windowsKeys</c> and <c>toastCorner</c> settings, and the chord table that decides which Windows-key
/// chords are swallowed. No hook is installed and no key reaches Windows.
/// </summary>
public sealed class NotificationHostTests
{
    private const uint LeftWin = WindowsKeyTracker.LeftWin;

    private static JsonObject Manifest(string popups, string hostApi = "^1.1") =>
        JsonNode.Parse($$"""
            {"manifestVersion":1,"id":"samples.notes","version":"1.0.0","displayName":"P","publisher":"T","description":"D",
             "hostApi":"{{hostApi}}","entry":{"path":"p.exe"},"modes":["replacement"],"popups":{{popups}}}
            """)!.AsObject();

    private static JsonObject Settings(Action<JsonObject> change)
    {
        var settings = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["activePreset"] = "subzerodev.default",
            ["startAtSignIn"] = false,
            ["plugins"] = new JsonObject(),
        };
        change(settings);
        return settings;
    }

    [Fact]
    public void S24_3_a_popup_may_declare_toast_overlay_or_anchored_and_defaults_to_anchored()
    {
        var problems = new List<string>();

        var manifest = PluginManifestReader.Parse(
            Manifest("""[{"id":"toast","placement":"toast"},{"id":"full","placement":"overlay"},{"id":"menu","placement":"anchored"},{"id":"plain"}]"""),
            problems);

        Assert.Empty(problems);
        Assert.Equal(PopupPlacement.Toast, manifest!.PlacementOf("toast"));
        Assert.Equal(PopupPlacement.Overlay, manifest.PlacementOf("full"));
        Assert.Equal(PopupPlacement.Anchored, manifest.PlacementOf("menu"));
        Assert.Equal(PopupPlacement.Anchored, manifest.PlacementOf("plain"));
        Assert.Equal(PopupPlacement.Anchored, manifest.PlacementOf("undeclared"));
        Assert.Equal(["toast", "full", "menu", "plain"], manifest.Popups);
    }

    [Theory]
    [InlineData("\"floating\"", "floating")]
    [InlineData("7", "7")]
    public void S24_3_an_unknown_or_non_string_placement_makes_the_manifest_invalid(string placement, string shown)
    {
        var problems = new List<string>();

        var manifest = PluginManifestReader.Parse(Manifest($$"""[{"id":"toast","placement":{{placement}}}]"""), problems);

        Assert.Null(manifest);
        Assert.Contains(problems, p => p.StartsWith("popup 'toast' has placement", StringComparison.Ordinal)
            && p.EndsWith("use anchored, toast or overlay.", StringComparison.Ordinal)
            && p.Contains(shown, StringComparison.Ordinal));
    }

    [Fact]
    public void S24_a_command_or_feed_id_may_use_camel_case_after_its_first_segment()
    {
        var accepted = new List<string>();
        var manifest = PluginManifestReader.Parse(JsonNode.Parse("""
            {"manifestVersion":1,"id":"samples.notes","version":"1.0.0","displayName":"P","publisher":"T","description":"D",
             "hostApi":"^1.1","entry":{"path":"p.exe"},"modes":["replacement"],
             "commands":[{"id":"notes.openCentre"}],"subscribes":["host.notes.doNotDisturb"]}
            """)!.AsObject(), accepted);
        Assert.Empty(accepted);
        Assert.NotNull(manifest);

        var rejected = new List<string>();
        PluginManifestReader.Parse(JsonNode.Parse("""
            {"manifestVersion":1,"id":"samples.notes","version":"1.0.0","displayName":"P","publisher":"T","description":"D",
             "hostApi":"^1.1","entry":{"path":"p.exe"},"modes":["replacement"],"commands":[{"id":"Notes.open"}]}
            """)!.AsObject(), rejected);
        Assert.Contains(rejected, p => p.Contains("'Notes.open' is not valid", StringComparison.Ordinal));
    }

    [Fact]
    public void S24_a_first_release_manifest_is_unchanged()
    {
        var problems = new List<string>();

        var manifest = PluginManifestReader.Parse(Manifest("""[{"id":"menu"}]""", hostApi: "^1.0"), problems);

        Assert.Empty(problems);
        Assert.Equal(PopupPlacement.Anchored, manifest!.PlacementOf("menu"));
        Assert.True(ProtocolInfo.HostApiVersion >= new SemVersion(1, 0, 0));
        Assert.True(manifest.HostApi.IsSatisfiedBy(ProtocolInfo.HostApiVersion));
    }

    [Fact]
    public void S24_the_trigger_travels_on_the_wire_only_when_given()
    {
        var chord = new CommandInvokeParams("a.b", null, InvocationSource.Hotkey, CommandTrigger.Chord);
        var plain = new CommandInvokeParams("a.b", null, InvocationSource.Pointer);

        var withTrigger = ProtocolJson.ToNode(chord)!.ToJsonString();
        var without = ProtocolJson.ToNode(plain)!.ToJsonString();

        Assert.Contains("\"trigger\":\"chord\"", withTrigger, StringComparison.Ordinal);
        Assert.DoesNotContain("trigger", without, StringComparison.Ordinal);
        Assert.Null(JsonSerializer.Deserialize<CommandInvokeParams>(without, ProtocolJson.Options)!.Trigger);
        Assert.Equal(CommandTrigger.Chord, JsonSerializer.Deserialize<CommandInvokeParams>(withTrigger, ProtocolJson.Options)!.Trigger);
    }

    [Fact]
    public void S24_6_windows_keys_and_the_toast_corner_parse_and_round_trip()
    {
        var problems = new List<string>();
        var json = Settings(s =>
        {
            s["windowsKeys"] = new JsonObject { ["Win+N"] = "notifications.openCentre", ["Win+Shift+S"] = "samples.snip" };
            s["toastCorner"] = "topLeft";
        });

        var parsed = Documents.ParseHostSettings(json, problems);

        Assert.Empty(problems);
        Assert.Equal("samples.snip", parsed!.WindowsKeys["Win+Shift+S"]);
        Assert.Equal(ToastCorner.TopLeft, parsed.ToastCorner);

        var again = Documents.ParseHostSettings(parsed.ToJson(), problems)!;
        Assert.Equal(parsed.WindowsKeys, again.WindowsKeys);
        Assert.Equal(ToastCorner.TopLeft, again.ToastCorner);
        Assert.Equal(1, parsed.ToJson()["schemaVersion"]!.GetValue<int>());
    }

    [Fact]
    public void S24_a_settings_file_without_the_new_fields_still_parses_and_writes_without_them()
    {
        var problems = new List<string>();

        var parsed = Documents.ParseHostSettings(Settings(_ => { }), problems);

        Assert.Empty(problems);
        Assert.Empty(parsed!.WindowsKeys);
        Assert.Null(parsed.ToastCorner);
        Assert.Null(parsed.ToJson()["windowsKeys"]);
        Assert.Null(parsed.ToJson()["toastCorner"]);
    }

    [Fact]
    public void S24_a_badly_typed_windows_keys_or_toast_corner_is_a_settings_problem()
    {
        var problems = new List<string>();

        Assert.Null(Documents.ParseHostSettings(Settings(s => s["windowsKeys"] = "Win+N"), problems));
        Assert.Null(Documents.ParseHostSettings(Settings(s => s["windowsKeys"] = new JsonObject { ["Win+N"] = 4 }), problems));
        Assert.Null(Documents.ParseHostSettings(Settings(s => s["toastCorner"] = "middle"), problems));

        Assert.Contains("windowsKeys: must be an object.", problems);
        Assert.Contains("windowsKeys.Win+N: must be a command id.", problems);
        Assert.Contains("toastCorner: must be topLeft, topRight, bottomLeft or bottomRight.", problems);
    }

    [Theory]
    [InlineData("Win+N", true)]
    [InlineData("Win+Shift+S", true)]
    [InlineData("Alt+Tab", true)]
    [InlineData("Alt+Shift+Tab", true)]
    [InlineData("Ctrl+Alt+7", true)]
    [InlineData("Win", false)]
    [InlineData("N", false)]
    [InlineData("Win+", false)]
    [InlineData("Hyper+N", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void S24_6_the_chord_grammar_matches_the_contract(string? text, bool valid) =>
        Assert.Equal(valid, WindowsKeyTable.IsChord(text));

    [Fact]
    public void S24_6_win_n_is_swallowed_only_while_the_centre_command_is_available()
    {
        var table = new WindowsKeyTable(new Dictionary<string, string>(), _ => { });

        Assert.False(table.SwallowsWithWin('N'));

        table.Refresh(id => id == WindowsKeyTable.NotificationCentreCommand, _ => true);
        Assert.True(table.SwallowsWithWin('N'));
        Assert.Equal(WindowsKeyTable.NotificationCentreCommand, table.Find(new KeyChord(KeyModifiers.Win, 'N'))!.CommandId);

        table.Refresh(id => id == WindowsKeyTable.NotificationCentreCommand, _ => false);
        Assert.False(table.SwallowsWithWin('N'));
        Assert.Null(table.Find(new KeyChord(KeyModifiers.Win, 'N')));

        table.Refresh(_ => false, _ => true);
        Assert.False(table.SwallowsWithWin('N'));
    }

    [Fact]
    public void S27_5_win_a_is_swallowed_only_while_the_quick_settings_command_is_available()
    {
        var table = new WindowsKeyTable(new Dictionary<string, string>(), _ => { });
        var chord = new KeyChord(KeyModifiers.Win, 'A');

        Assert.False(table.SwallowsWithWin('A'));

        table.Refresh(id => id == WindowsKeyTable.QuickSettingsCommand, _ => true);
        Assert.True(table.SwallowsWithWin('A'));
        Assert.Equal(WindowsKeyTable.QuickSettingsCommand, table.Find(chord)!.CommandId);

        table.Refresh(id => id == WindowsKeyTable.QuickSettingsCommand, _ => false);
        Assert.False(table.SwallowsWithWin('A'));
        Assert.Null(table.Find(chord));

        table.Refresh(_ => false, _ => true);
        Assert.False(table.SwallowsWithWin('A'));
    }

    [Fact]
    public void S28_2_win_x_is_swallowed_only_while_the_power_menu_command_is_available()
    {
        var table = new WindowsKeyTable(new Dictionary<string, string>(), _ => { });
        var chord = new KeyChord(KeyModifiers.Win, 'X');

        Assert.False(table.SwallowsWithWin('X'));

        table.Refresh(id => id == WindowsKeyTable.PowerMenuCommand, _ => true);
        Assert.True(table.SwallowsWithWin('X'));
        Assert.Equal(WindowsKeyTable.PowerMenuCommand, table.Find(chord)!.CommandId);

        // The provider stops, or nothing declares the command: the key goes back to Windows (I35).
        table.Refresh(id => id == WindowsKeyTable.PowerMenuCommand, _ => false);
        Assert.False(table.SwallowsWithWin('X'));
        Assert.Null(table.Find(chord));

        table.Refresh(_ => false, _ => true);
        Assert.False(table.SwallowsWithWin('X'));
    }

    [Fact]
    public void S28_1_every_default_chord_is_bound_and_an_unknown_chord_or_command_is_ignored_with_a_diagnostic()
    {
        var warnings = new List<string>();
        var table = new WindowsKeyTable(
            new Dictionary<string, string> { ["Win+Nonsense"] = "samples.a", ["Windows"] = "samples.b", ["Win+Shift+S"] = "samples.snip" },
            warnings.Add);

        // Every first-release chord, Win+N, Win+A, Win+X and the five desktop chords have a binding.
        var bound = table.Bindings.Select(b => b.Chord).ToHashSet();
        Assert.All(WindowsKeyBindings.Defaults, binding => Assert.Contains(binding.Chord, bound));
        Assert.Contains(new KeyChord(KeyModifiers.Win, 'N'), bound);
        Assert.Contains(new KeyChord(KeyModifiers.Win, 'A'), bound);
        Assert.Contains(new KeyChord(KeyModifiers.Win, 'X'), bound);
        Assert.All(WindowsKeyTable.DesktopBindings, binding => Assert.Contains(binding.Chord, bound));

        // A malformed chord is dropped with a diagnostic; a well-formed one naming a command nothing declares is left to Windows.
        Assert.Equal(2, warnings.Count(w => w.Contains("is not a key chord", StringComparison.Ordinal)));
        table.Refresh(_ => false, _ => true);
        Assert.False(table.SwallowsWithWin('S'));
        var unresolved = new List<string>();
        table.ReportUnresolved(_ => false, unresolved.Add);
        Assert.Contains(unresolved, w => w.Contains("→ samples.snip", StringComparison.Ordinal));
    }

    [Fact]
    public void S24_6_the_first_release_chords_stay_swallowed_with_no_plugin_running()
    {
        var table = new WindowsKeyTable(new Dictionary<string, string>(), _ => { });

        table.Refresh(_ => false, _ => false);

        Assert.All(new uint[] { 'R', 'E', 'D', 'M' }, vk => Assert.True(table.SwallowsWithWin(vk)));
        Assert.False(table.SwallowsWithWin('X'));
    }

    [Fact]
    public void S24_6_a_user_chord_replaces_a_default_and_is_swallowed_only_while_it_can_be_answered()
    {
        var table = new WindowsKeyTable(
            new Dictionary<string, string> { ["Win+E"] = "samples.files", ["Win+Shift+S"] = "samples.snip" }, _ => { });

        table.Refresh(_ => false, _ => false);
        Assert.True(table.SwallowsWithWin('E'));
        Assert.Equal("host.files.open", table.Find(new KeyChord(KeyModifiers.Win, 'E'))!.CommandId);

        table.Refresh(id => id == "samples.files", _ => true);
        Assert.True(table.SwallowsWithWin('E'));
        Assert.Equal("samples.files", table.Find(new KeyChord(KeyModifiers.Win, 'E'))!.CommandId);
        Assert.False(table.SwallowsWithWin('S'));
        Assert.Single(table.Unresolved(id => id == "samples.files"), u => u.EndsWith("→ samples.snip", StringComparison.Ordinal));
    }

    [Fact]
    public void S24_6_a_command_nothing_declares_is_reported_once_until_it_resolves()
    {
        var warnings = new List<string>();
        var table = new WindowsKeyTable(new Dictionary<string, string> { ["Win+Shift+S"] = "samples.snip" }, _ => { });

        table.ReportUnresolved(_ => false, warnings.Add);
        table.ReportUnresolved(_ => false, warnings.Add);

        var perPass = 4 + WindowsKeyTable.DesktopBindings.Count;
        Assert.Equal(perPass, warnings.Count);
        Assert.Contains(warnings, w => w.Contains("→ samples.snip", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("→ powermenu.open", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("→ notifications.openCentre", StringComparison.Ordinal));
        Assert.Contains(warnings, w => w.Contains("→ quicksettings.open", StringComparison.Ordinal));

        table.ReportUnresolved(_ => true, warnings.Add);
        table.ReportUnresolved(_ => false, warnings.Add);

        Assert.Equal(2 * perPass, warnings.Count);
    }

    [Fact]
    public void S24_6_concurrent_refreshes_do_not_fault_and_the_last_one_stands()
    {
        var table = new WindowsKeyTable(new Dictionary<string, string>(), _ => { });

        Parallel.For(0, 200, i =>
        {
            var on = i % 2 == 0;
            table.Refresh(_ => on, _ => on);
            _ = table.SwallowsWithWin('N');
        });

        table.Refresh(_ => true, _ => true);
        Assert.True(table.SwallowsWithWin('N'));
        table.Refresh(_ => false, _ => false);
        Assert.False(table.SwallowsWithWin('N'));
    }

    [Fact]
    public void S24_6_an_unknown_chord_is_ignored_with_a_diagnostic()
    {
        var warnings = new List<string>();

        var table = new WindowsKeyTable(
            new Dictionary<string, string> { ["Hyper+N"] = "samples.x", ["Win+"] = "samples.y" }, warnings.Add);
        table.Refresh(_ => true, _ => true);

        Assert.Equal(2, warnings.Count);
        Assert.All(warnings, w => Assert.EndsWith("is not a key chord; ignored.", w, StringComparison.Ordinal));
        Assert.DoesNotContain(table.Bindings, b => b.CommandId is "samples.x" or "samples.y");
    }

    [Fact]
    public void S24_do_not_disturb_reads_an_on_flag_and_publishes_it_as_the_shells_own_feed()
    {
        Assert.True(DoNotDisturbFeed.ParseOn(new JsonObject { ["on"] = true }));
        Assert.False(DoNotDisturbFeed.ParseOn(new JsonObject { ["on"] = false }));
        Assert.Null(DoNotDisturbFeed.ParseOn(null));
        Assert.Null(DoNotDisturbFeed.ParseOn(new JsonObject { ["on"] = "yes" }));
        Assert.Null(DoNotDisturbFeed.ParseOn([]));
        Assert.True(DoNotDisturbFeed.Snapshot(true)["on"]!.GetValue<bool>());
        Assert.Equal("host.notifications.setDoNotDisturb", DoNotDisturbFeed.Command);
    }

    [Fact]
    public void S24_6_the_tracker_swallows_win_n_only_while_the_table_says_so()
    {
        var table = new WindowsKeyTable(new Dictionary<string, string>(), _ => { });
        var tracker = new WindowsKeyTracker(table.SwallowsWithWin);

        tracker.OnKey(LeftWin, down: true);
        var refused = tracker.OnKey('N', down: true);
        tracker.OnKey('N', down: false);
        tracker.OnKey(LeftWin, down: false);

        table.Refresh(_ => true, _ => true);
        tracker.OnKey(LeftWin, down: true);
        var taken = tracker.OnKey('N', down: true);
        var takenUp = tracker.OnKey('N', down: false);
        tracker.OnKey(LeftWin, down: false);

        Assert.False(refused.Swallow);
        Assert.Null(refused.Chord);
        Assert.True(taken.Swallow);
        Assert.Equal(new KeyChord(KeyModifiers.Win, 'N'), taken.Chord);
        Assert.True(takenUp.Swallow);
    }

    [Theory]
    [InlineData(ToastCorner.TopLeft, 12, 12)]
    [InlineData(ToastCorner.TopRight, 1508, 12)]
    [InlineData(ToastCorner.BottomLeft, 12, 688)]
    [InlineData(ToastCorner.BottomRight, 1508, 688)]
    public void S24_3_a_toast_sits_in_the_chosen_corner_of_the_work_area(ToastCorner corner, int left, int top)
    {
        var rect = ToastPlacement.Place(new RectInt(0, 0, 1920, 1040), 400, 340, corner, 12);

        Assert.Equal(new RectInt(left, top, left + 400, top + 340), rect);
    }

    [Fact]
    public void S24_3_a_toast_taller_than_the_work_area_stays_on_the_monitor()
    {
        var rect = ToastPlacement.Place(new RectInt(100, 50, 600, 450), 300, 900, ToastCorner.BottomRight, 12);

        Assert.Equal(50, rect.Top);
        Assert.Equal(288, rect.Left);
    }

    [Fact]
    public void S24_3_manifest_placement_defaults_to_anchored_for_an_undeclared_popup()
    {
        var problems = new List<string>();
        var manifest = PluginManifestReader.Parse(new JsonObject
        {
            ["manifestVersion"] = 1,
            ["id"] = "x.y",
            ["name"] = "Y",
            ["version"] = "1.0.0",
            ["hostApi"] = "^1.1",
            ["modes"] = new JsonArray("replacement"),
            ["entry"] = new JsonObject { ["path"] = "y.exe" },
            ["popups"] = new JsonArray(new JsonObject { ["id"] = "a" }, new JsonObject { ["id"] = "t", ["placement"] = "toast" }),
        }, problems);

        Assert.Empty(problems);
        Assert.NotNull(manifest);
        Assert.Equal(PopupPlacement.Anchored, manifest.PlacementOf("a"));
        Assert.Equal(PopupPlacement.Toast, manifest.PlacementOf("t"));
        Assert.Equal(PopupPlacement.Anchored, manifest.PlacementOf("missing"));
    }
}

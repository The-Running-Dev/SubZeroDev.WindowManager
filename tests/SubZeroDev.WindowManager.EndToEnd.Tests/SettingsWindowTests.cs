using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Windows.Automation;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S10: the settings window. The test hook's <c>settings-ui</c> file drives it as a user would (pick a page,
/// select a plugin, press a button, set a control) and the status snapshot reports what it shows.
/// </summary>
public sealed partial class SettingsWindowTests
{
    private const string Clock = "subzerodev.clock";
    private const string Incompatible = "samples.incompatible";
    private const string Broken = "samples.broken";
    private const string Formed = "samples.formed";
    private const string Faulty = "samples.faulty";

    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task S10_1_and_S10_3_the_plugins_page_lists_every_plugin_and_an_incompatible_one_never_starts()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Incompatible, null, manifest => manifest["hostApi"] = "^2.0");
        run.CopyPlugin(HostRun.TestPluginDirectory, Broken, null, manifest => manifest["version"] = "not-a-version");
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"), (Guid.NewGuid(), Incompatible, "probe"));
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1 && Reason(s, Incompatible) == "Incompatible", Startup);

        await File.WriteAllLinesAsync(Path.Combine(run.HookDirectory, "invoke"),
            ["host.settings.open", "keyboard", new JsonObject { ["page"] = "plugins" }.ToJsonString()], TestContext.Current.CancellationToken);
        var status = await run.WaitForAsync(s => Window(s) is { } w && (bool)w["open"]! && (string?)w["page"] == "plugins", Reaction);

        var clock = Row(status, Clock);
        Assert.Equal("0.1.0", (string?)clock["version"]);
        Assert.Equal("BuiltIn", (string?)clock["source"]);
        Assert.Equal("Running", (string?)clock["state"]);
        Assert.Contains("0.1.0", (string?)clock["text"], StringComparison.Ordinal);

        var broken = Row(status, Broken);
        Assert.Equal("User", (string?)broken["source"]);
        Assert.Equal("Invalid", (string?)broken["state"]);
        Assert.Contains(broken["problems"]!.AsArray(), problem => (string?)problem!["code"] == "PackageInvalid"
            && ((string?)problem["detail"])!.Contains("version", StringComparison.OrdinalIgnoreCase));

        var incompatible = Row(status, Incompatible);
        Assert.Equal("Incompatible", (string?)incompatible["state"]);
        Assert.Contains(incompatible["problems"]!.AsArray(), problem => (string?)problem!["code"] == "HostApiMismatch");
        Assert.Equal("Incompatible", Reason(status, Incompatible));
        Assert.Null(Plugin(status, Incompatible)?["pid"]);

        status = await SettingsUiAsync(run, new JsonObject { ["select"] = Incompatible });
        var detail = Window(status)!["detail"]!;
        Assert.Contains(detail["problems"]!.AsArray(), text => ((string?)text)!.Contains("HostApiMismatch", StringComparison.Ordinal));
        Assert.True((bool)detail["disable"]!);
        Assert.False((bool)detail["retry"]!);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S10_2_disable_stops_the_worker_persists_and_survives_a_restart_and_enable_brings_it_back()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        run.Start(HostRun.HostExe);
        var status = await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1 && Plugin(s, Clock)?["pid"] is not null, Startup);
        using var worker = Process.GetProcessById((int)Plugin(status, Clock)!["pid"]!);

        var disabled = Stopwatch.StartNew();
        status = await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" }, new JsonObject { ["select"] = Clock },
            new JsonObject { ["invoke"] = "Plugin.Disable" });
        Assert.True(worker.WaitForExit(TimeSpan.FromSeconds(3) - disabled.Elapsed), "The clock worker was still running 3 s after Disable.");
        status = await run.WaitForAsync(s => Reason(s, Clock) == "Disabled" && State(s, Clock) == "Disabled", Reaction);
        Assert.Null(Plugin(status, Clock)?["pid"]);
        Assert.False(Enabled(run, Clock));
        var detail = Window(status)!["detail"]!;
        Assert.True((bool)detail["enable"]!);
        Assert.False((bool)detail["disable"]!);

        Assert.Equal(0, await Restart(run));
        status = await run.WaitForAsync(s => Reason(s, Clock) == "Disabled", Startup);
        Assert.Null(Plugin(status, Clock)?["pid"]);
        status = await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" }, new JsonObject { ["select"] = Clock });
        Assert.Equal("Disabled", (string?)Row(status, Clock)["state"]);

        await SettingsUiAsync(run, new JsonObject { ["invoke"] = "Plugin.Enable" });
        status = await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1 && State(s, Clock) == "Running", Startup);
        Assert.True(Enabled(run, Clock));
        Assert.NotNull(Plugin(status, Clock)?["pid"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S10_4_the_instance_form_follows_the_schema_and_save_writes_the_layout()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Formed, ["faulty"], manifest =>
            manifest["contributions"]![0]!["settingsSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("fast", "slow"), ["default"] = "fast" },
                    ["loud"] = new JsonObject { ["type"] = "boolean", ["default"] = false },
                    ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10, ["default"] = 3 },
                },
            });
        Directory.CreateDirectory(Path.Combine(run.DataRoot, "plugin-data", Formed));
        var instance = Guid.NewGuid();
        var layout = run.WriteDefaultLayout((instance, Formed, "probe",
            new JsonObject { ["mode"] = "slow", ["loud"] = true, ["count"] = 4 }));
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => Plugin(s, Formed)?["pid"] is not null, Startup);

        var status = await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" }, new JsonObject { ["select"] = Formed });
        var form = Window(status)!["detail"]!["instances"]!.AsArray().Single()!;
        Assert.Equal(instance.ToString("D"), (string?)form["instanceId"]);
        var fields = form["fields"]!.AsArray().ToDictionary(field => (string)field!["name"]!, field => field!.AsObject());
        Assert.Equal("choice", (string?)fields["mode"]["kind"]);
        Assert.Equal("toggle", (string?)fields["loud"]["kind"]);
        Assert.Equal("number", (string?)fields["count"]["kind"]);
        Assert.Equal("slow", (string?)fields["mode"]["value"]);
        Assert.True((bool)fields["loud"]["value"]!);
        Assert.Equal(4, (int)fields["count"]["value"]!);

        var scope = $"Instance.{instance:D}";
        await SettingsUiAsync(run,
            new JsonObject { ["set"] = $"{scope}.mode", ["value"] = "fast" },
            new JsonObject { ["set"] = $"{scope}.loud", ["value"] = false },
            new JsonObject { ["set"] = $"{scope}.count", ["value"] = 7 },
            new JsonObject { ["invoke"] = $"{scope}.Save" });
        var saved = await WaitForFileAsync(layout, text => (int?)InstanceSettings(text)?["count"] == 7);

        var settings = InstanceSettings(saved)!;
        Assert.Equal("fast", (string?)settings["mode"]);
        Assert.False((bool)settings["loud"]!);
        Assert.Equal(7, (int)settings["count"]!);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S10_5_choosing_a_preset_applies_it_and_persists_activePreset()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        run.WriteDataFile(Path.Combine("presets", "user.other.json"), new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "user.other",
            ["displayName"] = "Other",
            ["layout"] = "subzerodev.default",
            ["theme"] = "subzerodev.default",
        }.ToJsonString());
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => HostRun.Texts(s, Clock).Count == 1, Startup);

        var status = await SettingsUiAsync(run, new JsonObject { ["page"] = "appearance" });
        var presets = Window(status)!["presets"]!;
        Assert.Contains(presets["items"]!.AsArray(), item => (string?)item == "user.other");
        Assert.Equal("subzerodev.default", (string?)presets["selected"]);

        await SettingsUiAsync(run, new JsonObject { ["set"] = "Appearance.Preset", ["value"] = "user.other" });
        status = await run.WaitForAsync(s => (string?)s["settings"]!["activePreset"] == "user.other", Reaction);
        Assert.Equal("user.other", (string?)Window(status)!["presets"]!["selected"]);
        var settings = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(run.DataRoot, "settings.json"), TestContext.Current.CancellationToken))!;
        Assert.Equal("user.other", (string?)settings["activePreset"]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S10_6_the_detail_shows_the_last_200_log_lines_and_recent_faults_with_times()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Faulty, ["faulty"]);
        run.WriteDefaultLayout((Guid.NewGuid(), Faulty, "probe"));
        var data = Path.Combine(run.DataRoot, "plugin-data", Faulty);
        Directory.CreateDirectory(data);
        var log = Path.Combine(run.DataRoot, "logs", "plugins",
            $"{Faulty}-{DateTimeOffset.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
        Directory.CreateDirectory(Path.GetDirectoryName(log)!);
        await File.WriteAllLinesAsync(log, Enumerable.Range(0, 250).Select(i => $"earlier line {i}"), TestContext.Current.CancellationToken);
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => Plugin(s, Faulty)?["pid"] is not null, Startup);

        File.WriteAllText(Path.Combine(data, "crash"), "1");
        await run.WaitForAsync(s => Plugin(s, Faulty)?["lastFault"] is not null, Reaction);
        await run.WaitForAsync(s => (string?)Plugin(s, Faulty)?["state"] == "Running", Reaction);

        var status = await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" }, new JsonObject { ["select"] = Faulty });
        var detail = Window(status)!["detail"]!;
        var lines = detail["logLines"]!.AsArray().Select(line => (string)line!).ToList();
        Assert.Equal(200, lines.Count);
        Assert.DoesNotContain("earlier line 0", lines);
        var faults = detail["faults"]!.AsArray().Select(fault => (string)fault!).ToList();
        Assert.Contains(faults, fault => FaultLine().IsMatch(fault));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S10_7_every_control_has_an_accessible_name_and_takes_keyboard_focus()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Formed, ["faulty"], manifest =>
            manifest["contributions"]![0]!["settingsSchema"] = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["mode"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("fast", "slow") },
                    ["loud"] = new JsonObject { ["type"] = "boolean" },
                    ["count"] = new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 10 },
                    ["label"] = new JsonObject { ["type"] = "string", ["title"] = "Label" },
                },
            });
        Directory.CreateDirectory(Path.Combine(run.DataRoot, "plugin-data", Formed));
        run.WriteDefaultLayout((Guid.NewGuid(), Formed, "probe"));
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => Plugin(s, Formed)?["pid"] is not null, Startup);

        var problems = new List<string>();
        var status = await SettingsUiAsync(run, new JsonObject { ["page"] = "plugins" }, new JsonObject { ["select"] = Formed });
        problems.AddRange(Unreachable(Window(status)!));
        status = await SettingsUiAsync(run, new JsonObject { ["page"] = "appearance" });
        problems.AddRange(Unreachable(Window(status)!));

        Assert.True(problems.Count == 0, string.Join(Environment.NewLine, problems));
        Assert.Equal(0, await run.StopAsync());
    }

    /// <summary>
    /// The window's enabled, visible interactive controls that have no accessible name or cannot take keyboard
    /// focus, as UI Automation sees them.
    /// </summary>
    private static IEnumerable<string> Unreachable(JsonNode window)
    {
        ControlType[] interactive =
        [
            ControlType.Button, ControlType.CheckBox, ControlType.ComboBox, ControlType.Edit, ControlType.ListItem,
            ControlType.RadioButton, ControlType.Slider, ControlType.Spinner, ControlType.Hyperlink, ControlType.TabItem,
        ];
        var root = AutomationElement.FromHandle(new IntPtr((long)window["hwnd"]!));
        var seen = 0;
        var problems = new List<string>();
        var pending = new Stack<AutomationElement>([root]);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            for (var child = TreeWalker.ControlViewWalker.GetFirstChild(element); child is not null;
                child = TreeWalker.ControlViewWalker.GetNextSibling(child))
            {
                pending.Push(child);
            }

            var info = element.Current;
            if (!interactive.Contains(info.ControlType) || !info.IsEnabled || info.IsOffscreen
                || info.AutomationId is "Close" or "Maximize" or "Minimize")
            {
                // The caption buttons belong to the system title bar, which is reached with Alt+Space, not Tab.
                continue;
            }

            seen++;
            var label = $"{info.ControlType.ProgrammaticName} '{info.AutomationId}' '{info.Name}'";
            if (string.IsNullOrWhiteSpace(info.Name))
            {
                problems.Add($"{label} has no accessible name.");
            }

            if (!info.IsKeyboardFocusable && !FocusableWithin(element))
            {
                problems.Add($"{label} cannot take keyboard focus.");
            }
        }

        if (seen == 0)
        {
            problems.Add($"No interactive controls found on page '{window["page"]}'.");
        }

        return problems;
    }

    /// <summary>A composite control such as a number box's spinner takes focus through a part inside it.</summary>
    private static bool FocusableWithin(AutomationElement element) =>
        element.FindFirst(TreeScope.Descendants, new PropertyCondition(AutomationElement.IsKeyboardFocusableProperty, true)) is not null;

    private static async Task<JsonObject> SettingsUiAsync(HostRun run, params JsonObject[] requests)
    {
        var before = (int?)(await run.WaitForStatusAsync())["settingsUiRuns"] ?? 0;
        await File.WriteAllLinesAsync(Path.Combine(run.HookDirectory, "settings-ui"), requests.Select(request => request.ToJsonString()),
            TestContext.Current.CancellationToken);
        var status = await run.WaitForAsync(s => (int?)s["settingsUiRuns"] > before, Reaction);
        Assert.All(((string)status["lastSettingsUi"]!).Split(" | "), result => Assert.Equal("ok", result));
        return status;
    }

    private static async Task<int> Restart(HostRun run)
    {
        var code = await run.StopAsync();
        File.Delete(Path.Combine(run.HookDirectory, "exit"));
        File.Delete(Path.Combine(run.HookDirectory, "status.json"));
        run.Start(HostRun.HostExe);
        return code;
    }

    private static async Task<string> WaitForFileAsync(string path, Func<string, bool> condition)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            var text = File.Exists(path) ? await File.ReadAllTextAsync(path, TestContext.Current.CancellationToken) : string.Empty;
            if (condition(text))
            {
                return text;
            }

            Assert.True(deadline.Elapsed < Reaction, $"{path} did not change as expected: {text}");
            await Task.Delay(100, TestContext.Current.CancellationToken);
        }
    }

    private static bool Enabled(HostRun run, string plugin) =>
        JsonNode.Parse(File.ReadAllText(Path.Combine(run.DataRoot, "settings.json")))!["plugins"]?[plugin]?["enabled"] is not JsonValue value
        || value.GetValue<bool>();

    private static JsonObject? Window(JsonObject status) => status["settingsWindow"] as JsonObject;

    private static JsonObject Row(JsonObject status, string plugin) =>
        Window(status)?["plugins"]?.AsArray().FirstOrDefault(row => (string?)row!["id"] == plugin)?.AsObject()
        ?? throw new InvalidOperationException($"The settings window lists no {plugin}: {status["settingsWindow"]?.ToJsonString()}");

    private static string? State(JsonObject status, string plugin) =>
        (string?)Window(status)?["plugins"]?.AsArray().FirstOrDefault(row => (string?)row!["id"] == plugin)?["state"];

    private static JsonObject? Plugin(JsonObject status, string plugin) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == plugin)?.AsObject();

    private static string? Reason(JsonObject status, string plugin) =>
        (string?)status["contributions"]!.AsArray().FirstOrDefault(c => (string?)c!["plugin"] == plugin)?["reason"];

    /// <summary>The first instance's settings in a layout file's text; null while the file is missing or being written.</summary>
    private static JsonNode? InstanceSettings(string layout)
    {
        try
        {
            return JsonNode.Parse(layout)?["panels"]?[0]?["zones"]?["end"]?[0]?["settings"];
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}  \w+: ")]
    private static partial Regex FaultLine();
}

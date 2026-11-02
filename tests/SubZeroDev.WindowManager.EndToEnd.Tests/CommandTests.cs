using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S6: clicks, keys and hotkeys reach plugins as commands and events. The test plugin's
/// <c>buttons</c> scenario shows four elements and logs what reaches it; a second copy declares
/// <c>samples.echo</c> too, plus <c>samples.remote</c>, but is never placed, so it never runs.
/// </summary>
public sealed partial class CommandTests
{
    private const string Buttons = "samples.buttons";
    private const string Remote = "test.remote";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S6_1_S6_2_and_S6_4_a_click_sends_one_command_and_a_stopped_provider_shows_a_notice()
    {
        HostRun.RequireDesktop();
        using var run = Start(out var data);
        var status = await run.WaitForAsync(Ready, Startup);
        var pid = (int)Plugin(status, Buttons)["pid"]!;

        var echo = Command(status, "samples.echo");
        Assert.Equal(Buttons, (string?)echo["provider"]);
        Assert.True((bool)echo["available"]!);
        var remote = Command(status, "samples.remote");
        Assert.Equal(Remote, (string?)remote["provider"]);
        Assert.False((bool)remote["available"]!);

        ClickElement(status, "echo");
        await WaitForLinesAsync(data, "invocations.log", 1);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        Assert.Equal([$"command samples.echo pointer {pid}"], Lines(data, "invocations.log"));
        status = await run.WaitForStatusAsync();
        var dispatches = Dispatches(status);
        var allow = dispatches.IndexOf($"allow {pid}");
        var send = dispatches.IndexOf($"send samples.echo {pid} Pointer");
        Assert.True(allow >= 0 && send > allow, string.Join(" | ", dispatches));

        var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(run.DataRoot, "settings.json")))!;
        Assert.Equal(Buttons, (string?)settings["commandProviders"]?["samples.echo"]);

        ClickElement(status, "remote");
        status = await run.WaitForAsync(s => Notices(s).Any(n => (string?)n["element"] == "remote"), Reaction);
        var notice = Notices(status).Single(n => (string?)n["element"] == "remote");
        Assert.Equal("PluginUnavailable", (string?)notice["code"]);
        Assert.Contains("1001", (string?)notice["message"], StringComparison.Ordinal);
        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        status = await run.WaitForStatusAsync();
        Assert.DoesNotContain(Dispatches(status), d => d.StartsWith("send samples.remote", StringComparison.Ordinal));
        Assert.Single(Lines(data, "invocations.log"));

        ClickElement(status, "ev");
        await WaitForLinesAsync(data, "events.log", 1);
        Assert.Equal("event ev Invoked \"ping\" Pointer", Lines(data, "events.log")[0]);

        ClickElement(status, "tog");
        await WaitForLinesAsync(data, "events.log", 2);
        Assert.Equal("event tog Toggled true Pointer", Lines(data, "events.log")[1]);
        await run.WaitForAsync(s => (bool?)Element(s, "tog")["checked"] == true, Reaction);

        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S6_5_S6_6_and_S6_8_the_hotkey_and_the_keyboard_reach_the_command()
    {
        HostRun.RequireDesktop();
        using var run = Start(out var data);
        var status = await run.WaitForAsync(Ready, Startup);

        var binding = status["hotkeys"]!.AsArray().Single(h => (string?)h!["command"] == "samples.echo")!;
        Assert.Equal("Ctrl+Alt+Shift+F11", (string?)binding["chord"]);
        Assert.Equal("bound", (string?)binding["state"]);

        Interactive.Press(Interactive.Control, Interactive.Alt, Interactive.Shift, Interactive.F11);
        await WaitForLinesAsync(data, "invocations.log", 1);
        Assert.StartsWith("command samples.echo hotkey ", Lines(data, "invocations.log")[0], StringComparison.Ordinal);

        var panel = (string)status["panels"]!.AsArray()[0]!["id"]!;
        await File.WriteAllTextAsync(Path.Combine(run.HookDirectory, "focus-panel"), panel, TestContext.Current.CancellationToken);
        await run.WaitForAsync(s => Focused(s) == "echo", Reaction);
        Interactive.Press(Interactive.Tab);
        await run.WaitForAsync(s => Focused(s) == "remote", Reaction);
        Interactive.Press(Interactive.Tab);
        await run.WaitForAsync(s => Focused(s) == "ev", Reaction);
        Interactive.Press(Interactive.Shift, Interactive.Tab);
        await run.WaitForAsync(s => Focused(s) == "remote", Reaction);
        Interactive.Press(Interactive.Shift, Interactive.Tab);
        await run.WaitForAsync(s => Focused(s) == "echo", Reaction);

        Interactive.Press(Interactive.Enter);
        await WaitForLinesAsync(data, "invocations.log", 2);
        Assert.StartsWith("command samples.echo keyboard ", Lines(data, "invocations.log")[1], StringComparison.Ordinal);

        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S6_7_activate_restores_and_brings_forward_and_a_dead_window_is_gone()
    {
        HostRun.RequireDesktop();
        using var run = Start(out _);
        var status = await run.WaitForAsync(Ready, Startup);
        var handle = (long)status["panels"]!.AsArray()[0]!["handle"]!;

        status = await InvokeAsync(run, "host.window.minimize", new JsonObject { ["hwnd"] = handle });
        Assert.True((bool)status["lastInvoke"]!["ok"]!, status["lastInvoke"]!.ToJsonString());
        Assert.True(Interactive.IsMinimised((nint)handle));

        status = await InvokeAsync(run, "host.window.activate", new JsonObject { ["hwnd"] = handle });
        Assert.True((bool)status["lastInvoke"]!["ok"]!, status["lastInvoke"]!.ToJsonString());
        Assert.False(Interactive.IsMinimised((nint)handle));
        Assert.Equal((nint)handle, Interactive.Foreground());

        status = await InvokeAsync(run, "host.window.activate", new JsonObject { ["hwnd"] = 0x0BADF00D });
        Assert.Equal("WindowGone", (string?)status["lastInvoke"]!["code"]);

        Assert.Equal(0, await run.StopAsync());
    }

    private static HostRun Start(out string data)
    {
        var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Buttons, ["buttons"]);
        run.CopyPlugin(HostRun.TestPluginDirectory, Remote, ["buttons"], manifest =>
            manifest["commands"] = new JsonArray(
                new JsonObject { ["id"] = "samples.echo", ["title"] = "Echo" },
                new JsonObject { ["id"] = "samples.remote", ["title"] = "Remote" }));
        run.WriteDefaultLayout((Guid.NewGuid(), Buttons, "probe"));
        data = Path.Combine(run.DataRoot, "plugin-data", Buttons);
        Directory.CreateDirectory(data);
        run.Start(HostRun.HostExe);
        return run;
    }

    private static async Task<JsonObject> InvokeAsync(HostRun run, string command, JsonObject args)
    {
        var before = (await run.WaitForStatusAsync())["lastInvoke"]?.ToJsonString();
        await File.WriteAllLinesAsync(Path.Combine(run.HookDirectory, "invoke"), [command, "keyboard", args.ToJsonString()],
            TestContext.Current.CancellationToken);
        return await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == command
            && last.ToJsonString() != before, Reaction);
    }

    private static bool Ready(JsonObject status) =>
        (string?)Plugin(status, Buttons)["state"] == "Running"
        && Contribution(status)?["interactive"] is JsonArray elements && elements.Count == 4
        && elements.All(e => e!["bounds"] is JsonObject);

    private static void ClickElement(JsonObject status, string id)
    {
        var bounds = Element(status, id)["bounds"]!;
        Interactive.Click(((int)bounds["left"]! + (int)bounds["right"]!) / 2, ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2);
    }

    private static JsonObject? Contribution(JsonObject status) =>
        status["contributions"]!.AsArray().FirstOrDefault(c => (string?)c!["plugin"] == Buttons)?.AsObject();

    private static JsonObject Element(JsonObject status, string id) =>
        Contribution(status)!["interactive"]!.AsArray().Single(e => (string?)e!["id"] == id)!.AsObject();

    private static List<JsonObject> Notices(JsonObject status) =>
        Contribution(status)?["notices"]?.AsArray().Select(n => n!.AsObject()).ToList() ?? [];

    private static JsonObject Plugin(JsonObject status, string plugin) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == plugin)?.AsObject() ?? [];

    private static JsonObject Command(JsonObject status, string id) =>
        status["commands"]!.AsArray().Single(c => (string?)c!["id"] == id)!.AsObject();

    private static List<string> Dispatches(JsonObject status) => [.. status["dispatches"]!.AsArray().Select(d => (string)d!)];

    private static string? Focused(JsonObject status) =>
        status["panels"]!.AsArray().Select(p => (string?)p!["focused"]).FirstOrDefault(name => name is not null);

    private static string[] Lines(string data, string file)
    {
        var path = Path.Combine(data, file);
        if (!File.Exists(path))
        {
            return [];
        }

        // The worker may be appending while this reads, so share write access rather than demand it be closed.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(line => line.TrimEnd('\r')).ToArray();
    }

    private static async Task WaitForLinesAsync(string data, string file, int count)
    {
        var deadline = DateTime.UtcNow + Reaction;
        while (Lines(data, file).Length < count)
        {
            Assert.True(DateTime.UtcNow < deadline, $"{file} has {Lines(data, file).Length} of {count} lines.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }
}

internal static partial class Interactive
{
    public const ushort Tab = 0x09;
    public const ushort Enter = 0x0D;
    public const ushort Shift = 0x10;
    public const ushort Control = 0x11;
    public const ushort Alt = 0x12;
    public const ushort F11 = 0x7A;

    /// <summary>Presses the keys in order, then releases them in reverse, as one injected sequence.</summary>
    public static unsafe void Press(params ushort[] keys)
    {
        const uint Keyboard = 1;
        const uint KeyUp = 0x0002;
        var inputs = stackalloc KeyInput[keys.Length * 2];
        for (var i = 0; i < keys.Length; i++)
        {
            inputs[i] = new KeyInput { Type = Keyboard, VirtualKey = keys[i] };
            inputs[(keys.Length * 2) - 1 - i] = new KeyInput { Type = Keyboard, VirtualKey = keys[i], Flags = KeyUp };
        }

        var sent = SendInput((uint)(keys.Length * 2), inputs, sizeof(KeyInput));
        if (sent != keys.Length * 2)
        {
            throw new InvalidOperationException($"SendInput sent {sent} of {keys.Length * 2} inputs.");
        }
    }

    public static bool IsMinimised(nint hwnd) => IsIconic(hwnd);

    public static nint Foreground() => GetForegroundWindow();

    /// <summary>INPUT with its KEYBDINPUT member, padded to the size of the union's largest member.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 40)]
    private struct KeyInput
    {
        [FieldOffset(0)] public uint Type;
        [FieldOffset(8)] public ushort VirtualKey;
        [FieldOffset(10)] public ushort ScanCode;
        [FieldOffset(12)] public uint Flags;
        [FieldOffset(16)] public uint Time;
        [FieldOffset(24)] public nint ExtraInfo;
    }

    [LibraryImport("user32.dll")]
    private static unsafe partial uint SendInput(uint count, KeyInput* inputs, int size);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();
}

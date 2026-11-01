using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S8: the built-in launcher. Each test puts its own shortcuts in a fresh folder under the per-user
/// Start menu Programs folder, named with a token no real application has, so searches see only them.
/// </summary>
public sealed partial class LauncherTests
{
    private const string Launcher = "subzerodev.launcher";
    private const string Target = @"C:\Windows\System32\charmap.exe";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S8_1_the_hotkey_opens_the_popup_on_the_pointer_monitor_with_the_query_focused_within_300_ms()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        await run.WaitForAsync(Ready, Startup);
        var work = Interactive.PrimaryWorkArea();
        Interactive.MoveTo((work.Left + work.Right) / 2, (work.Top + work.Bottom) / 2);

        // The first open builds the window; time the second, as the user sees it from then on.
        await OpenAsync(run);
        Interactive.Press(Interactive.Escape);
        await run.WaitForAsync(s => !Open(s), Reaction);

        var pressed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interactive.Press(OpenChord.Keys);
        var status = await run.WaitForAsync(s => Open(s) && (string?)Popup(s)["focusedInput"] == "query"
            && (long)Popup(s)["readyAtUtcMs"]! >= pressed, Reaction);
        var popup = Popup(status);
        Assert.InRange((long)popup["readyAtUtcMs"]! - pressed, 0, 300);
        var bounds = popup["bounds"]!;
        Assert.InRange((int)bounds["left"]!, work.Left, work.Right);
        Assert.InRange((int)bounds["right"]!, work.Left, work.Right);
        Assert.InRange((int)bounds["top"]!, work.Top, work.Bottom);
        Assert.Equal((long)popup["hwnd"]!, (long)Interactive.Foreground());
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S8_2_and_S8_3_shortcuts_are_indexed_and_typing_filters_prefix_first_after_a_pause()
    {
        HostRun.RequireDesktop();
        using var shortcuts = Shortcuts.Create();
        var prefix = shortcuts.Add($"{shortcuts.Token} Pad", Target);
        var inner = shortcuts.Add($"My {shortcuts.Token.ToUpperInvariant()} Tool", Target);
        shortcuts.Add($"{shortcuts.Token} Broken", Path.Combine(shortcuts.Folder, "missing.exe"));
        using var run = Start();
        await run.WaitForAsync(Ready, Startup);
        await OpenAsync(run);

        Interactive.Type(shortcuts.Token);
        var typed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var status = await run.WaitForAsync(s => Results(s).Count == 2, Reaction);
        Assert.Equal([prefix, inner], Results(status));
        Assert.Equal([$"{shortcuts.Token} Pad", $"My {shortcuts.Token.ToUpperInvariant()} Tool"],
            Items(status).Select(item => (string?)item["label"]));
        Assert.Equal(prefix, (string?)Popup(status)["selectedKey"]);
        var query = Popup(status)["inputs"]!.AsArray().Single(i => (string?)i!["id"] == "query")!;
        Assert.Equal(shortcuts.Token, (string?)query["text"]);
        Assert.True((long)query["editedAtUtcMs"]! - typed >= 150, "The query was sent before 150 ms passed.");
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S8_4_arrows_move_the_selection_and_Enter_launches_into_the_foreground_and_closes()
    {
        HostRun.RequireDesktop();
        using var shortcuts = Shortcuts.Create();
        var first = shortcuts.Add($"{shortcuts.Token} A", @"C:\Windows\System32\notepad.exe");
        var second = shortcuts.Add($"{shortcuts.Token} B", Target);
        using var run = Start();
        await run.WaitForAsync(Ready, Startup);
        await OpenAsync(run);
        Interactive.Type(shortcuts.Token);
        await run.WaitForAsync(s => Results(s).Count == 2 && (string?)Popup(s)["selectedKey"] == first, Reaction);

        Interactive.Press(Interactive.Down);
        await run.WaitForAsync(s => (string?)Popup(s)["selectedKey"] == second
            && Items(s).Single(i => (string?)i["itemKey"] == second)["selected"]?.GetValue<bool>() == true, Reaction);
        Interactive.Press(Interactive.Up);
        await run.WaitForAsync(s => (string?)Popup(s)["selectedKey"] == first, Reaction);
        Interactive.Press(Interactive.Down);
        await run.WaitForAsync(s => (string?)Popup(s)["selectedKey"] == second, Reaction);

        var before = Pids("charmap");
        try
        {
            Interactive.Press(Interactive.Enter);
            await run.WaitForAsync(s => !Open(s) && Pids("charmap").Except(before).Contains(Interactive.ForegroundProcessId()),
                TimeSpan.FromSeconds(10));
            // The worker closes the popup, unless the new window took the foreground first.
            Assert.Contains((string?)Popup(await run.WaitForStatusAsync())["lastCloseReason"], new[] { "Requested", "LightDismiss" });
        }
        finally
        {
            Kill(Pids("charmap").Except(before));
        }

        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S8_5_Escape_and_a_click_outside_close_the_popup_and_tell_the_worker_why()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        await run.WaitForAsync(Ready, Startup);
        var work = Interactive.PrimaryWorkArea();
        Interactive.MoveTo((work.Left + work.Right) / 2, (work.Top + work.Bottom) / 2);

        await OpenAsync(run);
        Interactive.Press(Interactive.Escape);
        await run.WaitForAsync(s => !Open(s) && (string?)Popup(s)["lastCloseReason"] == "Escape", Reaction);

        var status = await OpenAsync(run);
        var bounds = Popup(status)["bounds"]!;
        var below = (int)bounds["bottom"]! + 20;
        Assert.True(below < work.Bottom, "The popup leaves no room below it to click.");
        Interactive.Click((int)bounds["left"]! + 10, below);
        await run.WaitForAsync(s => !Open(s) && (string?)Popup(s)["lastCloseReason"] == "LightDismiss", Reaction);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S8_6_clicking_the_panel_item_opens_the_popup_anchored_to_it()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        var status = await run.WaitForAsync(Ready, Startup);
        var item = PanelItem(status)["bounds"]!;
        Interactive.Click(((int)item["left"]! + (int)item["right"]!) / 2, ((int)item["top"]! + (int)item["bottom"]!) / 2);

        status = await run.WaitForAsync(s => Open(s) && (string?)Popup(s)["focusedInput"] == "query", Reaction);
        var popup = Popup(status)["bounds"]!;
        Assert.InRange((int)popup["bottom"]!, (int)item["top"]! - 16, (int)item["top"]!);
        Assert.True((int)popup["left"]! <= (int)item["right"]! && (int)popup["right"]! >= (int)item["left"]!,
            "The popup does not overlap the item horizontally.");
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S8_7_a_missing_executable_fails_without_retry_and_the_popup_shows_a_notice()
    {
        HostRun.RequireDesktop();
        using var shortcuts = Shortcuts.Create();
        var exe = Path.Combine(shortcuts.Folder, $"{shortcuts.Token}.exe");
        File.Copy(Target, exe);
        var key = shortcuts.Add($"{shortcuts.Token} Gone", exe);
        using var run = Start();
        await run.WaitForAsync(Ready, Startup);

        await File.WriteAllLinesAsync(Path.Combine(run.HookDirectory, "invoke"),
            ["applications.launch", "keyboard", new JsonObject { ["exe"] = exe + ".missing" }.ToJsonString()],
            TestContext.Current.CancellationToken);
        var status = await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == "applications.launch", Reaction);
        var invoked = status["lastInvoke"]!;
        Assert.False((bool)invoked["ok"]!);
        Assert.Equal("CommandFailed", (string?)invoked["code"]);
        Assert.False((bool)invoked["retryable"]!);

        await OpenAsync(run);
        Interactive.Type(shortcuts.Token);
        await run.WaitForAsync(s => Results(s).SequenceEqual([key]), Reaction);

        // The index rebuilds in the background on every open; let it settle before the target goes.
        await Task.Delay(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        File.Delete(exe);
        Assert.Equal([key], Results(await run.WaitForStatusAsync()));
        Interactive.Press(Interactive.Enter);
        status = await run.WaitForAsync(s => Popup(s)["notices"]!.AsArray().Any(n => (string?)n!["code"] == "CommandFailed"), Reaction);
        Assert.True(Open(status));
        Assert.Equal(0, await run.StopAsync());
    }

    /// <summary>
    /// The manifest's Ctrl+Alt+Space when no other program holds it; otherwise the first free fallback,
    /// bound through the host's <c>hotkeys</c> setting, so a machine with a conflicting tool still runs these tests.
    /// </summary>
    private static readonly (string? Override, ushort[] Keys) OpenChord = PickOpenChord();

    private static (string? Override, ushort[] Keys) PickOpenChord()
    {
        (string Chord, uint Modifiers, uint Key, ushort[] Keys)[] candidates =
        [
            ("Ctrl+Alt+Space", 0x3, Interactive.Space, [Interactive.Control, Interactive.Alt, Interactive.Space]),
            ("Ctrl+Shift+Space", 0x6, Interactive.Space, [Interactive.Control, Interactive.Shift, Interactive.Space]),
            ("Ctrl+Alt+Shift+F10", 0x7, 0x79, [Interactive.Control, Interactive.Alt, Interactive.Shift, 0x79]),
        ];
        for (var i = 0; i < candidates.Length; i++)
        {
            var candidate = candidates[i];
            if (RegisterHotKey(0, 0x5A00 + i, candidate.Modifiers, candidate.Key))
            {
                UnregisterHotKey(0, 0x5A00 + i);
                return (i == 0 ? null : candidate.Chord, candidate.Keys);
            }
        }

        return (null, candidates[0].Keys);
    }

    private static HostRun Start()
    {
        var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Launcher, "launcher"));
        if (OpenChord.Override is { } chord)
        {
            run.WriteDataFile("settings.json", new JsonObject
            {
                ["schemaVersion"] = 1,
                ["activePreset"] = "subzerodev.default",
                ["hotkeys"] = new JsonObject { ["launcher.open"] = chord },
            }.ToJsonString());
        }

        run.Start(HostRun.HostExe);
        return run;
    }

    private static async Task<JsonObject> OpenAsync(HostRun run)
    {
        Interactive.Press(OpenChord.Keys);
        return await run.WaitForAsync(s => Open(s) && (string?)Popup(s)["focusedInput"] == "query", Reaction);
    }

    private static bool Ready(JsonObject status) =>
        status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == Launcher && (string?)p["state"] == "Running")
        && status["hotkeys"]!.AsArray().Any(h => (string?)h!["command"] == "launcher.open" && (string?)h["state"] == "bound")
        && PanelItem(status)["bounds"] is JsonObject;

    private static JsonObject PanelItem(JsonObject status) =>
        status["contributions"]!.AsArray()
            .FirstOrDefault(c => (string?)c!["plugin"] == Launcher && c["reason"] is null)?["interactive"]?.AsArray()
            .FirstOrDefault(e => (string?)e!["id"] == "open")?.AsObject() ?? [];

    private static JsonObject Popup(JsonObject status) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == Launcher && (string?)p["popup"] == "search")?.AsObject() ?? [];

    private static bool Open(JsonObject status) => Popup(status)["open"]?.GetValue<bool>() == true;

    private static List<JsonObject> Items(JsonObject status) =>
        Popup(status)["items"]?.AsArray().Select(i => i!.AsObject()).Where(i => i["itemKey"] is not null).ToList() ?? [];

    private static List<string> Results(JsonObject status) => [.. Items(status).Select(i => (string)i["itemKey"]!)];

    private static HashSet<int> Pids(string name) => [.. Process.GetProcessesByName(name).Select(p =>
    {
        using (p)
        {
            return p.Id;
        }
    })];

    private static void Kill(IEnumerable<int> pids)
    {
        foreach (var pid in pids)
        {
            try
            {
                using var process = Process.GetProcessById(pid);
                process.Kill();
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RegisterHotKey(nint hwnd, int id, uint modifiers, uint key);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool UnregisterHotKey(nint hwnd, int id);

    /// <summary>A folder of test shortcuts under the per-user Start menu Programs folder, deleted on dispose.</summary>
    private sealed class Shortcuts : IDisposable
    {
        private Shortcuts(string token)
        {
            Token = token;
            Folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "SubZeroDev test " + token);
            Directory.CreateDirectory(Folder);
        }

        /// <summary>A word no real application's name contains.</summary>
        public string Token { get; }

        public string Folder { get; }

        public static Shortcuts Create() => new("zq" + Guid.NewGuid().ToString("N")[..6]);

        /// <summary>Writes <c>name.lnk</c> pointing at <paramref name="target"/> and returns its path, the launcher's key for it.</summary>
        public string Add(string name, string target)
        {
            var path = Path.Combine(Folder, name + ".lnk");
            var script = $"$s = (New-Object -ComObject WScript.Shell).CreateShortcut('{path.Replace("'", "''", StringComparison.Ordinal)}'); "
                + $"$s.TargetPath = '{target.Replace("'", "''", StringComparison.Ordinal)}'; $s.Save()";
            var start = new ProcessStartInfo("powershell.exe") { UseShellExecute = false, CreateNoWindow = true };
            start.ArgumentList.Add("-NoProfile");
            start.ArgumentList.Add("-Command");
            start.ArgumentList.Add(script);
            using var process = Process.Start(start)!;
            process.WaitForExit();
            Assert.True(File.Exists(path), $"Could not create the shortcut {path}.");
            return path;
        }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Folder, recursive: true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}

internal static partial class Interactive
{
    public const ushort Escape = 0x1B;
    public const ushort Space = 0x20;
    public const ushort Up = 0x26;
    public const ushort Down = 0x28;

    /// <summary>Types the text as Unicode key strokes, one injected sequence.</summary>
    public static unsafe void Type(string text)
    {
        const uint Keyboard = 1;
        const uint KeyUp = 0x0002;
        const uint Unicode = 0x0004;
        var inputs = stackalloc KeyInput[text.Length * 2];
        for (var i = 0; i < text.Length; i++)
        {
            inputs[i * 2] = new KeyInput { Type = Keyboard, ScanCode = text[i], Flags = Unicode };
            inputs[(i * 2) + 1] = new KeyInput { Type = Keyboard, ScanCode = text[i], Flags = Unicode | KeyUp };
        }

        var sent = SendInput((uint)(text.Length * 2), inputs, sizeof(KeyInput));
        if (sent != text.Length * 2)
        {
            throw new InvalidOperationException($"SendInput sent {sent} of {text.Length * 2} inputs.");
        }
    }

    /// <summary>Moves the cursor to the physical-pixel point.</summary>
    public static void MoveTo(int x, int y)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            if (!SetCursorPos(x, y))
            {
                throw new InvalidOperationException("SetCursorPos failed.");
            }
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    public static int ForegroundProcessId()
    {
        GetWindowThreadProcessId(GetForegroundWindow(), out var pid);
        return (int)pid;
    }

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}

using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S28: the Win+X menu. Under the test hook the plugin reads the <c>winx</c> folder of the hook directory instead
/// of Windows' own WinX folders, and a selected entry is recorded in <c>desktop-actions.log</c> instead of started
/// (invariant I38: a test never starts a program or reads the user's folders). The Windows-key hook acts only on keys a
/// test injects with its marker, so Win+X never reaches Windows.
/// </summary>
public sealed class PowerMenuTests
{
    private const string PowerMenu = "subzerodev.power-menu";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task S28_2_win_x_opens_the_menu_with_the_winx_entries_in_group_order_and_a_second_press_closes_it()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        var winx = Path.Combine(run.HookDirectory, "winx");
        Shortcut(Path.Combine(winx, "Group1", "Zeta tool.lnk"), @"C:\Windows\System32\notepad.exe", string.Empty);
        Shortcut(Path.Combine(winx, "Group3", "02 - Beta.lnk"), @"C:\Windows\System32\notepad.exe", "beta.txt");
        Shortcut(Path.Combine(winx, "Group3", "01 - Alpha.lnk"), @"C:\Windows\System32\notepad.exe", "alpha.txt");
        await run.WaitForAsync(KeysReady, Startup);

        Interactive.PressMarked(Interactive.LeftWin, 'X');
        var status = await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() == true && Items(s).Count == 3, Reaction);
        Assert.Contains(Dispatches(status), d => d.StartsWith("send powermenu.open ", StringComparison.Ordinal) && d.EndsWith(" Hotkey", StringComparison.Ordinal));

        // Windows lists the highest group first; the order prefix in a file name is not shown.
        Assert.Equal(["Alpha", "Beta", "Zeta tool"], Items(status).Select(i => (string?)i["label"]));

        // The chord toggles: a second press closes it.
        Interactive.PressMarked(Interactive.LeftWin, 'X');
        await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() != true, Reaction);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S28_3_choosing_an_entry_starts_it_through_the_shell_and_closes_the_menu()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        var winx = Path.Combine(run.HookDirectory, "winx");
        Shortcut(Path.Combine(winx, "Group2", "Editor.lnk"), @"C:\Windows\System32\notepad.exe", "notes.txt");
        var ready = await run.WaitForAsync(s => KeysReady(s) && Button(s)?["bounds"] is JsonObject, Startup);

        // Open the menu from its button with the keyboard: focus the panel, then press Enter.
        File.WriteAllText(Path.Combine(run.HookDirectory, "focus-panel"), (string)ready["panels"]![0]!["id"]!);
        await run.WaitForAsync(s => (string?)s["panels"]![0]!["focused"] == "menu", Reaction);
        Interactive.Press(Interactive.Enter);
        var status = await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() == true && Items(s).Count == 1
            && (string?)Menu(s)["focused"] == "entry0", Reaction);
        Assert.Equal((int)status["hostPid"]!, Interactive.ForegroundProcessId());

        // Enter on the focused entry chooses it, as a click does; the menu then closes.
        Interactive.Press(Interactive.Enter);
        await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() != true, Reaction);

        Assert.Contains("winx-launch Editor.lnk notes.txt", DesktopActions(run));
        Assert.Equal(0, await run.StopAsync());
    }

    // Requires an idle, isolated foreground desktop. This is an unresolved native acceptance gate,
    // kept explicit so a normal CI run cannot be mistaken for qualifying pointer selection.
    [Fact(Explicit = true)]
    public async Task S28_3_a_pointer_click_chooses_an_entry_on_every_open()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        Shortcut(Path.Combine(run.HookDirectory, "winx", "Group2", "Editor.lnk"), @"C:\Windows\System32\notepad.exe", "notes.txt");
        await run.WaitForAsync(KeysReady, Startup);

        for (var attempt = 1; attempt <= 10; attempt++)
        {
            Interactive.PressMarked(Interactive.LeftWin, 'X');
            var status = await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() == true
                && Items(s).Count == 1 && Items(s)[0]["bounds"] is JsonObject
                && (string?)Menu(s)["focused"] == "entry0", Reaction);
            var bounds = Items(status)[0]["bounds"]!;
            var x = ((int)bounds["left"]! + (int)bounds["right"]!) / 2;
            var y = ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2;
            Assert.True(Interactive.IsWindowAt(x, y, (long)Menu(status)["hwnd"]!), "Only click the test popup.");
            Interactive.MoveTo(x, y - 150);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            Interactive.MoveTo(x, y);
            await Task.Delay(150, TestContext.Current.CancellationToken);
            Interactive.Click(x, y);
            try
            {
                await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() != true
                    && DesktopActions(run).Count(line => line.Contains("winx-launch Editor.lnk notes.txt", StringComparison.Ordinal)) == attempt, Reaction);
            }
            catch (TimeoutException ex)
            {
                throw new TimeoutException($"Pointer selection failed on opening {attempt} of 10.", ex);
            }
        }

        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S28_2_without_winx_folders_the_menu_falls_back_to_the_built_in_tools()
    {
        HostRun.RequireDesktop();
        using var run = Start();
        await run.WaitForAsync(KeysReady, Startup);

        Interactive.PressMarked(Interactive.LeftWin, 'X');
        var status = await run.WaitForAsync(s => Menu(s)["open"]?.GetValue<bool>() == true && Items(s).Count > 0, Reaction);
        var labels = Items(status).Select(i => (string?)i["label"]).ToList();
        Assert.Contains("Task Manager", labels);
        Assert.Contains("Device Manager", labels);
        Assert.Contains("Settings", labels);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public void S28_2_the_shell_layout_places_the_power_menu()
    {
        var layout = Path.Combine(Path.GetDirectoryName(HostRun.HostExe)!, "content", "layouts", "subzerodev.shell.json");
        var placed = JsonNode.Parse(File.ReadAllText(layout))!["panels"]!.AsArray()
            .SelectMany(panel => panel!["zones"]!.AsObject().SelectMany(zone => zone.Value!.AsArray()))
            .Any(item => (string?)item!["plugin"] == PowerMenu && (string?)item["contribution"] == "menu");
        Assert.True(placed, "The default layout does not place subzerodev.power-menu.");
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private static HostRun Start()
    {
        var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), PowerMenu, "menu"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        return run;
    }

    /// <summary>Writes a shortcut file; it is never started, only read by the plugin.</summary>
    private static void Shortcut(string path, string target, string arguments)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var type = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException("WScript.Shell is not available.");
        var shell = Activator.CreateInstance(type)!;
        try
        {
            dynamic link = ((dynamic)shell).CreateShortcut(path);
            link.TargetPath = target;
            link.Arguments = arguments;
            link.Save();
        }
        finally
        {
            Marshal.ReleaseComObject(shell);
        }
    }

    private static JsonObject PluginOf(JsonObject status, string id) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == id)?.AsObject() ?? [];

    /// <summary>Ready, with the hook installed and the plugin running: only then may a test inject keys.</summary>
    private static bool KeysReady(JsonObject status) =>
        (bool?)status["ready"] == true
        && (bool?)status["keyHook"]?["installed"] == true
        && PluginOf(status, PowerMenu)["state"]?.GetValue<string>() == "Running";

    private static JsonObject? Button(JsonObject status) =>
        status["contributions"]!.AsArray().FirstOrDefault(c => (string?)c!["plugin"] == PowerMenu)?["interactive"]?.AsArray()
            .FirstOrDefault(e => (string?)e!["id"] == "menu")?.AsObject();

    private static List<string> Dispatches(JsonObject status) => [.. status["dispatches"]!.AsArray().Select(d => (string)d!)];

    private static JsonObject Menu(JsonObject status) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == PowerMenu && (string?)p["popup"] == "menu")?.AsObject() ?? [];

    private static List<JsonObject> Items(JsonObject status) =>
        Menu(status)["items"]?.AsArray().Select(i => i!.AsObject()).ToList() ?? [];

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

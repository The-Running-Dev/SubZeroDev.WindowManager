using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S20: the desktop, its wallpaper and the desktop icons. Under the test hook the system wallpaper comes from
/// the <c>system-wallpaper</c> hook file, show-desktop touches only the processes in <c>desktop-show-pids</c>, the
/// icons list the folders in <c>SUBZERODEV_WM_DESKTOP_FOLDERS</c>, and opening one is recorded in
/// <c>desktop-actions.log</c> instead of done.
/// </summary>
public sealed class DesktopTests
{
    private const string Icons = "subzerodev.desktop-icons";
    private const string PanelId = "8c2f6a10-3d4e-4b5f-a617-28394a5b6c7d";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S20_1_the_wallpaper_covers_every_monitor_below_the_panels_and_follows_the_preset()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), "subzerodev.clock", "clock"));
        var image = Path.Combine(HostRun.ContentDirectory, "assetpacks", "subzerodev.default", "icons", "launcher.png");
        var packRoot = Path.Combine(run.DataRoot, "assetpacks", "test.walls");
        Directory.CreateDirectory(Path.Combine(packRoot, "walls"));
        File.Copy(image, Path.Combine(packRoot, "walls", "dunes.png"));
        File.WriteAllText(Path.Combine(packRoot, "assetpack.json"), new JsonObject
        {
            ["id"] = "test.walls",
            ["version"] = "0.1.0",
            ["icons"] = new JsonObject(),
            ["appIcons"] = new JsonArray(),
            ["wallpapers"] = new JsonArray("walls/dunes.png"),
        }.ToJsonString());
        WritePreset(run, "test.dunes", new JsonObject { ["image"] = "dunes", ["fit"] = "fit" });
        WritePreset(run, "test.plain", null);
        WriteSettings(run, "test.dunes");
        var system = Path.Combine(run.Root, "system.png");
        File.Copy(image, system);
        Hook(run, "system-wallpaper", system + "\nstretch\n");
        Hook(run, "monitors", new JsonObject
        {
            ["monitors"] = new JsonArray(new JsonObject
            {
                ["id"] = "SIM-1", ["left"] = 4000, ["top"] = 0, ["right"] = 5280, ["bottom"] = 720, ["dpi"] = 96,
            }),
        }.ToJsonString());
        run.Start(HostRun.HostExe, "--mode", "replacement");

        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true
            && s["panels"]!.AsArray().Count > 0
            && Windows(s).Count == s["monitors"]!.AsArray().Count
            && s["monitors"]!.AsArray().Any(m => (string?)m!["id"] == "SIM-1"), Startup);
        Assert.Equal("preset", (string?)status["desktop"]!["source"]);
        foreach (var monitor in status["monitors"]!.AsArray().Select(m => m!.AsObject()))
        {
            var window = Assert.Single(Windows(status), w => (string?)w["monitor"] == (string?)monitor["id"]);
            Assert.Equal(monitor["bounds"]!.ToJsonString(), window["bounds"]!.ToJsonString());
            Assert.Equal(Path.Combine(packRoot, "walls", "dunes.png"), (string?)window["image"]);
            Assert.Equal("Fit", (string?)window["fit"]);
            // The z index counts from the top, so the wallpaper's is larger than every panel's.
            foreach (var panel in status["panels"]!.AsArray())
            {
                Assert.True((int)window["z"]! > (int)panel!["z"]!, $"The wallpaper on {monitor["id"]} is above a panel.");
            }
        }

        var asked = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        WriteSettings(run, "test.plain");
        status = await run.WaitForAsync(s => (string?)s["desktop"]?["source"] == "system", TimeSpan.FromSeconds(10));
        Assert.InRange((long)status["theme"]!["appliedAtUtcMs"]! - asked, 0, 1000);
        Assert.All(Windows(status), w =>
        {
            Assert.Equal(system, (string?)w["image"]);
            Assert.Equal("Stretch", (string?)w["fit"]);
        });
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S20_2_the_icons_merge_both_folders_sorted_and_follow_additions_and_deletions()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var (user, common) = Folders(run);
        Directory.CreateDirectory(Path.Combine(common, "Zeta"));
        File.WriteAllText(Path.Combine(user, "alpha.txt"), "a");
        File.WriteAllText(Path.Combine(common, "Beta.lnk"), "b");
        File.WriteAllText(Path.Combine(user, "desktop.ini"), "hidden");
        File.SetAttributes(Path.Combine(user, "desktop.ini"), FileAttributes.Hidden | FileAttributes.System);
        WriteDesktopLayout(run);
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => Items(s).Count == 3, Startup);
        Assert.Equal(["Zeta", "alpha.txt", "Beta"], Items(status).Select(i => (string?)i["label"]));
        Assert.Equal(Path.Combine(common, "Zeta"), (string?)Items(status)[0]["itemKey"]);
        Assert.Equal(Path.Combine(user, "alpha.txt"), (string?)Items(status)[1]["tooltip"]);

        File.WriteAllText(Path.Combine(user, "gamma.txt"), "g");
        var added = DateTime.UtcNow;
        await run.WaitForAsync(s => Items(s).Any(i => (string?)i["label"] == "gamma.txt"), TimeSpan.FromSeconds(10));
        Assert.True(DateTime.UtcNow - added < TimeSpan.FromSeconds(2), $"The new file showed after {DateTime.UtcNow - added}.");

        File.Delete(Path.Combine(user, "alpha.txt"));
        var deleted = DateTime.UtcNow;
        status = await run.WaitForAsync(s => Items(s).All(i => (string?)i["label"] != "alpha.txt"), TimeSpan.FromSeconds(10));
        Assert.True(DateTime.UtcNow - deleted < TimeSpan.FromSeconds(2), $"The deleted file went after {DateTime.UtcNow - deleted}.");
        Assert.Equal(["Zeta", "Beta", "gamma.txt"], Items(status).Select(i => (string?)i["label"]));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S20_3_a_double_click_opens_an_icon_and_a_single_click_does_not()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var (user, _) = Folders(run);
        var file = Path.Combine(user, "notes.txt");
        File.WriteAllText(file, "n");
        // Clicked in a topmost overlay panel, so no click can land on another application's window that covers
        // the desktop layer; how the icon acts does not depend on the layer it sits in.
        WriteDesktopLayout(run, "overlay");
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => Items(s).Count == 1 && Items(s)[0]["bounds"] is JsonObject
            && (bool?)s["panels"]![0]!["topmost"] == true, Startup);
        var bounds = Items(status)[0]["bounds"]!;
        var x = ((int)bounds["left"]! + (int)bounds["right"]!) / 2;
        var y = ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2;
        Assert.True(Interactive.IsWindowAt(x, y, (long)status["panels"]![0]!["handle"]!), "The icon is not uncovered on screen.");

        Interactive.Click(x, y);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.DoesNotContain(DesktopActions(run), line => line.StartsWith("desktop-open ", StringComparison.Ordinal));

        Interactive.DoubleClick(x, y);
        await WaitUntilAsync(() => DesktopActions(run).Count(line => line == $"desktop-open {file}") == 1, "the double click to open the file");

        File.WriteAllText(Path.Combine(run.HookDirectory, "focus-panel"), PanelId);
        await run.WaitForAsync(s => s["panels"]![0]!["focused"] is not null, TimeSpan.FromSeconds(5));
        Interactive.Press(Interactive.Enter);
        await WaitUntilAsync(() => DesktopActions(run).Count(line => line == $"desktop-open {file}") == 2, "Enter to open the file");

        // The plugin's command opens any path it is given, as the host's command palette would call it.
        Hook(run, "invoke", string.Join("\n", "desktop.open", "keyboard", new JsonObject { ["path"] = user }.ToJsonString()) + "\n");
        await run.WaitForAsync(s => (string?)s["lastInvoke"]?["command"] == "desktop.open" && (bool?)s["lastInvoke"]!["ok"] == true,
            TimeSpan.FromSeconds(10));
        Assert.Contains($"desktop-open {user}", DesktopActions(run));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S20_4_show_desktop_minimises_application_windows_and_restores_them()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), "subzerodev.clock", "clock"));
        using var fixture = RunningWindowsTests.FixtureWindows.Open();
        Hook(run, "desktop-show-pids", Interactive.ProcessOf(fixture["normal"]) + "\n");
        run.Start(HostRun.HostExe, "--mode", "replacement");
        await run.WaitForAsync(s => (bool?)s["ready"] == true, Startup);
        string[] shown = ["normal", "plain"];
        string[] skipped = ["owned", "tool", "invisible", "cloaked"];

        await ShowDesktopAsync(run);
        await WaitUntilAsync(() => shown.All(k => Interactive.IsMinimised(fixture[k])), "the fixture windows to minimise");
        var actions = DesktopActions(run);
        foreach (var kind in shown)
        {
            Assert.Contains($"desktop-show minimise 0x{fixture[kind]:X}", actions);
        }

        foreach (var kind in skipped)
        {
            Assert.DoesNotContain(actions, line => line.EndsWith($" 0x{fixture[kind]:X}", StringComparison.Ordinal));
        }

        await ShowDesktopAsync(run);
        await WaitUntilAsync(() => shown.All(k => !Interactive.IsMinimised(fixture[k])), "the fixture windows to come back");
        actions = DesktopActions(run);
        foreach (var kind in shown)
        {
            Assert.Contains($"desktop-show restore 0x{fixture[kind]:X}", actions);
        }

        Assert.Contains("Show desktop restored", run.HostLog());
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S20_5_alongside_Explorer_the_icons_gadget_runs_on_the_desktop_layer_without_a_wallpaper()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var (user, _) = Folders(run);
        File.WriteAllText(Path.Combine(user, "readme.txt"), "r");
        WriteDesktopLayout(run);
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && Items(s).Count == 1, Startup);
        Assert.Equal("Running", (string?)status["plugins"]!.AsArray().First(p => (string?)p!["id"] == Icons)!["state"]);
        Assert.Equal("readme.txt", (string?)Items(status)[0]["label"]);
        Assert.Null(status["desktop"]);
        Assert.NotNull(Assert.Single(status["panels"]!.AsArray())!["z"]);
        Assert.Equal(0, await run.StopAsync());
    }

    private static (string User, string Common) Folders(HostRun run)
    {
        var user = Path.Combine(run.Root, "desktop-user");
        var common = Path.Combine(run.Root, "desktop-public");
        Directory.CreateDirectory(user);
        Directory.CreateDirectory(common);
        run.ExtraEnvironment["SUBZERODEV_WM_DESKTOP_FOLDERS"] = user + ";" + common;
        return (user, common);
    }

    /// <summary>One floating panel holding the icons gadget, away from the screen's corners; on the desktop layer by default.</summary>
    private static void WriteDesktopLayout(HostRun run, string behaviour = "desktop") =>
        run.WriteDataFile(Path.Combine("layouts", "subzerodev.default.json"), new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "subzerodev.default",
            ["displayName"] = "Test layout",
            ["panels"] = new JsonArray(new JsonObject
            {
                ["id"] = PanelId,
                ["monitor"] = "primary",
                ["placement"] = new JsonObject
                {
                    ["float"] = new JsonObject { ["x"] = 200, ["y"] = 150, ["width"] = 160, ["height"] = 360 },
                },
                ["thickness"] = 96,
                ["behaviour"] = behaviour,
                ["zones"] = new JsonObject
                {
                    ["start"] = new JsonArray(new JsonObject
                    {
                        ["id"] = Guid.NewGuid().ToString("D"),
                        ["plugin"] = Icons,
                        ["contribution"] = "icons",
                    }),
                },
            }),
        }.ToJsonString());

    private static void WritePreset(HostRun run, string id, JsonObject? wallpaper)
    {
        var preset = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = id,
            ["displayName"] = id,
            ["layout"] = "subzerodev.default",
            ["theme"] = "subzerodev.default",
            ["assetPacks"] = new JsonArray("test.walls"),
        };
        if (wallpaper is not null)
        {
            preset["wallpaper"] = wallpaper;
        }

        run.WriteDataFile(Path.Combine("presets", id + ".json"), preset.ToJsonString());
    }

    private static void WriteSettings(HostRun run, string preset) =>
        run.WriteDataFile("settings.json", new JsonObject { ["schemaVersion"] = 1, ["activePreset"] = preset }.ToJsonString());

    private static List<JsonObject> Windows(JsonObject status) =>
        [.. status["desktop"]?["windows"]?.AsArray().Select(w => w!.AsObject()) ?? []];

    private static List<JsonObject> Items(JsonObject status) =>
        [.. status["contributions"]!.AsArray()
            .Where(c => (string?)c!["plugin"] == Icons && c["reason"] is null)
            .SelectMany(c => c!["interactive"]!.AsArray().Select(e => e!.AsObject()))];

    private static async Task ShowDesktopAsync(HostRun run)
    {
        await WaitUntilAsync(() => !File.Exists(Path.Combine(run.HookDirectory, "invoke")), "the previous invoke to be taken");
        var before = DesktopActions(run).Length;
        Hook(run, "invoke", "host.desktop.show\nkeyboard\n{}\n");
        await WaitUntilAsync(() => DesktopActions(run).Length > before, "show desktop to act");
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

internal static partial class Interactive
{
    /// <summary>Two clicks inside the system's double-click time.</summary>
    public static void DoubleClick(int x, int y)
    {
        Click(x, y);
        Click(x, y);
    }

    /// <summary>Whether the top-level window under a physical point is the given one.</summary>
    public static bool IsWindowAt(int x, int y, long handle)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            return GetAncestor(WindowFromPoint(new Point { X = x, Y = y }), GetAncestorRoot) == (nint)handle;
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    private const uint GetAncestorRoot = 2;

    [LibraryImport("user32.dll")]
    private static partial nint WindowFromPoint(Point point);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    public static int ProcessOf(nint hwnd)
    {
        _ = GetWindowThreadProcessId(hwnd, out var pid);
        return (int)pid;
    }
}

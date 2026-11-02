using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S18: the shell roles and session start-up in replacement mode. Under the test hook the desktop window
/// is never registered with <c>SetShellWindow</c>, and the work area, the desktop-ready signal, the
/// minimised-window setting, start-up programs and folder windows are recorded in <c>desktop-actions.log</c>
/// instead of being made, so the real desktop is never changed. Start-up programs are read from a scratch
/// <c>HKCU</c> key that stands in for both hives.
/// </summary>
public sealed partial class ShellRoleTests
{
    private const string RegistryRootVariable = "SUBZERODEV_WM_REGISTRY_ROOT";
    private const string TestKeyRoot = @"Software\SubZeroDev.WindowManager.Tests";
    private const string RunningWindows = "subzerodev.running-windows";
    private const int Created = 1;
    private const int Destroyed = 2;
    private const int Activated = 4;
    private const int Redraw = 6;
    private const int RudeActivated = 0x8004;
    private const int Flash = 0x8006;
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S18_1_the_hook_window_hears_a_window_come_flash_activate_and_go_and_a_flash_sets_attention()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        await run.WaitForAsync(s => (string?)s["shellRoles"]?["state"] == "Held" && Live(s), Startup);

        using var fixture = RunningWindowsTests.FixtureWindows.Open();
        var foreground = Interactive.Foreground();
        var hwnd = new[] { fixture["normal"], fixture["plain"] }.First(window => window != foreground);
        var key = Key(hwnd);
        await run.WaitForAsync(s => Item(s, key) is not null, TimeSpan.FromSeconds(5));

        Interactive.Flash(hwnd);
        var status = await run.WaitForAsync(s => (bool?)Item(s, key)?["attention"] == true, TimeSpan.FromMilliseconds(500));

        // Activating the window from its button answers the flash.
        Click(Item(status, key)!);
        await run.WaitForAsync(s => (bool?)Item(s, key)?["attention"] == false && Interactive.Foreground() == hwnd, TimeSpan.FromSeconds(5));

        Interactive.RequestClose(hwnd);
        status = await run.WaitForAsync(s => Codes(s, hwnd).Contains(Destroyed), TimeSpan.FromSeconds(5));
        var codes = Codes(status, hwnd);
        Assert.Contains(Created, codes);
        // Under the hook the host is not the task-manager window, so Windows sends the flash without its high bit.
        Assert.True(codes.Contains(Flash) || codes.Contains(Redraw), $"No flash among {string.Join(',', codes)}.");
        Assert.True(codes.Contains(Activated) || codes.Contains(RudeActivated), $"No activation among {string.Join(',', codes)}.");
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S18_2_another_shell_window_leaves_the_roles_alone_and_everything_else_starts()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        Hook(run, "shell-owner", "4242");
        run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows"));
        run.Start(HostRun.HostExe, "--mode", "replacement");

        var status = await run.WaitForAsync(s => (string?)s["shellRoles"]?["state"] == "Taken" && Live(s), Startup);
        Assert.Equal(0L, (long)status["shellRoles"]!["desktopWindow"]!);
        Assert.Equal(0L, (long)status["shellRoles"]!["hookWindow"]!);
        Assert.Contains("ShellRoleTaken: process 4242", run.HostLog());
        Assert.DoesNotContain("desktop-ready", DesktopActions(run));
        Assert.DoesNotContain("minimized-hide", DesktopActions(run));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S18_3_and_S18_6_the_desktop_is_signalled_once_and_minimised_windows_are_hidden()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows"));
        run.Start(HostRun.HostExe, "--mode", "replacement");
        await run.WaitForAsync(s => (bool?)s["ready"] == true && (string?)s["shellRoles"]?["state"] == "Held", Startup);
        await WaitUntilAsync(() => DesktopActions(run).Contains("minimized-hide"), "minimized-hide");

        // A layout applied again later must not signal the desktop a second time.
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        var actions = DesktopActions(run);
        Assert.Single(actions, line => line == "desktop-ready");
        Assert.Single(actions, line => line == "minimized-hide");
        Assert.True(Array.IndexOf(actions, "desktop-ready") > Array.FindIndex(actions, line => line.StartsWith("work-area ", StringComparison.Ordinal)),
            "The desktop was signalled before the first panel reserved its edge.");
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S18_4_start_up_programs_run_once_per_session()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var key = UseTestRegistry(run);
        try
        {
            using (var runKey = Registry.CurrentUser.CreateSubKey($@"{key}\HKCU\Software\Microsoft\Windows\CurrentVersion\Run"))
            {
                runKey.SetValue("Fixture", @"C:\Tools\fixture.exe /tray");
            }

            run.Start(HostRun.HostExe, "--mode", "replacement");
            await run.WaitForStatusAsync();
            await WaitUntilAsync(() => run.HostLog().Contains("Start-up programs: 1 started", StringComparison.Ordinal), "the start-up pass");
            Assert.Equal([@"start C:\Tools\fixture.exe /tray"], DesktopActions(run).Where(line => line.StartsWith("start ", StringComparison.Ordinal)));
            Assert.Equal(0, await run.StopAsync());

            // A host restarted in the same session starts nothing again (I24).
            File.Delete(Path.Combine(run.HookDirectory, "status.json"));
            File.Delete(Path.Combine(run.HookDirectory, "exit"));
            run.Start(HostRun.HostExe, "--mode", "replacement");
            await run.WaitForStatusAsync();
            await WaitUntilAsync(() => run.HostLog().Contains("already ran in this session", StringComparison.Ordinal), "the skipped start-up pass");
            Assert.Single(DesktopActions(run), line => line.StartsWith("start ", StringComparison.Ordinal));
            Assert.Equal(0, await run.StopAsync());
        }
        finally
        {
            DeleteTestRegistry(key);
        }
    }

    [Fact]
    public async Task S18_5_the_host_sets_the_work_area_journalled_first_and_the_guardian_restores_it_after_a_kill()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows"));
        run.Start(HostRun.GuardianExe, "--mode", "replacement");
        var status = await run.WaitForStatusAsync();
        await WaitUntilAsync(() => DesktopActions(run).Any(line => line.StartsWith("work-area ", StringComparison.Ordinal)), "the work area");

        var entries = run.ReadJournal()!["entries"]!.AsArray();
        Assert.All(entries, e => Assert.Equal("workArea", (string?)e!["kind"]));
        var entry = entries.First()!;
        var monitor = (string)entry["monitor"]!;
        var original = entry["payload"];
        Assert.Contains(DesktopActions(run), line => line.StartsWith($"work-area {monitor} ", StringComparison.Ordinal));

        using (var host = System.Diagnostics.Process.GetProcessById((int)status["hostPid"]!))
        {
            host.Kill();
            host.WaitForExit();
        }

        var expected = $"revert-work-area {monitor} {Rect(original)}";
        await WaitUntilAsync(() => DesktopActions(run).Contains(expected), "the guardian's revert");
    }

    [Fact]
    public async Task S18_7_host_files_open_starts_a_folder_window()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.Start(HostRun.HostExe, "--mode", "replacement");
        await run.WaitForStatusAsync();
        var folder = Path.Combine(run.Root, "folder");
        Directory.CreateDirectory(folder);

        Hook(run, "invoke", string.Join("\n", "host.files.open", "keyboard", new JsonObject { ["path"] = folder }.ToJsonString()) + "\n");
        await WaitUntilAsync(() => DesktopActions(run).Contains($"files-open {folder}"), "files-open");
        var status = await run.WaitForAsync(s => s["lastInvoke"] is JsonObject last && (string?)last["command"] == "host.files.open", TimeSpan.FromSeconds(5));
        Assert.Null(status["lastInvoke"]!["code"]);
        Assert.Equal(0, await run.StopAsync());
    }

    private static string Rect(JsonNode? rect) =>
        string.Create(CultureInfo.InvariantCulture, $"{(int)rect!["left"]!},{(int)rect["top"]!},{(int)rect["right"]!},{(int)rect["bottom"]!}");

    private static bool Live(JsonObject status) =>
        (bool?)status["ready"] == true
        && status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == RunningWindows && (string?)p["state"] == "Running");

    private static string Key(nint hwnd) => ((long)hwnd).ToString(CultureInfo.InvariantCulture);

    private static JsonObject? Item(JsonObject status, string key) =>
        status["contributions"]!.AsArray()
            .Where(c => (string?)c!["plugin"] == RunningWindows && c["reason"] is null)
            .SelectMany(c => c!["interactive"]!.AsArray().Select(e => e!.AsObject()))
            .FirstOrDefault(e => (string?)e["itemKey"] == key);

    private static List<int> Codes(JsonObject status, nint hwnd) =>
        [.. status["shellRoles"]!["events"]!.AsArray().Where(e => (long)e!["hwnd"]! == hwnd).Select(e => (int)e!["code"]!)];

    private static void Click(JsonObject item)
    {
        var bounds = item["bounds"]!;
        Interactive.Click(((int)bounds["left"]! + (int)bounds["right"]!) / 2, ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2);
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

    private static string UseTestRegistry(HostRun run)
    {
        var key = $@"{TestKeyRoot}\{Guid.NewGuid():N}";
        run.ExtraEnvironment[RegistryRootVariable] = key;
        return key;
    }

    private static void DeleteTestRegistry(string key)
    {
        Registry.CurrentUser.DeleteSubKeyTree(key, throwOnMissingSubKey: false);
        using var root = Registry.CurrentUser.OpenSubKey(TestKeyRoot, writable: true);
        if (root is { SubKeyCount: 0, ValueCount: 0 })
        {
            root.Dispose();
            Registry.CurrentUser.DeleteSubKey(TestKeyRoot, throwOnMissingSubKey: false);
        }
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
    /// <summary>Flashes the window's caption and taskbar button until it comes to the front, as an app wanting attention does.</summary>
    public static void Flash(nint hwnd)
    {
        const uint FlashAll = 0x3;
        const uint FlashTimerNoForeground = 0xC;
        var info = new FlashInfo
        {
            Size = (uint)Marshal.SizeOf<FlashInfo>(),
            Hwnd = hwnd,
            Flags = FlashAll | FlashTimerNoForeground,
        };
        FlashWindowEx(ref info);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FlashInfo
    {
        public uint Size;
        public nint Hwnd;
        public uint Flags;
        public uint Count;
        public uint Timeout;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool FlashWindowEx(ref FlashInfo info);
}

using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S12: panels on every monitor at the right DPI, stepping aside for full-screen windows, coming back
/// after the shell restarts, and the <c>autoHide</c> and <c>desktop</c> behaviours. Extra monitors are
/// simulated through the test hook (the real display configuration is never touched); the shell restart is
/// simulated by removing the app bar and broadcasting <c>TaskbarCreated</c> to the host.
/// </summary>
public sealed class MonitorTests
{
    private const string Clock = "subzerodev.clock";
    private const string Buttons = "samples.buttons";
    private const string PanelId = "6f0d3c1e-5a3b-4f0e-9c1d-2b7a8e4f5a01";

    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Topology = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan Setup = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task S12_1_a_panel_on_all_monitors_follows_monitors_coming_and_going()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteLayout(run, "all", "bottom", "reserve");
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => Panels(s).Count == 1 && (bool?)s["ready"] == true, Startup);

        var simulated = Simulated("SIM-1", 4000, 0, 5280, 720, 96);
        SetMonitors(run, simulated);
        var status = await run.WaitForAsync(s => Panels(s).Count == 2, Topology);
        var onSim = Panels(status).Single(p => (string?)p["monitor"] == "SIM-1");
        Assert.Equal("AppBarRefused", (string?)onSim["reserveError"]);
        var bounds = onSim["bounds"]!.AsObject();
        Assert.Equal(4000, (int)bounds["left"]!);
        Assert.Equal(5280, (int)bounds["right"]!);
        Assert.Equal(720, (int)bounds["bottom"]!);
        Assert.Contains(Panels(status), p => (bool?)p["primary"] == true && p["appBarRect"] is not null);

        var removing = Stopwatch.StartNew();
        SetMonitors(run);
        await run.WaitForAsync(s => Panels(s).Count == 1 && Panels(s).All(p => (string?)p["monitor"] != "SIM-1"),
            Topology - removing.Elapsed);

        var adding = Stopwatch.StartNew();
        SetMonitors(run, simulated);
        await run.WaitForAsync(s => Panels(s).Count == 2, Topology - adding.Elapsed);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S12_2_thickness_stays_40_dip_at_100_and_150_percent()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteLayout(run, new JsonObject { ["device"] = "SIM-96" }, "bottom", "overlay");
        run.Start(HostRun.HostExe);
        await run.WaitForStatusAsync();
        SetMonitors(run, Simulated("SIM-96", 4000, 0, 5280, 720, 96), Simulated("SIM-144", 6000, 0, 7920, 1080, 144));

        var status = await run.WaitForAsync(s => Panels(s).Any(p => (string?)p["monitor"] == "SIM-96"), Topology);
        var panel = Panels(status).Single();
        Assert.Equal(40, (int)panel["thicknessPx"]!);
        Assert.Equal(40, Height(panel));

        WriteLayout(run, new JsonObject { ["device"] = "SIM-144" }, "bottom", "overlay");
        status = await run.WaitForAsync(s => Panels(s).Any(p => (string?)p["monitor"] == "SIM-144"), TimeSpan.FromSeconds(5));
        panel = Panels(status).Single();
        Assert.Equal(60, (int)panel["thicknessPx"]!);
        Assert.Equal(60, Height(panel));
        Assert.Equal(1.5, (double)panel["scale"]!);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S12_3_a_full_screen_window_drops_the_panels_on_its_monitor_below_it()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteLayout(run, new JsonObject { ["device"] = "SIM-1" }, "bottom", "overlay");
        run.Start(HostRun.HostExe);
        await run.WaitForStatusAsync();
        SetMonitors(run, Simulated("SIM-1", 4000, 0, 5280, 720, 96));
        // Setting up, not the acceptance: a loaded machine may take longer than the topology budget here.
        await run.WaitForAsync(s => Panels(s).Any(p => (string?)p["monitor"] == "SIM-1" && (bool?)p["topmost"] == true), Setup);

        using (var fullscreen = FullscreenWindow.Open(4000, 0, 5280, 720))
        {
            Assert.True(fullscreen.Foreground, "The full-screen fixture could not take the foreground.");
            await run.WaitForAsync(s => (bool?)Panels(s).Single()["topmost"] == false, Second);
        }

        await run.WaitForAsync(s => (bool?)Panels(s).Single()["topmost"] == true, Second);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S12_4_reserve_panels_register_again_when_the_shell_restarts()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"));
        run.Start(HostRun.HostExe);
        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && Panels(s).Single()["appBarRect"] is not null, Startup);
        var reduced = Interactive.PrimaryWorkArea();
        var panel = (nint)(long)Panels(status).Single()["handle"]!;

        // What Explorer's restart does to every app bar: it forgets them all.
        Interactive.ForgetAppBar(panel);
        Assert.True(await WaitAsync(() => Interactive.PrimaryWorkArea() != reduced, TimeSpan.FromSeconds(5)),
            "The work area did not grow once the app bar was forgotten.");

        var restarting = Stopwatch.StartNew();
        Interactive.SendTaskbarCreated((nint)(long)status["shellEventsWindow"]!);
        Assert.True(await WaitAsync(() => Interactive.PrimaryWorkArea() == reduced, TimeSpan.FromSeconds(5)),
            $"The work area was {Interactive.PrimaryWorkArea()} after {restarting.Elapsed}, expected {reduced}.");
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S12_5_an_auto_hide_panel_collapses_when_left_and_expands_at_its_edge()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        WriteLayout(run, "primary", "top", "autoHide");
        var area = Interactive.PrimaryWorkArea();
        var centreX = (area.Left + area.Right) / 2;
        var centreY = (area.Top + area.Bottom) / 2;
        var cursor = Interactive.CursorPosition();
        try
        {
            Interactive.MoveTo(centreX, centreY);
            run.Start(HostRun.HostExe);
            var status = await run.WaitForAsync(s => (bool?)s["ready"] == true && (bool?)Panels(s).SingleOrDefault()?["collapsed"] == true,
                Startup);
            var handle = (nint)(long)Panels(status).Single()["handle"]!;
            var collapsed = Interactive.WindowRect(handle);
            Assert.Equal(2, collapsed.Bottom - collapsed.Top);
            Assert.Equal(area.Top, collapsed.Top);

            var touching = Stopwatch.StartNew();
            Interactive.MoveTo(centreX, area.Top);
            var expanded = false;
            while (touching.Elapsed < TimeSpan.FromSeconds(2) && !expanded)
            {
                var rect = Interactive.WindowRect(handle);
                expanded = rect.Bottom - rect.Top > 2;
                if (!expanded)
                {
                    await Task.Delay(5, TestContext.Current.CancellationToken);
                }
            }

            Assert.True(expanded, "The panel did not expand.");
            Assert.True(touching.Elapsed <= TimeSpan.FromMilliseconds(200), $"The panel expanded after {touching.Elapsed}.");

            Interactive.MoveTo(centreX, centreY);
            await run.WaitForAsync(s => (bool?)Panels(s).Single()["collapsed"] == true, TimeSpan.FromSeconds(3));
            Assert.Equal(0, await run.StopAsync());
        }
        finally
        {
            Interactive.MoveTo(cursor.X, cursor.Y);
        }
    }

    [Fact]
    public async Task S12_6_a_desktop_panel_stays_below_normal_windows_even_when_activated()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Buttons, ["buttons"]);
        WriteLayout(run, "primary", "bottom", "desktop", (Guid.NewGuid(), Buttons, "probe"));
        run.Start(HostRun.HostExe);
        var status = await run.WaitForAsync(s => (bool?)s["ready"] == true, Startup);
        var panel = (nint)(long)Panels(status).Single()["handle"]!;
        Assert.False((bool)Panels(status).Single()["topmost"]!);

        using var fixture = RunningWindowsTests.FixtureWindows.Open();
        // Activating the panel (as a click on it would) must not lift it above other windows.
        File.WriteAllText(Path.Combine(run.HookDirectory, "focus-panel"), PanelId);
        await run.WaitForAsync(s => Panels(s).Single()["focused"] is not null, TimeSpan.FromSeconds(5));
        await Task.Delay(250, TestContext.Current.CancellationToken);

        Assert.True(Interactive.IsBelow(panel, fixture["normal"]), "The desktop panel rose above a normal window.");
        Assert.False(Interactive.IsTopmost(panel));
        Assert.Equal(0, await run.StopAsync());
    }

    private static List<JsonObject> Panels(JsonObject status) =>
        [.. status["panels"]!.AsArray().Select(p => p!.AsObject())];

    private static int Height(JsonObject panel) =>
        (int)panel["bounds"]!["bottom"]! - (int)panel["bounds"]!["top"]!;

    private static JsonObject Simulated(string id, int left, int top, int right, int bottom, int dpi) => new()
    {
        ["id"] = id,
        ["left"] = left,
        ["top"] = top,
        ["right"] = right,
        ["bottom"] = bottom,
        ["dpi"] = dpi,
    };

    private static void SetMonitors(HostRun run, params JsonObject[] monitors) =>
        File.WriteAllText(Path.Combine(run.HookDirectory, "monitors"),
            new JsonObject { ["monitors"] = new JsonArray([.. monitors.Select(m => m.DeepClone())]) }.ToJsonString());

    private static void WriteLayout(HostRun run, JsonNode monitor, string edge, string behaviour,
        params (Guid Id, string Plugin, string Contribution)[] instances)
    {
        var layout = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "subzerodev.default",
            ["displayName"] = "Test layout",
            ["panels"] = new JsonArray(new JsonObject
            {
                ["id"] = PanelId,
                ["monitor"] = monitor,
                ["placement"] = new JsonObject { ["edge"] = edge },
                ["thickness"] = 40,
                ["behaviour"] = behaviour,
                ["zones"] = new JsonObject
                {
                    ["end"] = new JsonArray([.. instances.Select(i => (JsonNode)new JsonObject
                    {
                        ["id"] = i.Id.ToString("D"),
                        ["plugin"] = i.Plugin,
                        ["contribution"] = i.Contribution,
                    })]),
                },
            }),
        };
        run.WriteDataFile(Path.Combine("layouts", "subzerodev.default.json"), layout.ToJsonString());
    }

    private static async Task<bool> WaitAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return true;
            }

            await Task.Delay(50, TestContext.Current.CancellationToken);
        }

        return condition();
    }

    /// <summary>The test plugin in <c>fixture-fullscreen</c> mode: a borderless foreground window over a rectangle.</summary>
    private sealed class FullscreenWindow : IDisposable
    {
        private readonly Process _process;

        private FullscreenWindow(Process process) => _process = process;

        public bool Foreground { get; private set; }

        public static FullscreenWindow Open(int left, int top, int right, int bottom)
        {
            var start = new ProcessStartInfo(Path.Combine(HostRun.TestPluginDirectory, "SubZeroDev.WindowManager.TestPlugin.exe"))
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("fixture-fullscreen");
            foreach (var value in new[] { left, top, right, bottom })
            {
                start.ArgumentList.Add(value.ToString(CultureInfo.InvariantCulture));
            }

            var window = new FullscreenWindow(Process.Start(start)!);
            try
            {
                while (window._process.StandardOutput.ReadLine() is { } line && line != "ready")
                {
                    window.Foreground = line.Split(' ')[1] == "true";
                }

                return window;
            }
            catch
            {
                window.Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            try
            {
                _process.Kill();
                _process.WaitForExit(5000);
            }
            catch (InvalidOperationException)
            {
            }

            _process.Dispose();
        }
    }
}

internal static partial class Interactive
{
    public static (int X, int Y) CursorPosition()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            GetCursorPos(out var point);
            return (point.X, point.Y);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>Removes the window's app bar, as a shell restart does to every app bar.</summary>
    public static unsafe void ForgetAppBar(nint hwnd)
    {
        var data = new AppBarData { Size = (uint)sizeof(AppBarData), Window = hwnd };
        SHAppBarMessage(1, ref data);
    }

    /// <summary>Sends the shell's <c>TaskbarCreated</c> broadcast to one window.</summary>
    public static void SendTaskbarCreated(nint hwnd)
    {
        var message = RegisterWindowMessageW("TaskbarCreated");
        if (!PostMessageW(hwnd, message, 0, 0))
        {
            throw new InvalidOperationException("PostMessage failed.");
        }
    }

    /// <summary>Whether <paramref name="window"/> is lower in the z-order than <paramref name="other"/>.</summary>
    public static bool IsBelow(nint window, nint other)
    {
        const uint Next = 2;
        for (var next = GetWindow(other, Next); next != 0; next = GetWindow(next, Next))
        {
            if (next == window)
            {
                return true;
            }
        }

        return false;
    }

    public static bool IsTopmost(nint hwnd) => (GetWindowLongPtrW(hwnd, -20) & 0x8) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public Rect Bounds;
        public nint Param;
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out Point point);

    [LibraryImport("shell32.dll")]
    private static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindowLongPtrW(nint hwnd, int index);
}

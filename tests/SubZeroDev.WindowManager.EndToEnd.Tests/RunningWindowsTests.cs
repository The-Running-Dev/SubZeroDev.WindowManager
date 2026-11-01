using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S7: the built-in running-windows strip. The test plugin's <c>fixture-windows</c> mode opens one
/// window of each kind the list must judge; its <c>subscriber</c> scenario logs every <c>windows.running</c>
/// update it receives.
/// </summary>
public sealed partial class RunningWindowsTests
{
    private const string RunningWindows = "subzerodev.running-windows";
    private const string Subscriber = "samples.subscriber";
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    [Fact]
    public async Task S7_1_and_S7_2_a_window_is_listed_within_a_second_and_excluded_kinds_never_are()
    {
        HostRun.RequireDesktop();
        using var run = Start(out _);
        await run.WaitForAsync(s => Live(s, RunningWindows), Startup);

        using var fixture = FixtureWindows.Open();
        var normal = Key(fixture["normal"]);
        var status = await run.WaitForAsync(s => Keys(s).Contains(normal), Second);
        Assert.Equal($"{fixture.Token} normal", (string?)Item(status, normal)["label"]);

        await Task.Delay(TimeSpan.FromMilliseconds(500), TestContext.Current.CancellationToken);
        var keys = Keys(await run.WaitForStatusAsync());
        Assert.Contains(Key(fixture["plain"]), keys);
        foreach (var kind in new[] { "invisible", "owned", "tool", "cloaked", "untitled" })
        {
            Assert.DoesNotContain(Key(fixture[kind]), keys);
        }

        Interactive.RequestClose(fixture["normal"]);
        await run.WaitForAsync(s => !Keys(s).Contains(normal), Second);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S7_3_S7_4_and_S7_5_a_click_restores_and_raises_a_second_minimises_and_icons_resolve()
    {
        HostRun.RequireDesktop();
        using var run = Start(out _);
        await run.WaitForAsync(s => Live(s, RunningWindows), Startup);
        using var fixture = FixtureWindows.Open();
        var hwnd = fixture["normal"];
        var normal = Key(hwnd);
        var plain = Key(fixture["plain"]);

        // S7.5: the class icon is extracted; the plain window has none anywhere, so the default shows.
        var status = await run.WaitForAsync(s => Keys(s).Contains(normal) && Keys(s).Contains(plain)
            && (string?)Item(s, normal)["iconSource"] == "extracted" && (string?)Item(s, plain)["iconSource"] == "default", TimeSpan.FromSeconds(5));
        Assert.True((bool)Item(status, normal)["hasIcon"]!);
        Assert.True((bool)Item(status, plain)["hasIcon"]!);

        Interactive.Minimise(hwnd);
        status = await run.WaitForAsync(s => Interactive.IsMinimised(hwnd) && Item(s, normal)["active"]?.GetValue<bool>() == false, TimeSpan.FromSeconds(5));
        Click(Item(status, normal));
        status = await run.WaitForAsync(s => !Interactive.IsMinimised(hwnd) && Interactive.Foreground() == hwnd
            && Item(s, normal)["active"]?.GetValue<bool>() == true, TimeSpan.FromSeconds(5));

        // S7.4: the foreground window's item is active and its Button shows it.
        status = await run.WaitForAsync(s => (string?)Item(s, normal)["activeState"] == "Active", Second);
        Assert.All(Items(status).Where(item => (string?)item["itemKey"] != normal), item => Assert.False((bool)item["active"]!));

        Click(Item(status, normal));
        await run.WaitForAsync(_ => Interactive.IsMinimised(hwnd), TimeSpan.FromSeconds(5));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S7_6_this_monitor_lists_only_the_panel_monitor_windows()
    {
        HostRun.RequireDesktop();
        var all = Guid.NewGuid();
        var mine = Guid.NewGuid();
        using var run = HostRun.Create();
        run.WriteDefaultLayout(
            (all, RunningWindows, "windows", new JsonObject { ["scope"] = "allMonitors" }),
            (mine, RunningWindows, "windows", new JsonObject { ["scope"] = "thisMonitor" }));
        run.Start(HostRun.HostExe);
        using var fixture = FixtureWindows.Open();
        var primary = Interactive.PrimaryDevice();

        // With one monitor both lists match; the filter is still exercised on every window's device.
        var status = await run.WaitForAsync(s =>
        {
            var everything = Keys(s, all);
            var expected = everything.Where(key => Interactive.DeviceOf(nint.Parse(key, CultureInfo.InvariantCulture)) == primary).ToList();
            return everything.Contains(Key(fixture["normal"])) && Keys(s, mine).SequenceEqual(expected);
        }, Startup);
        Assert.NotEmpty(Keys(status, mine));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S7_7_killing_the_worker_tells_subscribers_and_shows_Faulted()
    {
        HostRun.RequireDesktop();
        using var run = Start(out var data);
        var status = await run.WaitForAsync(s => Live(s, RunningWindows) && Live(s, Subscriber), Startup);
        await WaitForAsync(() => Lines(data).Any(line => line.StartsWith("available true ", StringComparison.Ordinal)), TimeSpan.FromSeconds(5));
        var feed = status["feeds"]!.AsArray().Single(f => (string?)f!["id"] == "windows.running")!;
        Assert.Equal(RunningWindows, (string?)feed["provider"]);

        using (var worker = Process.GetProcessById((int)status["plugins"]!.AsArray().Single(p => (string?)p!["id"] == RunningWindows)!["pid"]!))
        {
            worker.Kill();
        }

        await run.WaitForAsync(s => (string?)Contribution(s, RunningWindows)?["reason"] == "Faulted", TimeSpan.FromSeconds(10));
        await WaitForAsync(() => Lines(data).Any(line => line.StartsWith("available false", StringComparison.Ordinal)), TimeSpan.FromSeconds(5));
        Assert.Equal(0, await run.StopAsync());
    }

    private static HostRun Start(out string data)
    {
        var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Subscriber, ["subscriber"], manifest =>
        {
            manifest["commands"] = new JsonArray();
            manifest["subscribes"] = new JsonArray("windows.running");
        });
        run.WriteDefaultLayout((Guid.NewGuid(), RunningWindows, "windows"), (Guid.NewGuid(), Subscriber, "probe"));
        data = Path.Combine(run.DataRoot, "plugin-data", Subscriber);
        Directory.CreateDirectory(data);
        run.Start(HostRun.HostExe);
        return run;
    }

    private static string Key(nint hwnd) => ((long)hwnd).ToString(CultureInfo.InvariantCulture);

    private static bool Live(JsonObject status, string plugin) =>
        status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == plugin && (string?)p["state"] == "Running")
        && Contribution(status, plugin) is { } contribution && contribution["reason"] is null;

    private static JsonObject? Contribution(JsonObject status, string plugin) =>
        status["contributions"]!.AsArray().FirstOrDefault(c => (string?)c!["plugin"] == plugin)?.AsObject();

    private static List<JsonObject> Items(JsonObject status, Guid? instance = null) =>
        status["contributions"]!.AsArray()
            .Where(c => (string?)c!["plugin"] == RunningWindows && c["reason"] is null
                && (instance is null || (string?)c["instanceId"] == instance.Value.ToString("D")))
            .Take(1)
            .SelectMany(c => c!["interactive"]!.AsArray().Select(e => e!.AsObject()))
            .Where(e => e["itemKey"] is not null)
            .ToList();

    private static List<string> Keys(JsonObject status, Guid? instance = null) =>
        [.. Items(status, instance).Select(item => (string)item["itemKey"]!)];

    private static JsonObject Item(JsonObject status, string key) =>
        Items(status).FirstOrDefault(item => (string?)item["itemKey"] == key) ?? [];

    private static void Click(JsonObject item)
    {
        var bounds = item["bounds"]!;
        Interactive.Click(((int)bounds["left"]! + (int)bounds["right"]!) / 2, ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2);
    }

    private static string[] Lines(string data)
    {
        var path = Path.Combine(data, "feed.log");
        if (!File.Exists(path))
        {
            return [];
        }

        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "Timed out waiting for feed.log.");
            await Task.Delay(50, TestContext.Current.CancellationToken);
        }
    }

    /// <summary>The test plugin in <c>fixture-windows</c> mode, killed on dispose.</summary>
    private sealed class FixtureWindows : IDisposable
    {
        private readonly Process _process;
        private readonly Dictionary<string, nint> _windows = new(StringComparer.Ordinal);

        private FixtureWindows(Process process, string token)
        {
            _process = process;
            Token = token;
        }

        public string Token { get; }

        public nint this[string kind] => _windows[kind];

        public static FixtureWindows Open()
        {
            var token = "szd-" + Guid.NewGuid().ToString("N")[..8];
            var start = new ProcessStartInfo(Path.Combine(HostRun.TestPluginDirectory, "SubZeroDev.WindowManager.TestPlugin.exe"))
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
            };
            start.ArgumentList.Add("fixture-windows");
            start.ArgumentList.Add(token);
            var fixture = new FixtureWindows(Process.Start(start)!, token);
            try
            {
                while (fixture._process.StandardOutput.ReadLine() is { } line && line != "ready")
                {
                    var parts = line.Split(' ');
                    fixture._windows[parts[0]] = nint.Parse(parts[1], CultureInfo.InvariantCulture);
                }

                Assert.Equal(7, fixture._windows.Count);
                return fixture;
            }
            catch
            {
                fixture.Dispose();
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
    public static void Minimise(nint hwnd)
    {
        const int Minimize = 6;
        ShowWindow(hwnd, Minimize);
    }

    /// <summary>The display device name of the primary monitor, as the running-windows feed reports monitors.</summary>
    public static string PrimaryDevice() => Device(MonitorFromPoint(0, 1));

    public static string DeviceOf(nint hwnd) => Device(MonitorFromWindow(hwnd, 2));

    private static unsafe string Device(nint monitor)
    {
        var info = new MonitorInfoEx { Size = (uint)sizeof(MonitorInfoEx) };
        return GetMonitorInfoW(monitor, ref info) ? new string(info.Device).TrimEnd('\0') : string.Empty;
    }

    [StructLayout(LayoutKind.Sequential)]
    private unsafe struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        public fixed char Device[32];
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfoEx info);
}

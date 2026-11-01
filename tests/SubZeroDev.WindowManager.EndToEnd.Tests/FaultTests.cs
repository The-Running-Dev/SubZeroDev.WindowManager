using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S3: a faulty plugin beside the clock. The test plugin's <c>faulty</c> scenario acts on switch
/// files in its data folder (crash, hang, burst, flood) and logs each instance it creates.
/// </summary>
public sealed partial class FaultTests
{
    private const string Faulty = "samples.faulty";
    private const string Clock = "subzerodev.clock";
    private const string TestPluginProcess = "SubZeroDev.WindowManager.TestPlugin";

    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task S3_1_a_crash_shows_Faulted_within_a_second_and_the_clock_keeps_running()
    {
        HostRun.RequireDesktop();
        using var run = Start(out var data);
        var status = await run.WaitForAsync(s => Live(s, Faulty) && Live(s, Clock), Startup);
        var clockPid = Pid(status, Clock);

        File.WriteAllText(Path.Combine(data, "crash"), "1");
        var faulted = await run.WaitForAsync(s => Reason(s, Faulty) == "Faulted", TimeSpan.FromSeconds(10));
        var exited = Times(Path.Combine(data, "exited.log")).Single();

        Assert.InRange((long)faulted["nowUtcMs"]! - exited, 0, 1000);
        Assert.True(Live(faulted, Clock));
        Assert.Equal(clockPid, Pid(faulted, Clock));
        Assert.True((bool)Contribution(faulted, Faulty)["hasRetry"]!);

        status = await run.WaitForAsync(s => Live(s, Faulty), TimeSpan.FromSeconds(10));
        Assert.Equal(clockPid, Pid(status, Clock));
        await run.WaitForAsync(s => ClockText(s) == Minute(DateTime.Now), TimeSpan.FromSeconds(3));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S3_2_a_worker_that_stops_answering_pings_is_faulted_and_killed()
    {
        HostRun.RequireDesktop();
        using var run = Start(out var data);
        var status = await run.WaitForAsync(s => Live(s, Faulty), Startup);
        using var worker = Process.GetProcessById(Pid(status, Faulty)!.Value);
        _ = worker.Handle;

        File.WriteAllText(Path.Combine(data, "hang"), string.Empty);
        var faulted = await run.WaitForAsync(s => Plugin(s, Faulty)["state"]!.GetValue<string>() == "Faulted", TimeSpan.FromSeconds(25));
        var lastReply = long.Parse(File.ReadAllText(Path.Combine(data, "hang-at")).Trim(), CultureInfo.InvariantCulture);

        Assert.Equal("HeartbeatTimeout", (string?)Plugin(faulted, Faulty)["lastFault"]);
        Assert.InRange((long)faulted["nowUtcMs"]! - lastReply, 10_000, 15_000);
        Assert.True(worker.HasExited);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S3_3_to_S3_6_restarts_back_off_until_unavailable_and_retry_recovers()
    {
        HostRun.RequireDesktop();
        using var run = Start(out var data);
        await run.WaitForAsync(s => Live(s, Faulty) && Live(s, Clock), Startup);

        // A first click on the panel activates it, which costs more than handling a click; take it now.
        await ClickClockAsync(run);
        var stallsBefore = (long)(await run.WaitForAsync(_ => true, Startup))["watchdog"]!["stalls"]!;

        using var sampling = new CancellationTokenSource();
        var sampler = SampleProcessesAsync(run.DataRoot, sampling.Token);

        File.WriteAllText(Path.Combine(data, "crash"), "4");
        var unavailable = await run.WaitForAsync(s => Plugin(s, Faulty)["state"]!.GetValue<string>() == "Unavailable",
            TimeSpan.FromSeconds(90));
        Assert.Equal("Unavailable", Reason(unavailable, Faulty));
        Assert.True((bool)Contribution(unavailable, Faulty)["hasRetry"]!);
        Assert.Equal(4, (int)Plugin(unavailable, Faulty)["recentFaults"]!);

        // No automatic start once the budget is spent.
        await Task.Delay(TimeSpan.FromSeconds(3), TestContext.Current.CancellationToken);
        var created = Times(Path.Combine(data, "created.log"));
        var exited = Times(Path.Combine(data, "exited.log"));
        Assert.Equal(4, created.Count);
        Assert.Equal(4, exited.Count);
        double[] backoffs = [1, 5, 30];
        for (var i = 0; i < 3; i++)
        {
            var waited = (created[i + 1] - exited[i]) / 1000.0;
            Assert.InRange(waited, backoffs[i], backoffs[i] + 5);
        }

        // S3.5: a scripted click on the clock is handled within 200 ms while the plugin is down.
        Assert.InRange(await ClickClockAsync(run), 0, 200);

        await File.WriteAllTextAsync(Path.Combine(run.HookDirectory, "retry"), Faulty, TestContext.Current.CancellationToken);
        var recovered = await run.WaitForAsync(s => Live(s, Faulty), TimeSpan.FromSeconds(15));
        Assert.Equal(0, (int)Plugin(recovered, Faulty)["recentFaults"]!);
        Assert.Equal(5, Times(Path.Combine(data, "created.log")).Count);

        await sampling.CancelAsync();
        var (most, overlaps) = await sampler;
        Assert.True(most == 1, $"Up to {most} faulty-plugin processes ran at once: {overlaps}");
        var stalls = (long)recovered["watchdog"]!["stalls"]!;
        Assert.True(stallsBefore == stalls, $"{stalls - stallsBefore} UI stalls during the test:{Environment.NewLine}"
            + string.Join(Environment.NewLine, run.HostLog().Split('\n').Where(line => line.Contains("watchdog", StringComparison.Ordinal)
                || line.Contains(Faulty, StringComparison.Ordinal))));
        Assert.True(Live(recovered, Clock));
        await run.WaitForAsync(s => ClockText(s) == Minute(DateTime.Now), TimeSpan.FromSeconds(3));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S3_7_a_burst_coalesces_to_the_last_value_and_a_sustained_flood_faults()
    {
        HostRun.RequireDesktop();
        using var run = Start(out var data);
        await run.WaitForAsync(s => Live(s, Faulty), Startup);

        File.WriteAllText(Path.Combine(data, "burst"), string.Empty);
        var burst = await run.WaitForAsync(s => HostRun.Texts(s, Faulty).SingleOrDefault() is ["burst-final"], TimeSpan.FromSeconds(10));
        Assert.Equal("Running", (string?)Plugin(burst, Faulty)["state"]);
        await Task.Delay(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(["burst-final"], HostRun.Texts(await run.WaitForAsync(_ => true, Startup), Faulty).Single());

        var flooding = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        File.WriteAllText(Path.Combine(data, "flood"), string.Empty);
        var faulted = await run.WaitForAsync(s => (string?)Plugin(s, Faulty)["lastFault"] == "Flood", TimeSpan.FromSeconds(25));
        Assert.InRange((long)faulted["nowUtcMs"]! - flooding, 10_000, 15_000);
        Assert.Equal(0, await run.StopAsync());
    }

    private static HostRun Start(out string data)
    {
        var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, Faulty, ["faulty"]);
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"), (Guid.NewGuid(), Faulty, "probe"));
        data = Path.Combine(run.DataRoot, "plugin-data", Faulty);
        Directory.CreateDirectory(data);
        run.Start(HostRun.HostExe);
        return run;
    }

    /// <summary>Clicks the middle of the clock and returns how long the panel took to see the press, in ms.</summary>
    private static async Task<long> ClickClockAsync(HostRun run)
    {
        var clock = await run.WaitForAsync(s => Contribution(s, Clock)["bounds"] is JsonObject, TimeSpan.FromSeconds(5));
        var bounds = Contribution(clock, Clock)["bounds"]!;
        var clicked = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interactive.Click(((int)bounds["left"]! + (int)bounds["right"]!) / 2, ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2);
        var pressed = await run.WaitForAsync(s => (long)s["lastPointerPress"]! >= clicked, TimeSpan.FromSeconds(5));
        return (long)pressed["lastPointerPress"]! - clicked;
    }

    /// <summary>
    /// The most faulty-plugin processes running at once, sampling every 50 ms (I4). A process that has ended can still
    /// be listed while something holds a handle to it, so only those that have not exited count.
    /// </summary>
    private static async Task<(int Most, string Overlaps)> SampleProcessesAsync(string dataRoot, CancellationToken ct)
    {
        var most = 0;
        var overlaps = new List<string>();
        var folder = Path.Combine(dataRoot, "plugins", Faulty) + Path.DirectorySeparatorChar;
        while (!ct.IsCancellationRequested)
        {
            var seen = new List<string>();
            foreach (var process in Process.GetProcessesByName(TestPluginProcess))
            {
                using (process)
                {
                    try
                    {
                        if (process.MainModule?.FileName.StartsWith(folder, StringComparison.OrdinalIgnoreCase) == true && !process.HasExited)
                        {
                            seen.Add($"{process.Id}@{process.StartTime:HH:mm:ss.fff}");
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }

            if (seen.Count > 1 && overlaps.Count < 10)
            {
                overlaps.Add($"{DateTime.Now:HH:mm:ss.fff} [{string.Join(", ", seen)}]");
            }

            most = Math.Max(most, seen.Count);
            try
            {
                await Task.Delay(50, ct);
            }
            catch (OperationCanceledException)
            {
            }
        }

        return (most, string.Join("; ", overlaps));
    }

    private static JsonObject Contribution(JsonObject status, string plugin) =>
        status["contributions"]!.AsArray().Single(c => (string?)c!["plugin"] == plugin)!.AsObject();

    private static JsonObject Plugin(JsonObject status, string plugin) =>
        status["plugins"]!.AsArray().FirstOrDefault(p => (string?)p!["id"] == plugin)?.AsObject() ?? [];

    private static string? Reason(JsonObject status, string plugin) => (string?)Contribution(status, plugin)["reason"];

    private static bool Live(JsonObject status, string plugin) =>
        (string?)Plugin(status, plugin)["state"] == "Running" && Reason(status, plugin) is null
        && Contribution(status, plugin)["texts"]!.AsArray().Count > 0;

    private static int? Pid(JsonObject status, string plugin) => (int?)Plugin(status, plugin)["pid"];

    private static string? ClockText(JsonObject status) =>
        HostRun.Texts(status, Clock).SingleOrDefault()?.FirstOrDefault(t => t.Length == 5 && t[2] == ':');

    private static string Minute(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static List<long> Times(string path) =>
        File.Exists(path)
            ? File.ReadAllLines(path).Where(line => line.Length > 0)
                .Select(line => long.Parse(line.Split(' ')[0], CultureInfo.InvariantCulture)).ToList()
            : [];
}

internal static partial class Interactive
{
    /// <summary>Moves the cursor to the physical-pixel point and clicks the left button there.</summary>
    public static void Click(int x, int y)
    {
        const uint LeftDown = 0x0002;
        const uint LeftUp = 0x0004;
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            if (!SetCursorPos(x, y))
            {
                throw new InvalidOperationException("SetCursorPos failed.");
            }

            mouse_event(LeftDown, 0, 0, 0, 0);
            mouse_event(LeftUp, 0, 0, 0, 0);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetCursorPos(int x, int y);

    [LibraryImport("user32.dll")]
    private static partial void mouse_event(uint flags, int dx, int dy, int data, nint extraInfo);
}

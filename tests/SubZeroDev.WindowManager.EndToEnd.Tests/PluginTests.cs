using System.Diagnostics;
using System.Globalization;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>Slice S2: plugins run as supervised workers and render through retained views.</summary>
public sealed partial class PluginTests
{
    private const string Clock = "subzerodev.clock";
    private const string TestPlugin = "subzerodev.testplugin";

    [Fact]
    public async Task S2_2_the_clock_shows_the_local_time_and_follows_the_minute()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var started = DateTime.Now;
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => ClockTexts(s).Count == 1, TimeSpan.FromSeconds(3) + Slack);
        var shown = Assert.Single(ClockTexts(status));
        Assert.Contains(shown, new[] { Minute(started), Minute(DateTime.Now) });

        var now = DateTime.Now;
        var boundary = now.Date.AddHours(now.Hour).AddMinutes(now.Minute + 1);
        await Task.Delay(boundary - DateTime.Now, TestContext.Current.CancellationToken);
        var expected = Minute(boundary);
        await run.WaitForAsync(s => ClockTexts(s).SingleOrDefault() == expected, TimeSpan.FromSeconds(2));

        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S2_3_killing_the_host_takes_the_worker_with_it()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        var host = run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => PluginPid(s, Clock) is not null, Startup);
        var pid = PluginPid(status, Clock)!.Value;
        Assert.Contains(pid, status["jobPids"]!.AsArray().Select(p => (int)p!));
        using var worker = Process.GetProcessById(pid);

        host.Kill();
        using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await worker.WaitForExitAsync(cancel.Token);
        Assert.True(worker.HasExited);
    }

    [Fact]
    public async Task S2_5_and_S2_6_invalid_views_and_stale_patches_leave_the_shown_view()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.CopyPlugin(HostRun.TestPluginDirectory, TestPlugin);
        run.WriteDefaultLayout((Guid.NewGuid(), TestPlugin, "probe"));
        run.Start(HostRun.HostExe);

        await run.WaitForAsync(s => HostRun.Texts(s, TestPlugin).Count == 1, Startup);
        var log = Path.Combine(run.DataRoot, "logs", "plugins",
            $"{TestPlugin}-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
        await WaitForLogAsync(log, text => CountOf(text, "1005") >= 4);

        var text = await ReadSharedAsync(log);
        Assert.Contains("more than once", text, StringComparison.Ordinal);
        Assert.Contains("hologram", text, StringComparison.Ordinal);
        Assert.Contains("background", text, StringComparison.Ordinal);
        Assert.Contains("fontSize", text, StringComparison.Ordinal);
        Assert.Contains("dropped", text, StringComparison.OrdinalIgnoreCase);

        var status = await run.WaitForAsync(_ => true, Startup);
        Assert.Equal(["ok"], Assert.Single(HostRun.Texts(status, TestPlugin)));
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S2_7_a_package_dropped_in_while_running_shows_after_a_layout_edit()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        run.Start(HostRun.HostExe);
        await run.WaitForAsync(s => ClockTexts(s).Count == 1, Startup);

        run.CopyPlugin(Path.Combine(HostRun.ContentDirectory, "plugins", Clock), "example.clock");
        await run.WaitForAsync(
            s => s["catalog"]!.AsArray().Any(p => (string?)p!["id"] == "example.clock" && (string?)p["health"] == "Valid"),
            TimeSpan.FromSeconds(10));

        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"), (Guid.NewGuid(), "example.clock", "clock"));
        var status = await run.WaitForAsync(
            s => ClockTexts(s).Count == 1 && ClockTexts(s, "example.clock").Count == 1,
            TimeSpan.FromSeconds(5) + Slack);

        Assert.Matches(TimePattern(), ClockTexts(status, "example.clock")[0]);
        Assert.Equal(0, await run.StopAsync());
    }

    [Fact]
    public async Task S2_8_exit_shuts_workers_down_and_kills_the_one_that_stays()
    {
        HostRun.RequireDesktop();
        using var run = HostRun.Create();
        const string stubborn = "subzerodev.stubborn";
        run.CopyPlugin(HostRun.TestPluginDirectory, stubborn, ["ignore-shutdown"]);
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"), (Guid.NewGuid(), stubborn, "probe"));
        run.Start(HostRun.HostExe);

        var status = await run.WaitForAsync(s => PluginPid(s, Clock) is not null && PluginPid(s, stubborn) is not null, Startup);
        using var clock = Process.GetProcessById(PluginPid(status, Clock)!.Value);
        using var held = Process.GetProcessById(PluginPid(status, stubborn)!.Value);
        _ = clock.Handle;
        _ = held.Handle;

        var stopping = Stopwatch.StartNew();
        Assert.Equal(0, await run.StopAsync());

        using (var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(3)))
        {
            await clock.WaitForExitAsync(cancel.Token);
        }

        Assert.Equal(0, clock.ExitCode);
        Assert.True(held.HasExited);
        Assert.InRange(stopping.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(10));
        var log = Path.Combine(run.DataRoot, "logs", "plugins",
            $"{stubborn}-{DateTime.Now.ToString("yyyyMMdd", CultureInfo.InvariantCulture)}.log");
        Assert.Contains("Killed after 3000 ms.", await ReadSharedAsync(log), StringComparison.Ordinal);
    }

    // The first run of a newly written worker exe waits on the virus scanner; S16 measures the real budgets.
    private static readonly TimeSpan Slack = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);

    [GeneratedRegex(@"^\d{2}:\d{2}$")]
    private static partial Regex TimePattern();

    private static string Minute(DateTime time) => time.ToString("HH:mm", CultureInfo.InvariantCulture);

    private static List<string> ClockTexts(JsonObject status, string plugin = Clock) =>
        HostRun.Texts(status, plugin).Select(texts => texts.FirstOrDefault(t => TimePattern().IsMatch(t)))
            .OfType<string>().ToList();

    private static int? PluginPid(JsonObject status, string plugin) =>
        status["plugins"]!.AsArray()
            .Where(p => (string?)p!["id"] == plugin && (string?)p["state"] == "Running")
            .Select(p => (int?)p!["pid"])
            .FirstOrDefault();

    private static int CountOf(string text, string value) => Regex.Matches(text, Regex.Escape(value)).Count;

    private static async Task WaitForLogAsync(string path, Func<string, bool> condition)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(path) && condition(await ReadSharedAsync(path)))
            {
                return;
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"{path} never matched. Contents: {(File.Exists(path) ? await ReadSharedAsync(path) : "<missing>")}");
    }

    private static async Task<string> ReadSharedAsync(string path)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}

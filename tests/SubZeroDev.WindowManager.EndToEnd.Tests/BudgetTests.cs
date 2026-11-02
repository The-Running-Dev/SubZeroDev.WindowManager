using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// Slice S16: performance budgets. Both tests are skipped unless <c>SUBZERODEV_WM_BUDGETS</c> asks for them.
/// <c>measure</c> runs the guardian with the three built-in plugins <c>SUBZERODEV_WM_BUDGET_RUNS</c> times (10 by
/// default) and records every metric; <c>reference</c> or <c>ci</c> runs startup and idle (3 times by default) and
/// fails when a median exceeds that column of <c>docs/budgets.md</c> by more than 10%. Either writes
/// <c>measurements.json</c> to <c>SUBZERODEV_WM_BUDGET_OUT</c> when set.
/// </summary>
public sealed partial class BudgetTests
{
    private const string ModeVariable = "SUBZERODEV_WM_BUDGETS";
    private const string RunsVariable = "SUBZERODEV_WM_BUDGET_RUNS";
    private const string OutVariable = "SUBZERODEV_WM_BUDGET_OUT";
    private const string Clock = "subzerodev.clock";
    private const string RunningWindows = "subzerodev.running-windows";
    private const string Launcher = "subzerodev.launcher";
    private const double Tolerance = 1.10;
    private static readonly string[] Plugins = [Clock, RunningWindows, Launcher];
    private static readonly TimeSpan Startup = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Idle = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan Reaction = TimeSpan.FromSeconds(5);

    /// <summary>Each metric's key in <c>docs/budgets.md</c>, in table order.</summary>
    internal static readonly string[] StartupAndIdle =
        ["startupMs", "hostPrivateMb", "clockPrivateMb", "runningWindowsPrivateMb", "launcherPrivateMb", "hostIdleCpuPercent"];

    internal static readonly string[] Interaction = ["hotkeyToPopupMs", "clickToForegroundMs"];

    [Fact]
    public async Task S16_1_measures_startup_idle_and_interaction_over_ten_runs()
    {
        if (Environment.GetEnvironmentVariable(ModeVariable) != "measure")
        {
            Assert.Skip($"Set {ModeVariable}=measure to measure the budgets.");
        }

        HostRun.RequireDesktop();
        var runs = await RunAsync(Runs(10), interaction: true);
        var summary = Summarise(runs, [.. StartupAndIdle, .. Interaction], budgets: null);
        Write(summary);
    }

    [Fact]
    public async Task S16_3_startup_and_idle_stay_within_budget()
    {
        var column = Environment.GetEnvironmentVariable(ModeVariable);
        if (column is not ("reference" or "ci"))
        {
            Assert.Skip($"Set {ModeVariable}=reference or {ModeVariable}=ci to check the budgets.");
        }

        HostRun.RequireDesktop();
        var budgets = ReadBudgets(column);
        var runs = await RunAsync(Runs(3), interaction: false);
        var summary = Summarise(runs, StartupAndIdle, budgets);
        Write(summary);

        var over = StartupAndIdle
            .Where(metric => Median(runs, metric) > budgets[metric] * Tolerance)
            .Select(metric => $"{metric}: median {Median(runs, metric):0.##} > budget {budgets[metric]:0.##} + 10%")
            .ToList();
        Assert.True(over.Count == 0, $"Over budget ({column}):{Environment.NewLine}{string.Join(Environment.NewLine, over)}");
    }

    private static async Task<List<Dictionary<string, double>>> RunAsync(int count, bool interaction)
    {
        var runs = new List<Dictionary<string, double>>();
        for (var i = 0; i < count; i++)
        {
            runs.Add(await MeasureOnceAsync(interaction));
            TestContext.Current.TestOutputHelper?.WriteLine(
                $"run {i + 1}: " + string.Join(", ", runs[^1].Select(pair => $"{pair.Key}={pair.Value:0.##}")));
        }

        return runs;
    }

    /// <summary>One guardian start: time to live views, then 60 s idle, then (optionally) the hotkey and a click.</summary>
    private static async Task<Dictionary<string, double>> MeasureOnceAsync(bool interaction)
    {
        var measured = new Dictionary<string, double>(StringComparer.Ordinal);
        using var run = HostRun.Create();
        run.WriteDefaultLayout((Guid.NewGuid(), Clock, "clock"), (Guid.NewGuid(), RunningWindows, "windows"), (Guid.NewGuid(), Launcher, "launcher"));
        if (LauncherTests.OpenChord.Override is { } chord)
        {
            run.WriteDataFile("settings.json", new JsonObject
            {
                ["schemaVersion"] = 1,
                ["activePreset"] = "subzerodev.default",
                ["hotkeys"] = new JsonObject { ["launcher.open"] = chord },
            }.ToJsonString());
        }

        var started = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        run.Start(HostRun.GuardianExe);
        var status = await run.WaitForAsync(AllLive, Startup);
        measured["startupMs"] = Plugins.Max(plugin => (long)Contribution(status, plugin)!["liveSinceUtcMs"]!) - started;

        using var host = Process.GetProcessById((int)status["hostPid"]!);
        var cpuBefore = host.TotalProcessorTime;
        var wall = Stopwatch.StartNew();
        await Task.Delay(Idle, TestContext.Current.CancellationToken);
        host.Refresh();
        measured["hostIdleCpuPercent"] = (host.TotalProcessorTime - cpuBefore).TotalMilliseconds / wall.Elapsed.TotalMilliseconds * 100;
        measured["hostPrivateMb"] = Megabytes(host.PrivateMemorySize64);
        status = await run.WaitForAsync(AllLive, Reaction);
        measured["clockPrivateMb"] = WorkerMegabytes(status, Clock);
        measured["runningWindowsPrivateMb"] = WorkerMegabytes(status, RunningWindows);
        measured["launcherPrivateMb"] = WorkerMegabytes(status, Launcher);

        if (interaction)
        {
            measured["hotkeyToPopupMs"] = await HotkeyAsync(run);
            measured["clickToForegroundMs"] = await ClickAsync(run);
        }

        Assert.Equal(0, await run.StopAsync());
        return measured;
    }

    /// <summary>As S8.1: the first open builds the popup, so the second is timed, by the host's own ready stamp.</summary>
    private static async Task<double> HotkeyAsync(HostRun run)
    {
        var work = Interactive.PrimaryWorkArea();
        Interactive.MoveTo((work.Left + work.Right) / 2, (work.Top + work.Bottom) / 2);
        Interactive.Press(LauncherTests.OpenChord.Keys);
        await run.WaitForAsync(s => PopupOpen(s) && (string?)Popup(s)["focusedInput"] == "query", Reaction);
        Interactive.Press(Interactive.Escape);
        await run.WaitForAsync(s => !PopupOpen(s), Reaction);

        var pressed = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Interactive.Press(LauncherTests.OpenChord.Keys);
        var status = await run.WaitForAsync(s => PopupOpen(s) && (string?)Popup(s)["focusedInput"] == "query"
            && (long)Popup(s)["readyAtUtcMs"]! >= pressed, Reaction);
        var latency = (long)Popup(status)["readyAtUtcMs"]! - pressed;
        Interactive.Press(Interactive.Escape);
        await run.WaitForAsync(s => !PopupOpen(s), Reaction);
        return latency;
    }

    /// <summary>As S7.3: a minimised window's item is clicked; timed until that window is the foreground one.</summary>
    private static async Task<double> ClickAsync(HostRun run)
    {
        using var fixture = RunningWindowsTests.FixtureWindows.Open();
        var hwnd = fixture["normal"];
        var key = ((long)hwnd).ToString(CultureInfo.InvariantCulture);
        Interactive.Minimise(hwnd);
        var status = await run.WaitForAsync(s => Interactive.IsMinimised(hwnd) && Item(s, key)?["active"]?.GetValue<bool>() == false, Reaction);
        var bounds = Item(status, key)!["bounds"]!;

        var clicked = Stopwatch.StartNew();
        Interactive.Click(((int)bounds["left"]! + (int)bounds["right"]!) / 2, ((int)bounds["top"]! + (int)bounds["bottom"]!) / 2);
        while (Interactive.Foreground() != hwnd)
        {
            Assert.True(clicked.Elapsed < Reaction, "The clicked window never came to the foreground.");
            Thread.Sleep(1);
        }

        return clicked.Elapsed.TotalMilliseconds;
    }

    private static bool AllLive(JsonObject status) =>
        Plugins.All(plugin =>
            status["plugins"]!.AsArray().Any(p => (string?)p!["id"] == plugin && (string?)p["state"] == "Running" && p["pid"] is not null)
            && Contribution(status, plugin) is { } contribution && contribution["reason"] is null && contribution["liveSinceUtcMs"] is not null)
        && status["hotkeys"]!.AsArray().Any(h => (string?)h!["command"] == "launcher.open" && (string?)h["state"] == "bound");

    private static JsonObject? Contribution(JsonObject status, string plugin) =>
        status["contributions"]!.AsArray().FirstOrDefault(c => (string?)c!["plugin"] == plugin)?.AsObject();

    private static JsonObject? Item(JsonObject status, string key) =>
        Contribution(status, RunningWindows)?["interactive"]?.AsArray()
            .FirstOrDefault(e => (string?)e!["itemKey"] == key)?.AsObject();

    private static JsonObject Popup(JsonObject status) =>
        status["popups"]!.AsArray().FirstOrDefault(p => (string?)p!["plugin"] == Launcher && (string?)p["popup"] == "search")?.AsObject() ?? [];

    private static bool PopupOpen(JsonObject status) => Popup(status)["open"]?.GetValue<bool>() == true;

    private static double WorkerMegabytes(JsonObject status, string plugin)
    {
        var pid = (int)status["plugins"]!.AsArray().Single(p => (string?)p!["id"] == plugin)!["pid"]!;
        using var worker = Process.GetProcessById(pid);
        return Megabytes(worker.PrivateMemorySize64);
    }

    private static double Megabytes(long bytes) => bytes / (1024.0 * 1024.0);

    private static int Runs(int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(RunsVariable), CultureInfo.InvariantCulture, out var runs) && runs > 0 ? runs : fallback;

    private static double Median(List<Dictionary<string, double>> runs, string metric) => Percentile(runs, metric, 0.5);

    /// <summary>Nearest-rank percentile: the smallest value with at least <paramref name="p"/> of the runs at or below it.</summary>
    internal static double Percentile(IEnumerable<double> values, double p)
    {
        var sorted = values.Order().ToArray();
        var rank = (int)Math.Ceiling(p * sorted.Length);
        return sorted[Math.Clamp(rank, 1, sorted.Length) - 1];
    }

    private static double Percentile(List<Dictionary<string, double>> runs, string metric, double p) =>
        Percentile(runs.Select(run => run[metric]), p);

    private static JsonObject Summarise(List<Dictionary<string, double>> runs, string[] metrics, Dictionary<string, double>? budgets)
    {
        var summary = new JsonObject();
        foreach (var metric in metrics)
        {
            var p90 = Percentile(runs, metric, 0.9);
            summary[metric] = new JsonObject
            {
                ["runs"] = new JsonArray([.. runs.Select(run => (JsonNode?)Math.Round(run[metric], 2))]),
                ["p50"] = Math.Round(Percentile(runs, metric, 0.5), 2),
                ["p90"] = Math.Round(p90, 2),
                ["max"] = Math.Round(runs.Max(run => run[metric]), 2),
                ["suggestedBudget"] = Math.Round(p90 * 1.2, 2),
                ["budget"] = budgets is null ? null : (JsonNode)budgets[metric],
            };
        }

        return new JsonObject
        {
            ["machine"] = Environment.MachineName,
            ["processors"] = Environment.ProcessorCount,
            ["measuredAtUtc"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["metrics"] = summary,
        };
    }

    private static void Write(JsonObject summary)
    {
        var json = summary.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        TestContext.Current.TestOutputHelper?.WriteLine(json);
        if (Environment.GetEnvironmentVariable(OutVariable) is { Length: > 0 } folder)
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "measurements.json"), json + "\n", new UTF8Encoding(false));
        }
    }

    /// <summary>One column of the budget table in <c>docs/budgets.md</c>; every startup and idle metric must have a number.</summary>
    internal static Dictionary<string, double> ReadBudgets(string column, string? path = null)
    {
        path ??= Path.Combine(RepositoryRoot(), "docs", "budgets.md");
        var index = column == "reference" ? 3 : 4;
        var budgets = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var line in File.ReadLines(path))
        {
            if (BudgetRow().Match(line) is not { Success: true } row)
            {
                continue;
            }

            var cells = line.Trim().Trim('|').Split('|').Select(cell => cell.Trim()).ToArray();
            if (cells.Length > index && double.TryParse(cells[index], NumberStyles.Float, CultureInfo.InvariantCulture, out var budget))
            {
                budgets[row.Groups[1].Value] = budget;
            }
        }

        var missing = StartupAndIdle.Where(metric => !budgets.ContainsKey(metric)).ToList();
        Assert.True(missing.Count == 0, $"docs/budgets.md has no {column} budget for: {string.Join(", ", missing)}.");
        return budgets;
    }

    private static string RepositoryRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SubZeroDev.WindowManager.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("No SubZeroDev.WindowManager.slnx above the test output.");
    }

    [GeneratedRegex(@"^\|\s*`(\w+)`\s*\|")]
    private static partial Regex BudgetRow();
}

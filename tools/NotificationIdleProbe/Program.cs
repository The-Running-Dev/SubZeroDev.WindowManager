using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Plugins.Notifications;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

internal static class NotificationIdleProbe
{
    private static async Task<int> Main(string[] args)
    {
        if (args is ["--self-test"])
        {
            await SelfTest();
            Console.WriteLine("PASS: idle polling, changed-history processing, content-free bounded diagnostics, strict budget reporting.");
            return 0;
        }

        if (args is ["--child", var childMode, var childPath])
        {
            await Measure(childMode, childPath);
            return 0;
        }

        if (args is not ["--measure", var output])
        {
            Console.Error.WriteLine("Usage: --self-test | --measure OUTPUT_DIRECTORY (3 fresh processes/mode, 2 s settle, 3 x 10 s windows)");
            return 2;
        }

        Directory.CreateDirectory(output);
        var failed = false;
        for (var run = 1; run <= 3; run++)
        {
            // Rotate order to avoid putting the same mode first in every comparison.
            string[] modes = ["control", "fake", "fake-traced", "worker", "worker-traced"];
            foreach (var mode in modes.Skip(run - 1).Concat(modes.Take(run - 1)))
            {
                var path = Path.GetFullPath(Path.Combine(output, $"{mode}-{run}.json"));
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false };
                if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
                {
                    start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
                }
                foreach (var arg in new[] { "--child", mode, path }) { start.ArgumentList.Add(arg); }
                using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start probe child.");
                await process.WaitForExitAsync();
                if (process.ExitCode != 0) { throw new InvalidOperationException($"Probe child failed: {process.ExitCode}"); }
                var report = JsonNode.Parse(File.ReadAllText(path))!;
                var passed = (bool)report["passed"]!;
                failed |= !passed;
                Console.WriteLine($"{mode} run {run}: {string.Join(", ", report["windows"]!.AsArray().Select(w => $"{(double)w!["percent"]!:F3}%"))} ({(passed ? "PASS" : "FAIL")})");
            }
        }

        // All children run and all reports survive even if an earlier measurement fails.
        return failed ? 1 : 0;
    }

    private static async Task Measure(string mode, string path)
    {
        using var root = new Scratch();
        File.WriteAllText(Path.Combine(root.Path, "notifications.json"), """{"items":[{"id":"1","title":"sentinel-text"}]}""");
        using var process = Process.GetCurrentProcess();
        var startup = Stopwatch.StartNew();
        var initialCpu = process.TotalProcessorTime;
        IdleDiagnostics? diagnostics = mode == "fake-traced" ? new() : null;
        using var history = mode is "fake" or "fake-traced" ? new FakeHistory(root.Path, diagnostics) : null;
        await using var connection = new ProbeHost(root.Path);
        NotificationsPlugin? plugin = null;
        if (mode is "worker" or "worker-traced")
        {
            Environment.SetEnvironmentVariable(NotificationsPlugin.HookVariable, root.Path);
            Environment.SetEnvironmentVariable(IdleDiagnostics.Variable, mode == "worker-traced" ? "1" : null);
            plugin = connection.CreatePlugin<NotificationsPlugin>();
            await connection.StartAsync(plugin);
            await Until(() => plugin.Current.Ready);
        }
        else if (history is not null) { await history.ReadAsync(CancellationToken.None); }
        await Task.Delay(TimeSpan.FromSeconds(2));
        process.Refresh();
        var startupReport = new { elapsedMs = startup.Elapsed.TotalMilliseconds, cpuMs = (process.TotalProcessorTime - initialCpu).TotalMilliseconds };
        var windows = new List<object>();
        var passed = true;
        for (var window = 0; window < 3; window++)
        {
            process.Refresh();
            var before = process.TotalProcessorTime;
            var started = Stopwatch.GetTimestamp();
            await Task.Delay(TimeSpan.FromSeconds(10));
            process.Refresh();
            var ended = Stopwatch.GetTimestamp();
            var cpuMs = (process.TotalProcessorTime - before).TotalMilliseconds;
            var elapsedMs = Stopwatch.GetElapsedTime(started, ended).TotalMilliseconds;
            var percent = cpuMs / elapsedMs * 100;
            passed &= percent < 2;
            windows.Add(new { started, ended, elapsedMs, cpuMs, percent, passed = percent < 2 });
        }
        if (plugin is not null) { await connection.StopAsync(plugin); }
        diagnostics?.Write(Path.Combine(root.Path, "notification-diagnostics.json"));
        var trace = Path.Combine(root.Path, "notification-diagnostics.json");
        File.WriteAllText(path, JsonSerializer.Serialize(new
        {
            scope = "portable simulation; no Windows host, IPC reader, native UI, or production provider",
            mode, runtime = System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription,
            os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
            logicalProcessors = Environment.ProcessorCount,
            startup = startupReport, windows, passed,
            activity = File.Exists(trace) ? JsonNode.Parse(File.ReadAllText(trace)) : null,
        }, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static async Task SelfTest()
    {
        using var root = new Scratch();
        var path = Path.Combine(root.Path, "notifications.json");
        File.WriteAllText(path, """{"items":[{"id":"1","title":"secret-title","body":"secret-body"}]}""");
        Environment.SetEnvironmentVariable(NotificationsPlugin.HookVariable, root.Path);
        Environment.SetEnvironmentVariable(IdleDiagnostics.Variable, "1");
        await using var connection = new ProbeHost(root.Path);
        var plugin = connection.CreatePlugin<NotificationsPlugin>();
        await connection.StartAsync(plugin);
        await Until(() => plugin.Current.Ready);
        await Task.Delay(550);
        // Disable pop-ups to keep this processing regression independent of native surfaces and host replies.
        plugin.ApplyBellSettings(new JsonObject { ["popups"] = false });
        File.WriteAllText(path, """{"items":[{"id":"1"},{"id":"2"}]}""");
        await Until(() => plugin.Current.Unread == 2);
        await Task.Delay(350);
        await connection.StopAsync(plugin);
        var trace = File.ReadAllText(Path.Combine(root.Path, "notification-diagnostics.json"));
        Check(!trace.Contains("secret-", StringComparison.Ordinal), "Diagnostics leaked notification content.");
        var events = JsonNode.Parse(trace)!["activities"]!.AsArray();
        Check(events.Count(e => (string?)e!["Kind"] == "worker.start") == 1, "Missing startup activity.");
        Check(events.Count(e => (string?)e!["Kind"] == "fake.poll") >= 5, "The fake polling path was not observed.");
        Check(events.Count(e => (string?)e!["Kind"] == "worker.refresh") == 2, "An unchanged fake caused refreshes, or a change was missed.");
        Check(events.Count(e => (string?)e!["Kind"] == "fake.read") == 2, "Polling reparsed unchanged history.");
        var bounded = new IdleDiagnostics();
        for (var i = 0; i < 8200; i++) { bounded.Record("test", Stopwatch.GetTimestamp()); }
        bounded.Write(Path.Combine(root.Path, "bounded.json"));
        var capped = JsonNode.Parse(File.ReadAllText(Path.Combine(root.Path, "bounded.json")))!;
        Check(capped["activities"]!.AsArray().Count == 8192 && (int)capped["dropped"]! == 8, "The trace is not bounded.");
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) { throw new InvalidOperationException(message); }
    }

    private static async Task Until(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) { await Task.Delay(10, timeout.Token); }
    }

    private sealed class Scratch : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), $"notification-idle-{Guid.NewGuid():N}");
        public Scratch() => Directory.CreateDirectory(Path);
        public void Dispose() => Directory.Delete(Path, recursive: true);
    }
}

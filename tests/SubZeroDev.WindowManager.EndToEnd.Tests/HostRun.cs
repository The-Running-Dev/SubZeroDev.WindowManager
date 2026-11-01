using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace SubZeroDev.WindowManager.EndToEnd.Tests;

/// <summary>
/// One run of the guardian or the host against a scratch data root, observed through the host's
/// test hook (<c>SUBZERODEV_WM_TEST_HOOK</c>): it writes <c>status.json</c> and exits when an
/// <c>exit</c> file appears.
/// </summary>
internal sealed class HostRun : IDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(30);

    private readonly DateTime _created = DateTime.Now;

    private HostRun(string root)
    {
        Root = root;
        DataRoot = Path.Combine(root, "data");
        HookDirectory = Path.Combine(root, "hook");
        Instance = "." + Path.GetFileName(root);
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(HookDirectory);
    }

    public string Root { get; }

    public string DataRoot { get; }

    public string HookDirectory { get; }

    public string Instance { get; }

    public Process? Process { get; private set; }

    public static string HostExe => Metadata("HostExe");

    public static string GuardianExe => Metadata("GuardianExe");

    public static HostRun Create() =>
        new(Path.Combine(Path.GetTempPath(), "SubZeroDev.WindowManager.EndToEnd", Guid.NewGuid().ToString("N")));

    public const string HostProcessName = "SubZeroDev.WindowManager.Host";

    public const string GuardianProcessName = "SubZeroDev.WindowManager";

    /// <summary>This session's recovery journal under the scratch data root.</summary>
    public string JournalPath
    {
        get
        {
            using var self = System.Diagnostics.Process.GetCurrentProcess();
            return Path.Combine(DataRoot, "recovery", $"session-{self.SessionId}.json");
        }
    }

    /// <summary>The journal as JSON, or null when there is none (or it is being replaced).</summary>
    public JsonObject? ReadJournal() => ReadJson(JournalPath);

    /// <summary>What the guardian last published under the test hook, or null.</summary>
    public JsonObject? ReadGuardian() => ReadJson(Path.Combine(HookDirectory, "guardian.json"));

    public string[] CrashRecords()
    {
        var directory = Path.Combine(DataRoot, "logs", "crash");
        return Directory.Exists(directory) ? [.. Directory.EnumerateFiles(directory, "*.json").Order()] : [];
    }

    /// <summary>
    /// Skips the test when there is no interactive desktop to place windows and app bars on. Under CI
    /// (the <c>CI</c> variable is set) a missing desktop fails instead, so a skip never reads as green.
    /// </summary>
    public static void RequireDesktop()
    {
        if (Interactive.HasDesktop())
        {
            return;
        }

        const string reason = "No interactive desktop: session 0 or no input desktop.";
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CI")))
        {
            Assert.Fail(reason + " CI must run the end-to-end tests on a desktop.");
        }

        Assert.Skip(reason);
    }

    /// <summary>Host processes that started at or after <paramref name="since"/>.</summary>
    public static int[] HostsStartedSince(DateTime since)
    {
        var ids = new List<int>();
        foreach (var process in System.Diagnostics.Process.GetProcessesByName(HostProcessName))
        {
            using (process)
            {
                try
                {
                    if (process.StartTime >= since)
                    {
                        ids.Add(process.Id);
                    }
                }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                {
                }
            }
        }

        return [.. ids];
    }

    /// <summary>An HKCU key the host reads the app mode from instead of the Windows one, when set.</summary>
    public string? AppModeKey { get; set; }

    public Process Start(string exe, params string[] args)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        start.Environment["SUBZERODEV_WM_DATA_ROOT"] = DataRoot;
        start.Environment["SUBZERODEV_WM_TEST_HOOK"] = HookDirectory;
        start.Environment["SUBZERODEV_WM_INSTANCE"] = Instance;
        start.Environment["SUBZERODEV_WM_HOST_EXE"] = HostExe;
        if (AppModeKey is not null)
        {
            start.Environment["SUBZERODEV_WM_APPMODE_KEY"] = AppModeKey;
        }

        Process = System.Diagnostics.Process.Start(start) ?? throw new InvalidOperationException($"{exe} did not start.");
        return Process;
    }

    public async Task<JsonObject> WaitForStatusAsync()
    {
        var path = Path.Combine(HookDirectory, "status.json");
        var deadline = DateTime.UtcNow + StartTimeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Process is { HasExited: true })
            {
                throw new InvalidOperationException(
                    $"The process exited with {Process.ExitCode} before reporting status. Host log:{Environment.NewLine}{HostLog()}");
            }

            if (File.Exists(path))
            {
                try
                {
                    var status = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                    if ((bool?)status["ready"] == true)
                    {
                        return status;
                    }
                }
                catch (IOException)
                {
                }
            }

            await Task.Delay(100);
        }

        throw new TimeoutException($"No status.json in {HookDirectory} after {StartTimeout}.");
    }

    /// <summary>Polls <c>status.json</c> until <paramref name="condition"/> holds; the last status is in the timeout message.</summary>
    public async Task<JsonObject> WaitForAsync(Func<JsonObject, bool> condition, TimeSpan timeout)
    {
        var path = Path.Combine(HookDirectory, "status.json");
        var deadline = DateTime.UtcNow + timeout;
        JsonObject? last = null;
        while (DateTime.UtcNow < deadline)
        {
            try
            {
                last = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
                if (condition(last))
                {
                    return last;
                }
            }
            catch (Exception e) when (e is IOException or System.Text.Json.JsonException)
            {
            }

            await Task.Delay(50);
        }

        throw new TimeoutException($"Condition not met within {timeout}. Last status: {last?.ToJsonString()}");
    }

    /// <summary>Texts shown by each contribution of <paramref name="plugin"/>, one list per instance.</summary>
    public static List<string[]> Texts(JsonObject status, string plugin) =>
        status["contributions"]!.AsArray()
            .Where(c => (string?)c!["plugin"] == plugin && c["reason"] is null)
            .Select(c => c!["texts"]!.AsArray().Select(t => (string)t!).ToArray())
            .ToList();

    /// <summary>
    /// Copies a built plugin folder into <c>&lt;data&gt;\plugins\&lt;id&gt;</c>, setting the manifest id
    /// (and the entry arguments, when given).
    /// </summary>
    public string CopyPlugin(string sourceDirectory, string id, string[]? entryArgs = null)
    {
        var target = Path.Combine(DataRoot, "plugins", id);
        foreach (var file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
        {
            if (Path.GetFileName(file) == "plugin.json")
            {
                continue;
            }

            var destination = Path.Combine(target, Path.GetRelativePath(sourceDirectory, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination);
        }

        var manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(sourceDirectory, "plugin.json")))!.AsObject();
        manifest["id"] = id;
        if (entryArgs is not null)
        {
            manifest["entry"]!["args"] = new JsonArray(entryArgs.Select(a => (JsonNode?)a).ToArray());
        }

        File.WriteAllText(Path.Combine(target, "plugin.json"), manifest.ToJsonString());
        return target;
    }

    /// <summary>Writes <c>&lt;data&gt;\layouts\subzerodev.default.json</c>: one bottom panel whose end zone holds these instances.</summary>
    public string WriteDefaultLayout(params (Guid Id, string Plugin, string Contribution)[] instances)
    {
        var layout = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["id"] = "subzerodev.default",
            ["displayName"] = "Test layout",
            ["panels"] = new JsonArray(new JsonObject
            {
                ["id"] = "6f0d3c1e-5a3b-4f0e-9c1d-2b7a8e4f5a01",
                ["monitor"] = "primary",
                ["placement"] = new JsonObject { ["edge"] = "bottom" },
                ["thickness"] = 40,
                ["behaviour"] = "reserve",
                ["zones"] = new JsonObject
                {
                    ["end"] = new JsonArray(instances.Select(i => (JsonNode?)new JsonObject
                    {
                        ["id"] = i.Id.ToString("D"),
                        ["plugin"] = i.Plugin,
                        ["contribution"] = i.Contribution,
                    }).ToArray()),
                },
            }),
        };
        return WriteDataFile(Path.Combine("layouts", "subzerodev.default.json"), layout.ToJsonString());
    }

    public static string ContentDirectory => Path.Combine(Path.GetDirectoryName(HostExe)!, "content");

    public static string TestPluginDirectory => Metadata("TestPluginDir");

    public async Task<int> StopAsync()
    {
        await File.WriteAllTextAsync(Path.Combine(HookDirectory, "exit"), string.Empty);
        using var cancel = new CancellationTokenSource(StartTimeout);
        await Process!.WaitForExitAsync(cancel.Token);
        return Process.ExitCode;
    }

    public string WriteDataFile(string relativePath, string content)
    {
        var path = Path.Combine(DataRoot, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        return path;
    }

    /// <summary>Copies the built-in default theme beside the host into the data root.</summary>
    public string CopyBuiltInTheme()
    {
        var source = Path.Combine(Path.GetDirectoryName(HostExe)!, "content", "themes", "subzerodev.default");
        var target = Path.Combine(DataRoot, "themes", "subzerodev.default");
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        }

        return target;
    }

    public void Dispose()
    {
        if (Process is { HasExited: false })
        {
            Process.Kill(entireProcessTree: true);
            Process.WaitForExit();
        }

        Process?.Dispose();

        // Hosts restarted by a guardian, and guardians relaunched by a host, are not children of the first process.
        foreach (var name in new[] { GuardianProcessName, HostProcessName })
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName(name))
            {
                using (process)
                {
                    try
                    {
                        if (process.StartTime >= _created)
                        {
                            process.Kill(entireProcessTree: true);
                            process.WaitForExit(5000);
                        }
                    }
                    catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
                    {
                    }
                }
            }
        }

        try
        {
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

            Directory.Delete(Root, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static JsonObject? ReadJson(string path)
    {
        try
        {
            return File.Exists(path) ? JsonNode.Parse(File.ReadAllText(path))?.AsObject() : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            return null;
        }
    }

    /// <summary>The host's own log so far, so a failure on a machine without the data root still shows why.</summary>
    public string HostLog(string pattern = "host-*.log")
    {
        var logs = Path.Combine(DataRoot, "logs");
        if (!Directory.Exists(logs))
        {
            return "(none)";
        }

        return string.Join(Environment.NewLine, Directory.EnumerateFiles(logs, pattern).Select(path =>
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }));
    }

    private static string Metadata(string key) =>
        typeof(HostRun).Assembly.GetCustomAttributes<AssemblyMetadataAttribute>().Single(a => a.Key == key).Value!;
}

internal static partial class Interactive
{
    private static readonly nint PerMonitorAwareV2 = -4;

    public static bool HasDesktop()
    {
        if (!ProcessIdToSessionId((uint)Environment.ProcessId, out var session) || session == 0)
        {
            return false;
        }

        var desktop = OpenInputDesktop(0, false, 0x0001);
        if (desktop == 0)
        {
            return false;
        }

        CloseDesktop(desktop);
        return true;
    }

    /// <summary>The primary monitor's work area in physical pixels.</summary>
    public static (int Left, int Top, int Right, int Bottom) PrimaryWorkArea()
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            var monitor = MonitorFromPoint(0, 1);
            var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
            if (!GetMonitorInfoW(monitor, ref info))
            {
                throw new InvalidOperationException("GetMonitorInfo failed.");
            }

            return (info.Work.Left, info.Work.Top, info.Work.Right, info.Work.Bottom);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>The window's rectangle in physical pixels.</summary>
    public static (int Left, int Top, int Right, int Bottom) WindowRect(nint hwnd)
    {
        var previous = SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        try
        {
            if (!GetWindowRect(hwnd, out var rect))
            {
                throw new InvalidOperationException("GetWindowRect failed.");
            }

            return (rect.Left, rect.Top, rect.Right, rect.Bottom);
        }
        finally
        {
            SetThreadDpiAwarenessContext(previous);
        }
    }

    /// <summary>Asks the window to close, as Alt+F4 or another process would.</summary>
    public static void RequestClose(nint hwnd)
    {
        const uint WmClose = 0x0010;
        if (!PostMessageW(hwnd, WmClose, 0, 0))
        {
            throw new InvalidOperationException("PostMessage failed.");
        }
    }

    public static bool IsAlive(nint hwnd) => IsWindow(hwnd);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ProcessIdToSessionId(uint processId, out uint sessionId);

    [LibraryImport("user32.dll")]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(long point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint hwnd);
}

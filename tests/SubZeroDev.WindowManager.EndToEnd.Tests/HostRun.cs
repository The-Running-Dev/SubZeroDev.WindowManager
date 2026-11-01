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

    public Process Start(string exe)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.Environment["SUBZERODEV_WM_DATA_ROOT"] = DataRoot;
        start.Environment["SUBZERODEV_WM_TEST_HOOK"] = HookDirectory;
        start.Environment["SUBZERODEV_WM_INSTANCE"] = Instance;
        start.Environment["SUBZERODEV_WM_HOST_EXE"] = HostExe;
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
                throw new InvalidOperationException($"The process exited with {Process.ExitCode} before reporting status.");
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
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
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

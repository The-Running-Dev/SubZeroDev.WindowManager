using System.Diagnostics;

namespace SubZeroDev.WindowManager.Guardian;

/// <summary>
/// The process the user starts. S1 runs one host and reports how it ended; restart policy and the
/// guardian pipe's listener arrive with the recovery slice, so the pipe name is generated but unused.
/// </summary>
public static class Program
{
    public const int Normal = 0;
    public const int AlreadyRunning = 2;
    public const int HostFailed = 3;
    public const string HostExeVariable = "SUBZERODEV_WM_HOST_EXE";
    public const string InstanceVariable = "SUBZERODEV_WM_INSTANCE";
    public const string HostExeName = "SubZeroDev.WindowManager.Host.exe";

    public static int Main()
    {
        var suffix = Environment.GetEnvironmentVariable(InstanceVariable) ?? string.Empty;
        using var mutex = new Mutex(initiallyOwned: true, @"Local\SubZeroDev.WindowManager.Guardian" + suffix, out var created);
        if (!created)
        {
            return AlreadyRunning;
        }

        var hostExe = FindHost();
        if (hostExe is null)
        {
            return HostFailed;
        }

        var start = new ProcessStartInfo(hostExe) { UseShellExecute = false };
        start.ArgumentList.Add("--guardian-pid");
        start.ArgumentList.Add(Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture));
        start.ArgumentList.Add("--guardian-pipe");
        start.ArgumentList.Add($"SubZeroDev.WindowManager.Guardian.{Guid.NewGuid():N}");

        Process? started;
        try
        {
            started = Process.Start(start);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return HostFailed;
        }

        using var host = started;
        if (host is null)
        {
            return HostFailed;
        }

        host.WaitForExit();
        return host.ExitCode == Normal ? Normal : HostFailed;
    }

    private static string? FindHost()
    {
        var configured = Environment.GetEnvironmentVariable(HostExeVariable);
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return File.Exists(configured) ? configured : null;
        }

        string[] candidates =
        [
            Path.Combine(AppContext.BaseDirectory, "host", HostExeName),
            Path.Combine(AppContext.BaseDirectory, HostExeName),
        ];
        return candidates.FirstOrDefault(File.Exists);
    }
}

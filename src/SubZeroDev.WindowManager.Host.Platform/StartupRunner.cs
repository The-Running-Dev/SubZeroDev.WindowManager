using System.Diagnostics;
using System.Security;
using System.Text;
using Microsoft.Win32;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>Where the start-up programs are read from: the registry root and the two Startup folders.</summary>
/// <param name="RegistryRoot">An <c>HKCU</c> subkey whose <c>HKCU</c> and <c>HKLM</c> subkeys stand in for the hives, or null for the real hives.</param>
public sealed record StartupSources(string? RegistryRoot, string? CommonStartupFolder, string? UserStartupFolder)
{
    public const string RegistryRootVariable = "SUBZERODEV_WM_REGISTRY_ROOT";
    public const string CommonStartupVariable = "SUBZERODEV_WM_COMMON_STARTUP_DIR";
    public const string UserStartupVariable = "SUBZERODEV_WM_USER_STARTUP_DIR";
    private const string HookRoot = @"Software\SubZeroDev.WindowManager.Tests\hook";

    /// <summary>
    /// The real hives and folders, or under the test hook the configured test root and folders, falling back to
    /// a fixed test key and no folders, so a test never reaches the real start-up programs.
    /// </summary>
    public static StartupSources FromEnvironment(string? hookDirectory)
    {
        var root = Configured(RegistryRootVariable);
        var common = Configured(CommonStartupVariable);
        var user = Configured(UserStartupVariable);
        return hookDirectory is null
            ? new StartupSources(root, common ?? Environment.GetFolderPath(Environment.SpecialFolder.CommonStartup),
                user ?? Environment.GetFolderPath(Environment.SpecialFolder.Startup))
            : new StartupSources(root ?? HookRoot, common, user);
    }

    private static string? Configured(string name) =>
        Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;
}

/// <summary>
/// Runs the session's start-up programs in Explorer's order: HKLM <c>RunOnce</c>, <c>RunOnceEx</c>, HKLM
/// <c>Run</c>, HKCU <c>Run</c>, the common Startup folder, the user's Startup folder, then HKCU <c>RunOnce</c>.
/// A <c>RunOnce</c> value is deleted before it runs, or after a successful start when its name begins with
/// <c>!</c>; one that cannot be deleted is left alone and logged, so it can never run every session. Sources
/// switched off by an <c>Explorer</c> policy, and entries disabled in Task Manager (<c>StartupApproved</c>), are
/// skipped. A volatile registry key marks the pass, so a host restarted in the same session runs nothing (I24).
/// </summary>
public sealed class StartupRunner(StartupSources sources, Func<string, string, bool> start, Action<string> log) : IStartupRunner
{
    public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    public const string RunOnceKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnce";
    public const string RunOnceExKey = @"Software\Microsoft\Windows\CurrentVersion\RunOnceEx";
    public const string Run32Key = @"Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Run";
    public const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\Explorer";
    public const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved";
    public const string MarkerKey = @"Software\SubZeroDev\WindowManager\StartupRan";

    private enum Hive { User, Machine }

    /// <summary>Starts a command through the shell, as Explorer does; under the test hook records it instead.</summary>
    public static Func<string, string, bool> Launcher(string? hookDirectory, Action<string> log) => (file, arguments) =>
    {
        if (hookDirectory is not null)
        {
            Directory.CreateDirectory(hookDirectory);
            File.AppendAllText(Path.Combine(hookDirectory, Recovery.Reverter.DesktopActionsLog),
                (arguments.Length == 0 ? $"start {file}" : $"start {file} {arguments}") + "\n", new UTF8Encoding(false));
            return true;
        }

        try
        {
            var info = new ProcessStartInfo(file, arguments) { UseShellExecute = true };
            if (Path.GetDirectoryName(file) is { Length: > 0 } folder && Directory.Exists(folder))
            {
                info.WorkingDirectory = folder;
            }

            using var process = Process.Start(info);
            return true;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException or FileNotFoundException)
        {
            log($"Could not start {file}: {e.Message}");
            return false;
        }
    };

    public StartupReport Run()
    {
        var started = new List<string>();
        var skipped = new List<string>();
        if (!Mark())
        {
            log("Start-up programs already ran in this session; nothing was started.");
            return new StartupReport(true, started, skipped);
        }

        RunOnce(Hive.Machine, "DisableLocalMachineRunOnce", started, skipped);
        RunOnceEx(started, skipped);
        if (Allowed(Hive.Machine, "DisableLocalMachineRun", skipped))
        {
            RunValues(Hive.Machine, RunKey, "Run", started, skipped);
            RunValues(Hive.Machine, Run32Key, "Run32", started, skipped);
        }

        if (Allowed(Hive.User, "DisableCurrentUserRun", skipped))
        {
            RunValues(Hive.User, RunKey, "Run", started, skipped);
        }

        RunFolder(Hive.Machine, sources.CommonStartupFolder, started, skipped);
        RunFolder(Hive.User, sources.UserStartupFolder, started, skipped);
        RunOnce(Hive.User, "DisableCurrentUserRunOnce", started, skipped);
        log($"Start-up programs: {started.Count} started, {skipped.Count} skipped.");
        return new StartupReport(false, started, skipped);
    }

    /// <summary>Creates the session's marker; false when it was already there.</summary>
    private bool Mark()
    {
        var path = Rooted(Hive.User, MarkerKey);
        using (var existing = Registry.CurrentUser.OpenSubKey(path))
        {
            if (existing is not null)
            {
                return false;
            }
        }

        // A volatile key is gone at sign-out, so the next session runs the programs again.
        using var created = Registry.CurrentUser.CreateSubKey(path, writable: true, RegistryOptions.Volatile);
        return true;
    }

    private bool Allowed(Hive source, string policy, List<string> skipped)
    {
        foreach (var hive in new[] { Hive.Machine, Hive.User })
        {
            using var key = Open(hive, PolicyKey);
            if (key?.GetValue(policy) is int value && value != 0)
            {
                log($"{Name(source)} start-up source skipped: the {policy} policy is set.");
                skipped.Add(policy);
                return false;
            }
        }

        return true;
    }

    private void RunOnce(Hive hive, string policy, List<string> started, List<string> skipped)
    {
        if (!Allowed(hive, policy, skipped))
        {
            return;
        }

        var label = $"{Name(hive)}\\RunOnce";
        using var readable = Open(hive, RunOnceKey);
        if (readable is null)
        {
            return;
        }

        using var writable = OpenWritable(hive, RunOnceKey);
        foreach (var name in Sorted(readable.GetValueNames()))
        {
            if (readable.GetValue(name) is not string command)
            {
                continue;
            }

            var afterStart = name.StartsWith('!');
            // Without write access even a '!' value could never be deleted, and would run every session.
            if (writable is null || (!afterStart && !Delete(writable, name)))
            {
                log($"{label} value '{name}' could not be deleted, so it was not run.");
                skipped.Add($"{label}\\{name}");
                continue;
            }

            if (Launch(command, $"{label}\\{name}", started) && afterStart && !Delete(writable, name))
            {
                log($"{label} value '{name}' ran but could not be deleted.");
            }
        }
    }

    /// <summary>Each <c>RunOnceEx</c> subkey in name order, deleted before its commands run.</summary>
    private void RunOnceEx(List<string> started, List<string> skipped)
    {
        if (!Allowed(Hive.Machine, "DisableLocalMachineRunOnce", skipped))
        {
            return;
        }

        using var parent = Open(Hive.Machine, RunOnceExKey);
        if (parent is null)
        {
            return;
        }

        using var writableParent = OpenWritable(Hive.Machine, RunOnceExKey);
        foreach (var section in Sorted(parent.GetSubKeyNames()))
        {
            var commands = new List<(string Name, string Command)>();
            using (var key = parent.OpenSubKey(section))
            {
                foreach (var name in Sorted(key?.GetValueNames() ?? []).Where(name => name.Length > 0))
                {
                    if (key!.GetValue(name) is string command)
                    {
                        commands.Add((name, command));
                    }
                }
            }

            try
            {
                if (writableParent is null)
                {
                    throw new UnauthorizedAccessException();
                }

                writableParent.DeleteSubKeyTree(section, throwOnMissingSubKey: false);
            }
            catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
            {
                log($"HKLM\\RunOnceEx section '{section}' could not be deleted, so it was not run.");
                skipped.Add($"HKLM\\RunOnceEx\\{section}");
                continue;
            }

            foreach (var (name, command) in commands)
            {
                Launch(command, $"HKLM\\RunOnceEx\\{section}\\{name}", started);
            }
        }
    }

    private void RunValues(Hive hive, string path, string approval, List<string> started, List<string> skipped)
    {
        using var key = Open(hive, path);
        if (key is null)
        {
            return;
        }

        var label = $"{Name(hive)}\\{approval}";
        foreach (var name in Sorted(key.GetValueNames()).Where(name => name.Length > 0))
        {
            if (key.GetValue(name) is not string command)
            {
                continue;
            }

            if (Disabled(hive, approval, name))
            {
                skipped.Add($"{label}\\{name}");
                continue;
            }

            Launch(command, $"{label}\\{name}", started);
        }
    }

    private void RunFolder(Hive hive, string? folder, List<string> started, List<string> skipped)
    {
        if (folder is null || !Directory.Exists(folder))
        {
            return;
        }

        var label = hive == Hive.Machine ? "Common Startup" : "Startup";
        foreach (var file in Directory.EnumerateFiles(folder).Order(StringComparer.OrdinalIgnoreCase))
        {
            var name = Path.GetFileName(file);
            if (name.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase)
                || File.GetAttributes(file).HasFlag(FileAttributes.Hidden))
            {
                continue;
            }

            if (Disabled(hive, "StartupFolder", name))
            {
                skipped.Add($"{label}\\{name}");
                continue;
            }

            if (start(file, string.Empty))
            {
                started.Add($"{label}\\{name}");
            }
        }
    }

    /// <summary>Task Manager's switch: an odd first byte of the <c>StartupApproved</c> value means disabled.</summary>
    private bool Disabled(Hive hive, string approval, string name)
    {
        using var key = Open(hive, $"{ApprovedKey}\\{approval}");
        return key?.GetValue(name) is byte[] { Length: > 0 } bytes && (bytes[0] & 1) == 1;
    }

    private bool Launch(string command, string label, List<string> started)
    {
        var (file, arguments) = Split(Environment.ExpandEnvironmentVariables(command));
        if (file.Length == 0)
        {
            return false;
        }

        if (start(file, arguments))
        {
            started.Add(label);
            return true;
        }

        log($"{label} did not start.");
        return false;
    }

    /// <summary>
    /// Splits a <c>Run</c> command into the file and its arguments: a quoted path, else the longest leading run of
    /// words that names an existing file (with or without <c>.exe</c>), else the first word.
    /// </summary>
    public static (string File, string Arguments) Split(string command)
    {
        var text = command.Trim();
        if (text.StartsWith('"'))
        {
            var close = text.IndexOf('"', 1);
            return close < 0 ? (text[1..], string.Empty) : (text[1..close], text[(close + 1)..].Trim());
        }

        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        for (var count = words.Length; count > 1; count--)
        {
            var candidate = string.Join(' ', words.Take(count));
            if (File.Exists(candidate) || File.Exists(candidate + ".exe"))
            {
                return (candidate, string.Join(' ', words.Skip(count)));
            }
        }

        return words.Length == 0 ? (string.Empty, string.Empty) : (words[0], string.Join(' ', words.Skip(1)));
    }

    private static IEnumerable<string> Sorted(IEnumerable<string> names) => names.Order(StringComparer.OrdinalIgnoreCase);

    private static bool Delete(RegistryKey? key, string name)
    {
        if (key is null)
        {
            return false;
        }

        try
        {
            key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or SecurityException or IOException)
        {
            return false;
        }
    }

    private static string Name(Hive hive) => hive == Hive.Machine ? "HKLM" : "HKCU";

    private RegistryKey? Open(Hive hive, string path)
    {
        try
        {
            return sources.RegistryRoot is not null || hive == Hive.User
                ? Registry.CurrentUser.OpenSubKey(Rooted(hive, path))
                : Registry.LocalMachine.OpenSubKey(path);
        }
        catch (SecurityException)
        {
            return null;
        }
    }

    private RegistryKey? OpenWritable(Hive hive, string path)
    {
        try
        {
            return sources.RegistryRoot is not null || hive == Hive.User
                ? Registry.CurrentUser.OpenSubKey(Rooted(hive, path), writable: true)
                : Registry.LocalMachine.OpenSubKey(path, writable: true);
        }
        catch (Exception e) when (e is SecurityException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string Rooted(Hive hive, string path) =>
        sources.RegistryRoot is null ? path : Path.Combine(sources.RegistryRoot, hive == Hive.User ? "HKCU" : "HKLM", path);
}

using System.Globalization;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SubZeroDev.WindowManager.Guardian;

/// <summary>
/// <c>--shell on|off|status</c>: makes the guardian the user's shell through the per-user <c>Winlogon\Shell</c>
/// value, and puts back exactly what was there (I22). The previous value, or its absence, is recorded in
/// <c>shell.json</c> before anything is written. A Custom User Interface policy or a Shell Launcher configuration
/// overrides the per-user value, so with either present nothing is written. Nothing here needs elevation.
/// </summary>
/// <remarks>
/// Tests set <see cref="RegistryRootVariable"/> to an <c>HKCU</c> subkey that stands in for both hives: its
/// <c>HKCU</c> and <c>HKLM</c> subkeys hold the keys the real hives would. Under the test hook without a root,
/// a fixed test key is used, so a test can never reach the real values.
/// </remarks>
internal sealed class ShellRegistration(string dataRoot, Action<string> log)
{
    public const string RegistryRootVariable = "SUBZERODEV_WM_REGISTRY_ROOT";
    public const string WinlogonKey = @"Software\Microsoft\Windows NT\CurrentVersion\Winlogon";
    public const string PolicyKey = @"Software\Microsoft\Windows\CurrentVersion\Policies\System";
    public const string ShellLauncherKey = @"SOFTWARE\Microsoft\Windows Embedded\Shell Launcher";
    public const string ShellValue = "Shell";
    public const string ExplorerValue = "explorer.exe";
    private const string HookRoot = @"Software\SubZeroDev.WindowManager.Tests\hook";
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    private readonly string? _root = Configured(RegistryRootVariable)
        ?? (Configured(Program.TestHookVariable) is null ? null : HookRoot);

    public string StatePath => Path.Combine(dataRoot, "shell.json");

    /// <summary>The command line Windows runs at sign-in.</summary>
    public static string Command(string exePath) => $"\"{exePath}\" --mode replacement";

    /// <summary>Registers <paramref name="exePath"/> as the shell; exit 5 when a policy or Shell Launcher overrides it.</summary>
    public int On(string exePath)
    {
        if (Override() is { } overriding)
        {
            log($"Shell registration is overridden by {overriding}; nothing was changed.");
            Console.Error.WriteLine($"Shell registration is overridden by {overriding}. Remove it, or ask your administrator to, then try again.");
            return Program.ShellOverridden;
        }

        using var winlogon = UserKey(WinlogonKey);
        var command = Command(exePath);
        var state = ReadState();
        if (state is not { Registered: true })
        {
            var current = winlogon.GetValue(ShellValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;

            // A value of ours with no record of what it replaced (the data folder was cleared) restores to absence.
            var previous = current is null || IsOurs(current) ? new Previous(false, null) : new Previous(true, current);
            WriteState(new State(true, previous, DateTimeOffset.UtcNow));
            log(previous.Present
                ? $"Recorded the previous shell value: {previous.Value}"
                : "Recorded that there was no per-user shell value.");
        }

        winlogon.SetValue(ShellValue, command, RegistryValueKind.String);
        log($"Registered as the shell: {command}");
        return Program.Normal;
    }

    /// <summary>Restores what <see cref="On"/> recorded; with no record, only a value of ours is removed.</summary>
    public int Off()
    {
        using var winlogon = UserKey(WinlogonKey);
        var state = ReadState();
        if (state is not { Registered: true })
        {
            if (winlogon.GetValue(ShellValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) is string current && IsOurs(current))
            {
                winlogon.DeleteValue(ShellValue, throwOnMissingValue: false);
                log("Removed a shell value of ours that had no record.");
            }

            return Program.Normal;
        }

        if (state.Previous is { Present: true, Value: { } value })
        {
            winlogon.SetValue(ShellValue, value, RegistryValueKind.String);
            log($"Restored the previous shell value: {value}");
        }
        else
        {
            winlogon.DeleteValue(ShellValue, throwOnMissingValue: false);
            log("Removed the per-user shell value; there was none before.");
        }

        WriteState(state with { Registered = false, RegisteredAt = null });
        return Program.Normal;
    }

    /// <summary>Writes the registration, the current value and any override to standard output as JSON.</summary>
    public int Status()
    {
        using var winlogon = Open(Hive.User, WinlogonKey);
        var state = ReadState();
        var status = new JsonObject
        {
            ["registered"] = state?.Registered ?? false,
            ["value"] = winlogon?.GetValue(ShellValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string,
            ["override"] = Override(),
            ["state"] = StatePath,
        };
        Console.Out.WriteLine(status.ToJsonString());
        return Program.Normal;
    }

    /// <summary>
    /// Explorer takes the shell role only when the shell value names it, so while ours is registered the value
    /// names Explorer until <paramref name="start"/> returns, and then ours goes back. A crash in between leaves
    /// Explorer registered, which only means the next sign-in gets Explorer.
    /// </summary>
    public async Task WhileExplorerStartsAsync(Func<Task> start)
    {
        using var winlogon = UserKey(WinlogonKey);
        var current = winlogon.GetValue(ShellValue, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        if (current is null || !IsOurs(current))
        {
            await start();
            return;
        }

        winlogon.SetValue(ShellValue, ExplorerValue, RegistryValueKind.String);
        try
        {
            await start();
        }
        finally
        {
            winlogon.SetValue(ShellValue, current, RegistryValueKind.String);
            log("Put the shell value back after Explorer started.");
        }
    }

    /// <summary>The setting that overrides the per-user value, named for the user, or null.</summary>
    public string? Override()
    {
        foreach (var (hive, name) in new[] { (Hive.User, "HKCU"), (Hive.Machine, "HKLM") })
        {
            using var policy = Open(hive, PolicyKey);
            if (policy?.GetValue(ShellValue) is string { Length: > 0 })
            {
                return $@"the Custom User Interface policy ({name}\{PolicyKey}\{ShellValue})";
            }
        }

        using var launcher = Open(Hive.Machine, ShellLauncherKey);
        if (launcher is not null)
        {
            var sid = WindowsIdentity.GetCurrent().User?.Value;
            using var mine = sid is null ? null : launcher.OpenSubKey(sid);
            if (launcher.GetValue(ShellValue) is not null || mine?.GetValue(ShellValue) is not null)
            {
                return $@"Shell Launcher (HKLM\{ShellLauncherKey})";
            }
        }

        return null;
    }

    private static bool IsOurs(string value) =>
        value.Contains("SubZeroDev.WindowManager.exe", StringComparison.OrdinalIgnoreCase)
        && value.Contains("--mode replacement", StringComparison.OrdinalIgnoreCase);

    private RegistryKey UserKey(string path) => Registry.CurrentUser.CreateSubKey(Rooted(Hive.User, path), writable: true);

    private RegistryKey? Open(Hive hive, string path) =>
        _root is not null || hive == Hive.User
            ? Registry.CurrentUser.OpenSubKey(Rooted(hive, path))
            : Registry.LocalMachine.OpenSubKey(path);

    private string Rooted(Hive hive, string path) =>
        _root is null ? path : Path.Combine(_root, hive == Hive.User ? "HKCU" : "HKLM", path);

    private State? ReadState()
    {
        try
        {
            var json = JsonNode.Parse(File.ReadAllText(StatePath));
            var previous = json?["previous"] as JsonObject;
            return new State(
                (bool?)json?["registered"] ?? false,
                new Previous((bool?)previous?["present"] ?? false, (string?)previous?["value"]),
                json?["registeredAt"] is JsonValue at && at.TryGetValue<string>(out var text)
                    ? DateTimeOffset.Parse(text, CultureInfo.InvariantCulture)
                    : null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or FormatException or InvalidOperationException)
        {
            return null;
        }
    }

    private void WriteState(State state)
    {
        var previous = new JsonObject { ["present"] = state.Previous.Present };
        if (state.Previous.Present)
        {
            previous["value"] = state.Previous.Value;
        }

        var json = new JsonObject
        {
            ["schemaVersion"] = 1,
            ["registered"] = state.Registered,
            ["previous"] = previous,
        };
        if (state.RegisteredAt is { } at)
        {
            json["registeredAt"] = at.ToString("O", CultureInfo.InvariantCulture);
        }

        Directory.CreateDirectory(dataRoot);
        var temp = StatePath + ".tmp";
        File.WriteAllText(temp, json.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n", Utf8NoBom);
        File.Move(temp, StatePath, overwrite: true);
    }

    private static string? Configured(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;

    private enum Hive
    {
        User,
        Machine,
    }

    private sealed record Previous(bool Present, string? Value);

    private sealed record State(bool Registered, Previous Previous, DateTimeOffset? RegisteredAt);
}

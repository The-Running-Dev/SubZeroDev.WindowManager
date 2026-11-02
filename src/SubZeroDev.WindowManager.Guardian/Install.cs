using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32;

namespace SubZeroDev.WindowManager.Guardian;

/// <summary>
/// The per-user install registration behind <c>--first-run</c> and <c>--uninstall</c>: a Start-menu shortcut
/// to the guardian and, when <c>startAtSignIn</c> is on, an <c>HKCU</c> Run value. Nothing here needs
/// elevation, and the data folder is never touched. Tests point both places elsewhere through
/// <see cref="StartMenuVariable"/> and <see cref="RunKeyVariable"/>.
/// </summary>
internal static class Install
{
    public const string StartMenuVariable = "SUBZERODEV_WM_START_MENU_DIR";
    public const string RunKeyVariable = "SUBZERODEV_WM_RUN_KEY";
    public const string RunValueName = "SubZeroDev.WindowManager";
    public const string ShortcutName = "SubZeroDev.WindowManager.lnk";
    private const string DefaultRunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private static string StartMenu => Configured(StartMenuVariable) ?? Environment.GetFolderPath(Environment.SpecialFolder.Programs);

    private static string RunKey => Configured(RunKeyVariable) ?? DefaultRunKey;

    /// <summary>Creates the shortcut, and sets or clears the Run value to match <c>startAtSignIn</c>.</summary>
    public static void Register(string exePath, string dataRoot, Action<string> log)
    {
        var shortcut = Path.Combine(StartMenu, ShortcutName);
        Directory.CreateDirectory(StartMenu);
        WriteShortcut(shortcut, exePath);
        log($"Start-menu shortcut written: {shortcut}");

        using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true);
        if (StartAtSignIn(dataRoot))
        {
            key.SetValue(RunValueName, $"\"{exePath}\"", RegistryValueKind.String);
            log($"Run value set under HKCU\\{RunKey}.");
        }
        else
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
        }
    }

    /// <summary>Removes the shortcut and the Run value; either may already be gone.</summary>
    public static void Unregister(Action<string> log)
    {
        var shortcut = Path.Combine(StartMenu, ShortcutName);
        if (File.Exists(shortcut))
        {
            File.Delete(shortcut);
            log($"Start-menu shortcut removed: {shortcut}");
        }

        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key?.GetValue(RunValueName) is not null)
        {
            key.DeleteValue(RunValueName, throwOnMissingValue: false);
            log($"Run value removed from HKCU\\{RunKey}.");
        }
    }

    private static bool StartAtSignIn(string dataRoot)
    {
        try
        {
            var settings = JsonNode.Parse(File.ReadAllText(Path.Combine(dataRoot, "settings.json")));
            return settings?["startAtSignIn"] is JsonValue value && value.TryGetValue<bool>(out var start) && start;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return false;
        }
    }

    private static string? Configured(string variable) =>
        Environment.GetEnvironmentVariable(variable) is { Length: > 0 } value ? value : null;

    private static void WriteShortcut(string path, string target)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            link.SetPath(target);
            link.SetWorkingDirectory(Path.GetDirectoryName(target)!);
            link.SetDescription("SubZeroDev Window Manager");
            ((IPersistFile)link).Save(path, fRemember: true);
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }

    [ComImport]
    [Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink;

    /// <summary>The setters this class uses; the getters before them only keep the vtable order.</summary>
    [ComImport]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellLinkW
    {
        void GetPath(nint file, int max, nint findData, uint flags);

        void GetIDList(out nint idList);

        void SetIDList(nint idList);

        void GetDescription(nint name, int max);

        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);

        void GetWorkingDirectory(nint directory, int max);

        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string directory);

        void GetArguments(nint arguments, int max);

        void SetArguments([MarshalAs(UnmanagedType.LPWStr)] string arguments);

        void GetHotkey(out short hotkey);

        void SetHotkey(short hotkey);

        void GetShowCmd(out int showCmd);

        void SetShowCmd(int showCmd);

        void GetIconLocation(nint path, int max, out int index);

        void SetIconLocation([MarshalAs(UnmanagedType.LPWStr)] string path, int index);

        void SetRelativePath([MarshalAs(UnmanagedType.LPWStr)] string path, uint reserved);

        void Resolve(nint window, uint flags);

        void SetPath([MarshalAs(UnmanagedType.LPWStr)] string file);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SubZeroDev.WindowManager.Guardian;

/// <summary>
/// What the guardian does with Explorer in replacement mode: the Shift escape hatch, the check that no other
/// process is the shell, asking Explorer to exit for <c>--take-over</c>, and starting Explorer as the fallback.
/// Under the test hook nothing reaches the desktop: a <c>shift-held</c> file stands in for the Shift key, a
/// <c>shell-owner</c> file for another process's shell window, and Explorer starts and exit requests are
/// appended to <c>shell-actions.log</c> instead of being done.
/// </summary>
internal static partial class ShellStart
{
    public const string ActionsLog = "shell-actions.log";
    private const int VkShift = 0x10;
    private const uint ExplorerExitMessage = 0x5B4;
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(10);

    public static bool ShiftHeld(string? hook) =>
        hook is not null ? File.Exists(Path.Combine(hook, "shift-held")) : (GetAsyncKeyState(VkShift) & 0x8000) != 0;

    /// <summary>The process that owns the shell window when it is not this one, or null.</summary>
    public static int? ShellOwner(string? hook)
    {
        if (hook is not null)
        {
            var file = Path.Combine(hook, "shell-owner");
            return File.Exists(file) ? int.TryParse(File.ReadAllText(file).Trim(), out var pid) ? pid : 0 : null;
        }

        var shell = GetShellWindow();
        if (shell == 0)
        {
            return null;
        }

        _ = GetWindowThreadProcessId(shell, out var owner);
        return owner == Environment.ProcessId ? null : (int)owner;
    }

    /// <summary>Asks Explorer to exit the way its own exit command does, so Windows does not restart it, and waits for it.</summary>
    public static async Task TakeOverAsync(string? hook, Action<string> log)
    {
        if (hook is not null)
        {
            Record(hook, "take-over");
            return;
        }

        var tray = FindWindowW("Shell_TrayWnd", null);
        if (tray == 0 || !PostMessageW(tray, ExplorerExitMessage, 0, 0))
        {
            log("Could not ask Explorer to exit: no taskbar window to ask.");
            return;
        }

        var waited = Stopwatch.StartNew();
        while (GetShellWindow() != 0 && waited.Elapsed < ExitWait)
        {
            await Task.Delay(100);
        }

        log(GetShellWindow() == 0 ? "Explorer exited; taking over as the shell." : "Explorer did not exit in time; continuing.");
    }

    /// <summary>Starts Explorer so the user is never left without a shell (I23), and waits for its shell window.</summary>
    public static async Task StartExplorerAsync(string? hook, Action<string> log)
    {
        if (hook is not null)
        {
            Record(hook, "explorer");
            log("Started Explorer (recorded under the test hook).");
            return;
        }

        var explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");
        try
        {
            using var started = Process.Start(new ProcessStartInfo(explorer) { UseShellExecute = false });
            log($"Started Explorer ({started?.Id}).");
        }
        catch (System.ComponentModel.Win32Exception e)
        {
            log($"Could not start Explorer: {e.Message}");
            return;
        }

        var waited = Stopwatch.StartNew();
        while (GetShellWindow() == 0 && waited.Elapsed < ExitWait)
        {
            await Task.Delay(100);
        }

        log(GetShellWindow() != 0 ? "Explorer is the shell." : "Explorer has no shell window yet; continuing.");
    }

    private static void Record(string hook, string action)
    {
        Directory.CreateDirectory(hook);
        File.AppendAllText(Path.Combine(hook, ActionsLog), action + "\n", new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll")]
    private static partial nint GetShellWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll", EntryPoint = "FindWindowW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial nint FindWindowW(string className, string? windowName);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);
}

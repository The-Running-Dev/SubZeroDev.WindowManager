using System.Runtime.InteropServices;
using System.Text;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// <c>host.desktop.show</c>, as Win+D: the first call minimises every ordinary application window and remembers
/// them; the next call restores the ones still minimised, bottom first so their order comes back, and forgets
/// them. Under the test hook only windows of the processes listed in the hook file <c>desktop-show-pids</c> are
/// touched, and each step is recorded in <c>desktop-actions.log</c>.
/// </summary>
public sealed class ShowDesktop(string? hookDirectory)
{
    public const string HookPidsFile = "desktop-show-pids";

    private readonly object _gate = new();
    private readonly int _self = Environment.ProcessId;
    private List<nint> _minimised = [];

    /// <summary>Minimises or restores; returns how many windows it acted on and whether it restored.</summary>
    public (int Count, bool Restored) Toggle()
    {
        lock (_gate)
        {
            var remembered = _minimised.Where(w => IsWindow(w) && IsIconic(w)).ToList();
            _minimised = [];
            if (remembered.Count > 0)
            {
                for (var i = remembered.Count - 1; i >= 0; i--)
                {
                    ShowWindowAsync(remembered[i], SwShowNoActivate);
                    Record($"desktop-show restore 0x{remembered[i]:X}");
                }

                if (hookDirectory is null)
                {
                    SetForegroundWindow(remembered[0]);
                }

                return (remembered.Count, true);
            }

            var windows = EligibleWindows();
            foreach (var window in windows)
            {
                ShowWindowAsync(window, SwShowMinNoActive);
                Record($"desktop-show minimise 0x{window:X}");
            }

            _minimised = windows;
            return (windows.Count, false);
        }
    }

    /// <summary>
    /// <c>host.desktop.minimizeAll</c>, as Win+M: minimises every ordinary application window and remembers
    /// nothing, so a later <see cref="Toggle"/> starts afresh. Returns how many windows it minimised.
    /// </summary>
    public int MinimiseAll()
    {
        lock (_gate)
        {
            _minimised = [];
            var windows = EligibleWindows();
            foreach (var window in windows)
            {
                ShowWindowAsync(window, SwShowMinNoActive);
                Record($"desktop-minimize-all 0x{window:X}");
            }

            return windows.Count;
        }
    }

    /// <summary>The windows Show desktop acts on, top first.</summary>
    private List<nint> EligibleWindows()
    {
        var allowed = AllowedProcesses();
        var windows = new List<nint>();
        for (var window = GetTopWindow(0); window != 0; window = GetWindow(window, GwHwndNext))
        {
            if (Eligible(window, allowed))
            {
                windows.Add(window);
            }
        }

        return windows;
    }

    /// <summary>The processes a test lets this touch, or null outside the hook (any process).</summary>
    private HashSet<int>? AllowedProcesses()
    {
        if (hookDirectory is null)
        {
            return null;
        }

        var file = Path.Combine(hookDirectory, HookPidsFile);
        return File.Exists(file)
            ? [.. File.ReadAllLines(file).Select(l => int.TryParse(l.Trim(), out var pid) ? pid : 0).Where(pid => pid > 0)]
            : [];
    }

    /// <summary>An application window the user would see on the taskbar: visible, not minimised, unowned, not a tool window, not cloaked, not ours.</summary>
    private bool Eligible(nint window, HashSet<int>? allowed)
    {
        if (!IsWindowVisible(window) || IsIconic(window) || GetWindow(window, GwOwner) != 0
            || (GetWindowLongPtrW(window, GwlExStyle) & WsExToolWindow) != 0)
        {
            return false;
        }

        GetWindowThreadProcessId(window, out var pid);
        if (pid == _self || (allowed is not null && !allowed.Contains((int)pid)))
        {
            return false;
        }

        if (DwmGetWindowAttribute(window, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
        {
            return false;
        }

        return GetWindowRect(window, out var rect) && rect.Right > rect.Left && rect.Bottom > rect.Top;
    }

    private void Record(string line)
    {
        if (hookDirectory is not null)
        {
            Directory.CreateDirectory(hookDirectory);
            File.AppendAllText(Path.Combine(hookDirectory, Recovery.Reverter.DesktopActionsLog), line + "\n", new UTF8Encoding(false));
        }
    }

    private const uint GwHwndNext = 2;
    private const uint GwOwner = 4;
    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x80;
    private const int SwShowMinNoActive = 7;
    private const int SwShowNoActivate = 4;
    private const int DwmwaCloaked = 14;

    [DllImport("user32.dll")]
    private static extern nint GetTopWindow(nint parent);

    [DllImport("user32.dll")]
    private static extern nint GetWindow(nint window, uint command);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(nint window);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(nint window);

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint window, int index);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint window, out Rect rect);

    [DllImport("user32.dll")]
    private static extern bool ShowWindowAsync(nint window, int command);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(nint window);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(nint window, int attribute, out int value, int size);
}

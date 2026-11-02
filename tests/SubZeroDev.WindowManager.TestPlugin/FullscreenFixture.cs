using System.Globalization;
using System.Runtime.InteropServices;

namespace SubZeroDev.WindowManager.TestPlugin;

/// <summary>
/// <c>fixture-fullscreen &lt;left&gt; &lt;top&gt; &lt;right&gt; &lt;bottom&gt;</c>: not a plugin. Opens a borderless window
/// covering that rectangle in physical pixels, as a full-screen game or presentation does, brings it to the
/// foreground, prints "&lt;hwnd&gt; &lt;true|false&gt;" (whether it became the foreground window) and then "ready",
/// and pumps messages until it is killed.
/// </summary>
internal static unsafe partial class Fixture
{
    private const uint WsPopup = 0x80000000;
    private static readonly nint PerMonitorAwareV2 = -4;

    public static int RunFullscreen(string[] bounds)
    {
        var rect = bounds.Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        SetThreadDpiAwarenessContext(PerMonitorAwareV2);
        var module = GetModuleHandleW(null);
        Register("SzdFixtureFullscreen", module, 0);
        var hwnd = CreateWindowExW(0, "SzdFixtureFullscreen", "SubZeroDev full-screen fixture", WsPopup | WsVisible,
            rect[0], rect[1], rect[2] - rect[0], rect[3] - rect[1], 0, 0, module, 0);
        if (hwnd == 0)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastPInvokeError()}");
        }

        // A process started in the background may only take the foreground while attached to the input of
        // the thread that has it.
        var foreground = GetWindowThreadProcessId(GetForegroundWindow(), 0);
        var current = GetCurrentThreadId();
        var attached = foreground != 0 && foreground != current && AttachThreadInput(current, foreground, true);
        SetForegroundWindow(hwnd);
        BringWindowToTop(hwnd);
        if (attached)
        {
            AttachThreadInput(current, foreground, false);
        }

        Console.WriteLine($"{hwnd} {(GetForegroundWindow() == hwnd ? "true" : "false")}");
        Console.WriteLine("ready");
        Console.Out.Flush();
        while (GetMessageW(out var message, 0, 0, 0) > 0)
        {
            TranslateMessage(in message);
            DispatchMessageW(in message);
        }

        return 0;
    }

    [LibraryImport("user32.dll")]
    private static partial nint SetThreadDpiAwarenessContext(nint context);

    [LibraryImport("user32.dll")]
    private static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint hwnd, nint processId);

    [LibraryImport("kernel32.dll")]
    private static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool on);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool BringWindowToTop(nint hwnd);
}

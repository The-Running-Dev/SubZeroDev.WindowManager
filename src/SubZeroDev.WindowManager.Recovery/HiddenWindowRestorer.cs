using System.Runtime.InteropServices;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Recovery;

/// <summary>What the guardian reads of a window before it shows it.</summary>
public readonly record struct WindowProbe(int ProcessId, long ProcessStartTicks, int ThreadId, string ClassName, bool Visible);

/// <summary>The two window operations the revert of a <see cref="DesktopChangeKind.HiddenWindows"/> entry needs; tests supply a fake.</summary>
public interface IWindowShowPort
{
    /// <summary>The window's identity now; null when the handle is not a window.</summary>
    WindowProbe? Probe(nint handle);

    /// <summary>Shows the window without activating, moving, resizing or restoring it, so a minimised window stays minimised.</summary>
    bool ShowInPlace(nint handle);
}

/// <summary>
/// Shows the windows a dead host had hidden. A window is shown only if it is still the window the host hid (all five
/// fingerprint fields match) and is not visible already; anything else is left alone, so a reused handle is never touched.
/// </summary>
public static partial class HiddenWindowRestorer
{
    /// <param name="payload">The entry's payload.</param>
    /// <param name="port">The window operations.</param>
    /// <returns>The handles that were shown.</returns>
    public static IReadOnlyList<nint> Restore(JsonObject payload, IWindowShowPort port)
    {
        var shown = new List<nint>();
        if (payload["windows"] is not JsonArray windows)
        {
            return shown;
        }

        foreach (var node in windows)
        {
            if (node is not JsonObject item || !TryRead(item, out var expected))
            {
                continue;
            }

            if (port.Probe(expected.Handle) is not { } now || now.Visible
                || now.ProcessId != expected.ProcessId || now.ProcessStartTicks != expected.ProcessStartTicks
                || now.ThreadId != expected.ThreadId || !string.Equals(now.ClassName, expected.ClassName, StringComparison.Ordinal))
            {
                continue;
            }

            if (port.ShowInPlace(expected.Handle))
            {
                shown.Add(expected.Handle);
            }
        }

        return shown;
    }

    /// <summary>The Win32 form of <see cref="IWindowShowPort"/>.</summary>
    public static IWindowShowPort Win32 { get; } = new Win32ShowPort();

    /// <summary>The handles an entry names, for a test hook that records instead of showing.</summary>
    public static IReadOnlyList<HiddenWindow> Windows(JsonObject payload)
    {
        var list = new List<HiddenWindow>();
        if (payload["windows"] is JsonArray windows)
        {
            foreach (var node in windows)
            {
                if (node is JsonObject item && TryRead(item, out var window))
                {
                    list.Add(window);
                }
            }
        }

        return list;
    }

    private static bool TryRead(JsonObject item, out HiddenWindow window)
    {
        window = default;
        if (item["hwnd"] is not JsonValue hwnd || !hwnd.TryGetValue<long>(out var handle) || handle == 0
            || item["processId"] is not JsonValue pid || !pid.TryGetValue<int>(out var processId)
            || item["processStartTicks"] is not JsonValue ticks || !ticks.TryGetValue<long>(out var startTicks)
            || item["threadId"] is not JsonValue tid || !tid.TryGetValue<int>(out var threadId)
            || item["className"] is not JsonValue cls || !cls.TryGetValue<string>(out var className))
        {
            return false;
        }

        window = new HiddenWindow((nint)handle, processId, startTicks, threadId, className);
        return true;
    }

    private sealed unsafe class Win32ShowPort : IWindowShowPort
    {
        public WindowProbe? Probe(nint handle)
        {
            if (!IsWindow(handle))
            {
                return null;
            }

            var thread = GetWindowThreadProcessId(handle, out var processId);
            if (thread == 0)
            {
                return null;
            }

            long startTicks = 0;
            var process = OpenProcess(ProcessQueryLimitedInformation, 0, processId);
            if (process != 0)
            {
                long creation = 0, exit = 0, kernel = 0, user = 0;
                if (GetProcessTimes(process, &creation, &exit, &kernel, &user))
                {
                    startTicks = creation;
                }

                CloseHandle(process);
            }

            var buffer = stackalloc char[256];
            var length = GetClassNameW(handle, buffer, 256);
            return new WindowProbe((int)processId, startTicks, (int)thread, length > 0 ? new string(buffer, 0, length) : string.Empty,
                IsWindowVisible(handle));
        }

        public bool ShowInPlace(nint handle) =>
            SetWindowPos(handle, 0, 0, 0, 0, 0, SwpNoSize | SwpNoMove | SwpNoZOrder | SwpNoActivate | SwpShowWindow);
    }

    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint SwpNoSize = 0x0001;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpShowWindow = 0x0040;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    private static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    private static unsafe partial int GetClassNameW(nint window, char* buffer, int max);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint window, nint after, int x, int y, int width, int height, uint flags);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, int inherit, uint processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetProcessTimes(nint process, long* creation, long* exit, long* kernel, long* user);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);
}

using System.Runtime.InteropServices;

namespace SubZeroDev.WindowManager.Host.Platform;

[StructLayout(LayoutKind.Sequential)]
internal struct Rect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct AppBarData
{
    public uint Size;
    public nint Window;
    public uint CallbackMessage;
    public uint Edge;
    public Rect Bounds;
    public nint Param;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct MonitorInfoEx
{
    public uint Size;
    public Rect Monitor;
    public Rect Work;
    public uint Flags;
    public fixed char Device[32];
}

[StructLayout(LayoutKind.Sequential)]
internal struct Msg
{
    public nint Window;
    public uint Message;
    public nuint WParam;
    public nint LParam;
    public uint Time;
    public int X;
    public int Y;
    public uint Private;
}

[StructLayout(LayoutKind.Sequential)]
internal struct FlashWInfo
{
    public uint Size;
    public nint Window;
    public uint Flags;
    public uint Count;
    public uint Timeout;
}

[StructLayout(LayoutKind.Sequential)]
internal struct KeyboardInput
{
    public uint Type;
    public uint Padding;
    public ushort VirtualKey;
    public ushort ScanCode;
    public uint Flags;
    public uint Time;
    public nuint ExtraInfo;
    public ulong Padding2;
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct WindowClassEx
{
    public uint Size;
    public uint Style;
    public delegate* unmanaged<nint, uint, nuint, nint, nint> WindowProc;
    public int ClassExtra;
    public int WindowExtra;
    public nint Instance;
    public nint Icon;
    public nint Cursor;
    public nint Background;
    public char* MenuName;
    public char* ClassName;
    public nint SmallIcon;
}

internal static unsafe partial class Native
{
    public const uint AbmNew = 0;
    public const uint AbmRemove = 1;
    public const uint AbmQueryPos = 2;
    public const uint AbmSetPos = 3;
    public const uint MonitorInfoPrimary = 1;
    public const uint SpiGetWorkArea = 0x0030;

    [LibraryImport("shell32.dll")]
    public static partial nuint SHAppBarMessage(uint message, ref AppBarData data);

    [LibraryImport("user32.dll", EntryPoint = "RegisterWindowMessageW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint RegisterWindowMessage(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumDisplayMonitors(nint hdc, nint clip, delegate* unmanaged<nint, nint, Rect*, nint, int> callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetMonitorInfo(nint monitor, MonitorInfoEx* info);

    [LibraryImport("shcore.dll")]
    public static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint param, Rect* data, uint winIni);

    public const uint RegNotifyChangeLastSet = 0x4;

    public const uint WmHotkey = 0x0312;
    public const uint WmApp = 0x8000;
    public const uint WmQuit = 0x0012;
    public const uint ModNoRepeat = 0x4000;
    public const int SwRestore = 9;
    public const int SwMinimize = 6;
    public const uint FlashwAll = 3;
    public const uint FlashwTimerNoFg = 12;
    public const uint InputKeyboard = 1;
    public const uint KeyEventFKeyUp = 2;
    public const ushort VkMenu = 0x12;

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterHotKey(nint window, int id, uint modifiers, uint virtualKey);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnregisterHotKey(nint window, int id);

    [LibraryImport("user32.dll", EntryPoint = "GetMessageW")]
    public static partial int GetMessage(out Msg message, nint window, uint min, uint max);

    [LibraryImport("user32.dll", EntryPoint = "PeekMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PeekMessage(out Msg message, nint window, uint min, uint max, uint remove);

    [LibraryImport("user32.dll", EntryPoint = "PostThreadMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostThreadMessage(uint threadId, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AllowSetForegroundWindow(int processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint window, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint window);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AttachThreadInput(uint attach, uint attachTo, [MarshalAs(UnmanagedType.Bool)] bool doAttach);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool BringWindowToTop(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FlashWindowEx(ref FlashWInfo info);

    [LibraryImport("user32.dll")]
    public static partial uint SendInput(uint count, KeyboardInput* inputs, int size);

    [LibraryImport("advapi32.dll")]
    public static partial int RegNotifyChangeKeyValue(nint key, int watchSubtree, uint filter, nint changed, int asynchronous);

    public const uint WmDisplayChange = 0x007E;
    public const uint WmSettingChange = 0x001A;
    public const uint WsPopup = 0x80000000;
    public const uint WsCaption = 0x00C00000;
    public const int GwlStyle = -16;

    [LibraryImport("kernel32.dll", EntryPoint = "GetModuleHandleW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint GetModuleHandle(string? name);

    [LibraryImport("user32.dll", EntryPoint = "RegisterClassExW", SetLastError = true)]
    public static partial ushort RegisterClassEx(WindowClassEx* windowClass);

    [LibraryImport("user32.dll", EntryPoint = "CreateWindowExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint CreateWindowEx(uint exStyle, string className, string title, uint style,
        int x, int y, int width, int height, nint parent, nint menu, nint module, nint param);

    [LibraryImport("user32.dll", EntryPoint = "DefWindowProcW")]
    public static partial nint DefWindowProc(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "DispatchMessageW")]
    public static partial nint DispatchMessage(in Msg message);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    public static partial nint GetWindowLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW")]
    public static partial int GetClassName(nint window, char* name, int capacity);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    public const uint SpiSetWorkArea = 0x002F;
    public const uint SpiGetMinimizedMetrics = 0x002B;
    public const uint SpiSetMinimizedMetrics = 0x002C;
    public const uint SpifSendChange = 0x0002;
    public const int ArwHide = 0x0008;
    public const uint WmUser = 0x0400;
    public const uint WsExToolWindow = 0x00000080;
    public const uint EventModifyState = 0x0002;
    public const uint HshellWindowCreated = 1;
    public const uint HshellWindowDestroyed = 2;
    public const uint HshellWindowActivated = 4;
    public const uint HshellRudeAppActivated = 0x8004;
    public const uint HshellFlash = 0x8006;

    [LibraryImport("user32.dll", EntryPoint = "SystemParametersInfoW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SystemParametersInfo(uint action, uint param, MinimizedMetrics* data, uint winIni);

    [LibraryImport("user32.dll")]
    public static partial nint GetShellWindow();

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetShellWindow(nint window);

    /// <summary>Undocumented, exported by user32 since Windows NT: the window that hears shell hook messages with their high bit.</summary>
    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetTaskmanWindow(nint window);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool RegisterShellHookWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeregisterShellHookWindow(nint window);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("kernel32.dll", EntryPoint = "OpenEventW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint OpenEvent(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, string name);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetEvent(nint handle);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetThreadDesktop(nint desktop);
}

[StructLayout(LayoutKind.Sequential)]
internal struct MinimizedMetrics
{
    public uint Size;
    public int Width;
    public int HorizontalGap;
    public int VerticalGap;
    public int Arrange;
}

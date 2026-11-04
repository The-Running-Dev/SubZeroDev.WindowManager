using System.Runtime.InteropServices;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>The Win32 surface the window tracker and the facts reader need; the rest of <see cref="Native"/> is shared.</summary>
internal static unsafe partial class Native
{
    public const uint WinEventOutOfContext = 0x0000;
    public const uint EventSystemForeground = 0x0003;
    public const uint EventSystemMoveSizeEnd = 0x000B;
    public const uint EventSystemMinimizeStart = 0x0016;
    public const uint EventSystemMinimizeEnd = 0x0017;
    public const uint EventObjectCreate = 0x8000;
    public const uint EventObjectDestroy = 0x8001;
    public const uint EventObjectShow = 0x8002;
    public const uint EventObjectHide = 0x8003;
    public const uint EventObjectNameChange = 0x800C;
    public const uint EventObjectCloaked = 0x8017;
    public const uint EventObjectUncloaked = 0x8018;
    public const uint WmTimer = 0x0113;
    public const int GwlExStyle = -20;
    public const uint GwOwner = 4;
    public const long WsExAppWindow = 0x00040000;
    public const long WsExNoActivate = 0x08000000;
    public const long WsExToolWindowStyle = 0x00000080;
    public const uint DwmwaCloaked = 14;
    public const uint MonitorDefaultToNearest = 2;
    public const int HshellRedraw = 6;

    [LibraryImport("user32.dll")]
    public static partial nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        delegate* unmanaged<nint, uint, nint, int, int, uint, uint, void> callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool EnumWindows(delegate* unmanaged<nint, nint, int> callback, nint data);

    [LibraryImport("user32.dll")]
    public static partial nint GetWindow(nint window, uint command);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextLengthW(nint window);

    [LibraryImport("user32.dll")]
    public static partial int GetWindowTextW(nint window, char* text, int capacity);

    [LibraryImport("user32.dll")]
    public static partial nint MonitorFromWindow(nint window, uint flags);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint window, uint attribute, out int value, uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, long* creation, long* exit, long* kernel, long* user);

    [LibraryImport("shell32.dll")]
    public static partial int SHGetPropertyStoreForWindow(nint window, Guid* riid, nint* store);

    [LibraryImport("ole32.dll")]
    public static partial int PropVariantClear(byte* value);
}

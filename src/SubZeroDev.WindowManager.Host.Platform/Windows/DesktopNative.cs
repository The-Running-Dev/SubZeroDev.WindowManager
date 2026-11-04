using System.Runtime.InteropServices;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary><c>DWM_THUMBNAIL_PROPERTIES</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct DwmThumbnailProperties
{
    public uint Flags;
    public Rect Destination;
    public Rect Source;
    public byte Opacity;
    public int Visible;
    public int SourceClientAreaOnly;
}

/// <summary>The Win32 surface the window effects thread and the thumbnail service need.</summary>
internal static partial class DesktopNative
{
    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoZOrder = 0x0004;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;
    public const uint SwpHideWindow = 0x0080;
    public const uint SwpAsyncWindowPos = 0x4000;
    public const uint WmClose = 0x0010;
    public const int ErrorAccessDenied = 5;
    public const int ErrorInvalidWindowHandle = 1400;
    public const uint DwmTnpRectDestination = 0x01;
    public const uint DwmTnpOpacity = 0x04;
    public const uint DwmTnpVisible = 0x08;
    public const uint DwmTnpSourceClientAreaOnly = 0x10;
    public const uint DwmwaCloaked = 14;

    /// <summary>The fixed part of every hide and show: never move, size, reorder or activate, and never wait on the window.</summary>
    public const uint SwpQuiet = SwpNoMove | SwpNoSize | SwpNoZOrder | SwpNoActivate | SwpAsyncWindowPos;

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessageW(nint window, uint message, nuint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsHungAppWindow(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint window);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmUnregisterThumbnail(nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmUpdateThumbnailProperties(nint thumbnail, in DwmThumbnailProperties properties);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmQueryThumbnailSourceSize(nint thumbnail, out Size size);

    [LibraryImport("dwmapi.dll")]
    public static partial int DwmGetWindowAttribute(nint window, uint attribute, out int value, uint size);

    /// <summary><c>SIZE</c>.</summary>
    [StructLayout(LayoutKind.Sequential)]
    public struct Size
    {
        public int Width;
        public int Height;
    }
}

using System.Runtime.InteropServices;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Extracts system icons off the calling thread (I9). A window's icon comes from <c>WM_GETICON</c> (with
/// a 200 ms timeout so a hung window cannot stall it), then its class icon, then its executable's first
/// icon. An app's icon comes from its executable. Each icon is drawn at the asked size into a 32-bit
/// buffer; an icon without alpha takes its transparency from its mask.
/// </summary>
public sealed class IconService : IIconService
{
    public Task<Outcome<IconImage>> ExtractAsync(IconRef reference, int sizePx, CancellationToken ct) => Task.Run(() =>
    {
        var size = Math.Clamp(sizePx, 8, 256);
        return reference switch
        {
            IconRef.Window window => FromWindow((nint)window.Hwnd, size),
            IconRef.App { Exe: { Length: > 0 } exe } => FromFile(exe, size),
            _ => NotFound($"No system icon for {reference}."),
        };
    }, ct);

    public Task<Outcome<IconImage>> DefaultAsync(int sizePx, CancellationToken ct) => Task.Run(() =>
    {
        var size = Math.Clamp(sizePx, 8, 256);
        var icon = Native.LoadImage(0, Native.IdiApplication, Native.ImageIcon, size, size, Native.LrShared);
        return icon == 0 ? NotFound("The system has no default application icon.") : Draw(icon, size);
    }, ct);

    private static Outcome<IconImage> FromWindow(nint hwnd, int size)
    {
        if (!Native.IsWindow(hwnd))
        {
            return Outcome<IconImage>.Fail(CoreErrorCode.WindowGone, $"Window 0x{hwnd:X} no longer exists.");
        }

        // Icons owned by the window or its class are shared: they are drawn, never destroyed.
        var big = size > 16;
        int[] kinds = big ? [Native.IconBig, Native.IconSmall] : [Native.IconSmall, Native.IconBig];
        foreach (var kind in kinds)
        {
            if (Native.SendMessageTimeout(hwnd, Native.WmGetIcon, (nuint)kind, 0, Native.SmtoAbortIfHung, 200, out var result) != 0 && result != 0)
            {
                return Draw((nint)result, size);
            }
        }

        int[] indexes = big ? [Native.GclpHIcon, Native.GclpHIconSm] : [Native.GclpHIconSm, Native.GclpHIcon];
        foreach (var index in indexes)
        {
            var classIcon = Native.GetClassLongPtr(hwnd, index);
            if (classIcon != 0)
            {
                return Draw((nint)classIcon, size);
            }
        }

        return ExecutableOf(hwnd) is { } exe ? FromFile(exe, size) : NotFound($"Window 0x{hwnd:X} has no icon.");
    }

    private static Outcome<IconImage> FromFile(string path, int size)
    {
        var hr = Native.SHDefExtractIcon(path, 0, 0, out var large, out var small, (uint)(size | (size << 16)));
        if (small != 0 && small != large)
        {
            Native.DestroyIcon(small);
        }

        if (hr != 0 || large == 0)
        {
            return NotFound($"'{path}' has no icon.");
        }

        try
        {
            return Draw(large, size);
        }
        finally
        {
            Native.DestroyIcon(large);
        }
    }

    private static unsafe string? ExecutableOf(nint hwnd)
    {
        Native.GetWindowThreadProcessId(hwnd, out var processId);
        var process = Native.OpenProcess(Native.ProcessQueryLimitedInformation, 0, processId);
        if (process == 0)
        {
            return null;
        }

        try
        {
            var buffer = stackalloc char[1024];
            var length = 1024u;
            return Native.QueryFullProcessImageName(process, 0, buffer, ref length) ? new string(buffer, 0, (int)length) : null;
        }
        finally
        {
            Native.CloseHandle(process);
        }
    }

    private static Outcome<IconImage> Draw(nint icon, int size)
    {
        var dc = Native.CreateCompatibleDC(0);
        if (dc == 0)
        {
            return NotFound("No device context for drawing an icon.");
        }

        try
        {
            var image = Render(dc, icon, size, Native.DiNormal);
            if (image is null)
            {
                return NotFound("The icon could not be drawn.");
            }

            if (!HasAlpha(image))
            {
                // No alpha channel: opaque where the mask is black, clear where it is white.
                var mask = Render(dc, icon, size, Native.DiMask);
                for (var i = 0; i < image.Length; i += 4)
                {
                    var opaque = mask is null || mask[i] == 0;
                    image[i + 3] = opaque ? (byte)255 : (byte)0;
                    if (!opaque)
                    {
                        image[i] = image[i + 1] = image[i + 2] = 0;
                    }
                }
            }

            return Outcome<IconImage>.Success(new IconImage(size, size, image));
        }
        finally
        {
            Native.DeleteDC(dc);
        }
    }

    private static unsafe byte[]? Render(nint dc, nint icon, int size, uint flags)
    {
        var header = new BitmapInfoHeader
        {
            Size = 40,
            Width = size,
            Height = -size,
            Planes = 1,
            BitCount = 32,
        };
        void* bits;
        var bitmap = Native.CreateDIBSection(dc, &header, 0, &bits, 0, 0);
        if (bitmap == 0)
        {
            return null;
        }

        try
        {
            var previous = Native.SelectObject(dc, bitmap);
            var drawn = Native.DrawIconEx(dc, 0, 0, icon, size, size, 0, 0, flags);
            Native.GdiFlush();
            Native.SelectObject(dc, previous);
            return drawn ? new Span<byte>(bits, size * size * 4).ToArray() : null;
        }
        finally
        {
            Native.DeleteObject(bitmap);
        }
    }

    private static bool HasAlpha(byte[] image)
    {
        for (var i = 3; i < image.Length; i += 4)
        {
            if (image[i] != 0)
            {
                return true;
            }
        }

        return false;
    }

    private static Outcome<IconImage> NotFound(string detail) => Outcome<IconImage>.Fail(CoreErrorCode.NotFound, detail);
}

/// <summary>A <c>BITMAPINFO</c>: the header followed by its one-entry colour table.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct BitmapInfoHeader
{
    public uint Size;
    public int Width;
    public int Height;
    public ushort Planes;
    public ushort BitCount;
    public uint Compression;
    public uint SizeImage;
    public int XPelsPerMeter;
    public int YPelsPerMeter;
    public uint ClrUsed;
    public uint ClrImportant;
    public uint Colours;
}

internal static unsafe partial class Native
{
    public const uint WmGetIcon = 0x007F;
    public const int IconSmall = 0;
    public const int IconBig = 1;
    public const int GclpHIcon = -14;
    public const int GclpHIconSm = -34;
    public const uint SmtoAbortIfHung = 0x0002;
    public const uint ProcessQueryLimitedInformation = 0x1000;
    public const uint DiMask = 0x0001;
    public const uint DiNormal = 0x0003;
    public const nint IdiApplication = 32512;
    public const uint ImageIcon = 1;
    public const uint LrShared = 0x8000;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    public static partial nint SendMessageTimeout(nint window, uint message, nuint wParam, nint lParam, uint flags, uint timeoutMs, out nuint result);

    [LibraryImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    public static partial nuint GetClassLongPtr(nint window, int index);

    [LibraryImport("user32.dll", EntryPoint = "LoadImageW")]
    public static partial nint LoadImage(nint instance, nint name, uint type, int width, int height, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DrawIconEx(nint dc, int x, int y, nint icon, int width, int height, uint step, nint brush, uint flags);

    [LibraryImport("shell32.dll", EntryPoint = "SHDefExtractIconW", StringMarshalling = StringMarshalling.Utf16)]
    public static partial int SHDefExtractIcon(string file, int index, uint flags, out nint large, out nint small, uint sizes);

    [LibraryImport("kernel32.dll")]
    public static partial nint OpenProcess(uint access, int inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool QueryFullProcessImageName(nint process, uint flags, char* name, ref uint size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateCompatibleDC(nint dc);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteDC(nint dc);

    [LibraryImport("gdi32.dll")]
    public static partial nint CreateDIBSection(nint dc, BitmapInfoHeader* info, uint usage, void** bits, nint section, uint offset);

    [LibraryImport("gdi32.dll")]
    public static partial nint SelectObject(nint dc, nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeleteObject(nint item);

    [LibraryImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GdiFlush();
}

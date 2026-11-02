using System.Runtime.InteropServices;
using Microsoft.Win32;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// The wallpaper Windows is set to: <c>SPI_GETDESKWALLPAPER</c> and the <c>WallpaperStyle</c> and
/// <c>TileWallpaper</c> values under <c>HKCU\Control Panel\Desktop</c>. Under the test hook it reads the hook
/// file <c>system-wallpaper</c> instead (the path on the first line, the fit name on the second), so a test never
/// depends on the user's own wallpaper.
/// </summary>
public static class SystemWallpaper
{
    public const string HookFile = "system-wallpaper";

    /// <summary>The image path, or null when there is none, with its fit.</summary>
    public static (string? Path, WallpaperFit Fit) Read(string? hookDirectory)
    {
        if (hookDirectory is not null)
        {
            var file = Path.Combine(hookDirectory, HookFile);
            var lines = File.Exists(file) ? File.ReadAllLines(file) : [];
            var path = lines.Length > 0 && lines[0].Trim() is { Length: > 0 } p ? p : null;
            var fit = lines.Length > 1 && Documents.TryParseFit(lines[1].Trim(), out var f) ? f : WallpaperFit.Fill;
            return (path, fit);
        }

        var buffer = new char[MaxPath];
        var current = SystemParametersInfoW(SpiGetDeskWallpaper, (uint)buffer.Length, buffer, 0)
            ? new string(buffer, 0, Array.IndexOf(buffer, '\0') is var end and >= 0 ? end : buffer.Length)
            : "";
        using var desktop = Registry.CurrentUser.OpenSubKey(@"Control Panel\Desktop");
        var style = desktop?.GetValue("WallpaperStyle") as string;
        var tile = desktop?.GetValue("TileWallpaper") as string == "1";
        return (current.Length > 0 && File.Exists(current) ? current : null, FitOf(style, tile));
    }

    /// <summary>
    /// Maps Windows' wallpaper style to a fit: 10 fill, 6 fit, 2 stretch, 0 centre (or tile, shown centred),
    /// 22 span (shown as fill on each monitor). Anything else is fill.
    /// </summary>
    public static WallpaperFit FitOf(string? style, bool tile) => (style?.Trim(), tile) switch
    {
        (_, true) => WallpaperFit.Center,
        ("6", _) => WallpaperFit.Fit,
        ("2", _) => WallpaperFit.Stretch,
        ("0", _) => WallpaperFit.Center,
        _ => WallpaperFit.Fill,
    };

    private const uint SpiGetDeskWallpaper = 0x73;
    private const int MaxPath = 260;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool SystemParametersInfoW(uint action, uint param, char[] buffer, uint winIni);
}

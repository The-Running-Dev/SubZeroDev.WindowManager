using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Theming;

/// <summary>Where a resolved icon came from: the system icon, the host default, or nothing (a named icon the theme maps).</summary>
public enum IconOrigin { None, Extracted, Default }

public sealed record ResolvedIcon(ImageSource? Image, IconOrigin Source);

/// <summary>
/// Turns <c>window</c> and <c>app</c> icon references into image sources. Extraction runs off the UI
/// thread through <see cref="IIconService"/>; the bitmap is made on the UI thread, where this is called
/// and where it returns. A reference whose icon cannot be extracted gets the system default application
/// icon. Extracted icons are cached per reference and size (the newest 256); a miss is not cached, so a
/// window that gains its icon later shows it on the next render.
/// </summary>
public sealed class IconResolver(IIconService icons)
{
    private const int CacheLimit = 256;
    private readonly Dictionary<string, ImageSource> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<int, ImageSource> _defaults = [];

    public async Task<ResolvedIcon> ResolveAsync(IconRef reference, int sizePx, CancellationToken ct)
    {
        if (reference is not (IconRef.Window or IconRef.App))
        {
            return new ResolvedIcon(null, IconOrigin.None);
        }

        var key = $"{reference}|{sizePx}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return new ResolvedIcon(cached, IconOrigin.Extracted);
        }

        var extracted = await icons.ExtractAsync(reference, sizePx, ct);
        if (extracted.Ok)
        {
            var image = ToBitmap(extracted.Value!);
            _cache[key] = image;
            _order.Enqueue(key);
            while (_order.Count > CacheLimit)
            {
                _cache.Remove(_order.Dequeue());
            }

            return new ResolvedIcon(image, IconOrigin.Extracted);
        }

        if (!_defaults.TryGetValue(sizePx, out var fallback))
        {
            var system = await icons.DefaultAsync(sizePx, ct);
            if (!system.Ok)
            {
                return new ResolvedIcon(null, IconOrigin.Default);
            }

            fallback = _defaults[sizePx] = ToBitmap(system.Value!);
        }

        return new ResolvedIcon(fallback, IconOrigin.Default);
    }

    private static WriteableBitmap ToBitmap(IconImage icon)
    {
        var bitmap = new WriteableBitmap(icon.Width, icon.Height);
        using (var stream = bitmap.PixelBuffer.AsStream())
        {
            stream.Write(icon.Bgra32.Span);
        }

        bitmap.Invalidate();
        return bitmap;
    }
}

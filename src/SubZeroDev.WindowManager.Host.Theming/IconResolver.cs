using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Theming;

/// <summary>
/// A resolved icon and where it came from: <c>pack:id</c>, <c>theme:id</c> or <c>host:id</c> for an
/// asset-pack image, <c>extracted</c> for the system icon, <c>default</c> for the default application
/// icon, or <c>none</c>.
/// </summary>
public sealed record ResolvedIcon(ImageSource? Image, string Source)
{
    public static readonly ResolvedIcon None = new(null, "none");
}

/// <summary>
/// Turns icon references into image sources through the asset-pack chain (S13): a <c>named</c> id is
/// the first pack image of that id; a <c>window</c> or <c>app</c> ref is the first matching
/// <c>appIcons</c> entry, then the extracted system icon, then the default application icon. File work
/// and extraction run off the UI thread; images are made on the UI thread, where this is called and
/// where it returns. Results are cached per reference and size (the newest 256), and the cache empties
/// when the chain changes; an extraction miss is not cached, so a window that gains its icon later
/// shows it on the next render. A <c>tray</c> ref is the notification icon's own image, drawn by
/// <paramref name="tray"/>; its version is part of the cache key, so a new image is drawn again.
/// </summary>
public sealed class IconResolver(IIconService icons, Func<AssetPackChain>? chain = null, Func<string, int, IconImage?>? tray = null)
{
    private const int CacheLimit = 256;
    private readonly Dictionary<string, ResolvedIcon> _cache = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly Dictionary<int, ImageSource> _defaults = [];
    private readonly Dictionary<string, ImageSource> _files = new(StringComparer.OrdinalIgnoreCase);
    private AssetPackChain? _chain;

    public async Task<ResolvedIcon> ResolveAsync(IconRef reference, int sizePx, CancellationToken ct)
    {
        var current = chain?.Invoke() ?? AssetPackChain.Empty;
        if (!ReferenceEquals(current, _chain))
        {
            _chain = current;
            _cache.Clear();
            _order.Clear();
            _files.Clear();
        }

        var key = $"{reference}|{sizePx}";
        if (_cache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var resolved = await ResolveUncachedAsync(current, reference, sizePx, ct);
        if (resolved.Source != "default" && resolved.Image is not null && ReferenceEquals(current, _chain))
        {
            _cache[key] = resolved;
            _order.Enqueue(key);
            while (_order.Count > CacheLimit)
            {
                _cache.Remove(_order.Dequeue());
            }
        }

        return resolved;
    }

    private async Task<ResolvedIcon> ResolveUncachedAsync(AssetPackChain current, IconRef reference, int sizePx, CancellationToken ct)
    {
        switch (reference)
        {
            case IconRef.Named named:
                return current.FindNamed(named.Id) is { } image ? new ResolvedIcon(Load(image.Path), image.Pack.Label) : ResolvedIcon.None;

            case IconRef.Window or IconRef.App:
                var match = current.Packs.Any(p => p.AppIcons.Count > 0)
                    ? await Task.Run(() => reference switch
                    {
                        IconRef.Window window => current.FindApp(icons.ExecutableOf(window.Hwnd), null),
                        IconRef.App app => current.FindApp(app.Exe, app.Aumid),
                        _ => null,
                    }, ct)
                    : null;
                if (match is not null)
                {
                    return new ResolvedIcon(Load(match.Path), match.Pack.Label);
                }

                var extracted = await icons.ExtractAsync(reference, sizePx, ct);
                if (extracted.Ok)
                {
                    return new ResolvedIcon(ToBitmap(extracted.Value!), "extracted");
                }

                if (!_defaults.TryGetValue(sizePx, out var fallback))
                {
                    var system = await icons.DefaultAsync(sizePx, ct);
                    if (!system.Ok)
                    {
                        return new ResolvedIcon(null, "default");
                    }

                    fallback = _defaults[sizePx] = ToBitmap(system.Value!);
                }

                return new ResolvedIcon(fallback, "default");

            case IconRef.Tray trayIcon when tray is not null:
                var drawn = await Task.Run(() => tray(trayIcon.Key, sizePx), ct);
                return drawn is null ? ResolvedIcon.None : new ResolvedIcon(ToBitmap(drawn), "tray");

            default:
                return ResolvedIcon.None;
        }
    }

    /// <summary>A pack image; PNG decodes through <see cref="BitmapImage"/>, SVG through <see cref="SvgImageSource"/>.</summary>
    private ImageSource Load(string path)
    {
        if (!_files.TryGetValue(path, out var image))
        {
            var uri = new Uri(path);
            image = _files[path] = path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)
                ? new SvgImageSource(uri)
                : new BitmapImage(uri);
        }

        return image;
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

using System.Globalization;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Platform;

/// <summary>
/// Registers DWM thumbnails of tracked windows. The source is checked against the model's fingerprint before it is
/// registered, so a stale key registers nothing (I41); disposing a registration unregisters it once (I36).
/// </summary>
public sealed class Win32ThumbnailService : IThumbnailService
{
    private readonly Func<nint, WindowFingerprint?> _live;
    private readonly Func<WindowKey, WindowFingerprint?> _fingerprintOf;

    /// <param name="facts">Reads the source window's fingerprint now.</param>
    /// <param name="fingerprintOf">The fingerprint the model holds for a key, or null when the key is not tracked.</param>
    public Win32ThumbnailService(IWindowFacts facts, Func<WindowKey, WindowFingerprint?> fingerprintOf)
    {
        _live = facts is Win32WindowFacts win32 ? win32.Fingerprint : handle => facts.Read(handle)?.Fingerprint;
        _fingerprintOf = fingerprintOf;
    }

    public Outcome<IThumbnailRegistration> Register(WindowKey source, nint destinationWindow)
    {
        if (source.Handle == 0 || _fingerprintOf(source) is not { } expected || _live(source.Handle) != expected)
        {
            return Outcome<IThumbnailRegistration>.Fail(CoreErrorCode.WindowGone, "The window is gone.");
        }

        var result = DesktopNative.DwmRegisterThumbnail(destinationWindow, source.Handle, out var thumbnail);
        if (result != 0 || thumbnail == 0)
        {
            return Outcome<IThumbnailRegistration>.Fail(
                CoreErrorCode.CommandFailed,
                string.Create(CultureInfo.InvariantCulture, $"DWM refused the thumbnail (0x{result:X8})."),
                retryable: true);
        }

        return Outcome<IThumbnailRegistration>.Success(new Win32ThumbnailRegistration(source.Handle, thumbnail));
    }
}

/// <summary>One registered DWM thumbnail. Every member is cheap and safe from any thread.</summary>
internal sealed class Win32ThumbnailRegistration(nint source, nint thumbnail) : IThumbnailRegistration
{
    private const uint Flags = DesktopNative.DwmTnpRectDestination | DesktopNative.DwmTnpVisible
        | DesktopNative.DwmTnpOpacity | DesktopNative.DwmTnpSourceClientAreaOnly;

    private readonly Lock _gate = new();
    private nint _thumbnail = thumbnail;

    /// <summary>Places the thumbnail, in the destination window's client pixels; DWM clips it to that client area.</summary>
    public void SetBounds(RectInt bounds, bool visible)
    {
        var properties = new DwmThumbnailProperties
        {
            Flags = Flags,
            Destination = new Rect { Left = bounds.Left, Top = bounds.Top, Right = bounds.Right, Bottom = bounds.Bottom },
            Opacity = 255,
            Visible = visible ? 1 : 0,

            // The whole window, frame and title included, as the taskbar's previews show it.
            SourceClientAreaOnly = 0,
        };

        lock (_gate)
        {
            if (_thumbnail != 0)
            {
                _ = DesktopNative.DwmUpdateThumbnailProperties(_thumbnail, properties);
            }
        }
    }

    public bool SourceAvailable
    {
        get
        {
            lock (_gate)
            {
                if (_thumbnail == 0 || !DesktopNative.IsWindowVisible(source) || DesktopNative.IsIconic(source))
                {
                    return false;
                }

                if (DesktopNative.DwmGetWindowAttribute(source, DesktopNative.DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0)
                {
                    return false;
                }

                return DesktopNative.DwmQueryThumbnailSourceSize(_thumbnail, out var size) == 0 && size.Width > 0 && size.Height > 0;
            }
        }
    }

    public void Dispose()
    {
        nint registered;
        lock (_gate)
        {
            registered = _thumbnail;
            _thumbnail = 0;
        }

        if (registered != 0)
        {
            _ = DesktopNative.DwmUnregisterThumbnail(registered);
        }
    }
}

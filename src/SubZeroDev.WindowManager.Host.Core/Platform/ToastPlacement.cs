namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>Where a toast stack of a given size sits on a monitor's work area (the <c>toastCorner</c> setting).</summary>
public static class ToastPlacement
{
    /// <summary>
    /// The rectangle of a <paramref name="width"/> by <paramref name="height"/> window in <paramref name="corner"/> of
    /// <paramref name="workArea"/>, <paramref name="margin"/> pixels in from both edges. A window larger than the work
    /// area is pinned to its top-left so it is never placed off the monitor.
    /// </summary>
    public static RectInt Place(RectInt workArea, int width, int height, ToastCorner corner, int margin)
    {
        var right = corner is ToastCorner.TopRight or ToastCorner.BottomRight;
        var bottom = corner is ToastCorner.BottomLeft or ToastCorner.BottomRight;
        var left = right ? workArea.Right - margin - width : workArea.Left + margin;
        var top = bottom ? workArea.Bottom - margin - height : workArea.Top + margin;
        left = Math.Max(left, workArea.Left);
        top = Math.Max(top, workArea.Top);
        return new RectInt(left, top, left + width, top + height);
    }
}

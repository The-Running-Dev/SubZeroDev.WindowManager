using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using SubZeroDev.WindowManager.Host.Core;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>
/// One monitor's piece of the desktop floor: a borderless window covering the whole monitor at the very bottom
/// of the z-order, drawing the wallpaper. It never activates, never appears in window lists and refuses to
/// close unless the host closes it.
/// </summary>
public sealed class WallpaperWindow
{
    private readonly Window _window;
    private readonly Image _image = new();
    private bool _closeRequested;

    public WallpaperWindow(MonitorInfo monitor)
    {
        Monitor = monitor;
        _window = new Window
        {
            Title = "SubZeroDev.WindowManager desktop",
            Content = new Grid { Background = new SolidColorBrush(Microsoft.UI.Colors.Black), Children = { _image } },
        };
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = false;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Closing += (_, e) => e.Cancel = !_closeRequested;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetWindowLongPtrW(Handle, GwlExStyle, GetWindowLongPtrW(Handle, GwlExStyle) | WsExToolWindow | WsExNoActivate);
        PanelWindow.Subclass(Handle, ZPin.Floor);
        // As for panels, a move onto a monitor with another scale rescales the window as it lands: size it twice.
        for (var pass = 0; pass < 2; pass++)
        {
            SetWindowPos(Handle, HwndBottom, monitor.Bounds.Left, monitor.Bounds.Top, monitor.Bounds.Width, monitor.Bounds.Height,
                SwpNoActivate);
        }
    }

    public MonitorInfo Monitor { get; }

    public nint Handle { get; }

    /// <summary>The image shown, or null for plain black.</summary>
    public string? ImagePath { get; private set; }

    public WallpaperFit Fit { get; private set; } = WallpaperFit.Fill;

    public RectInt Bounds
    {
        get
        {
            var p = _window.AppWindow.Position;
            var s = _window.AppWindow.Size;
            return new RectInt(p.X, p.Y, p.X + s.Width, p.Y + s.Height);
        }
    }

    /// <summary>Shows an image with a fit, or plain black for a null path. A file that cannot be decoded shows black.</summary>
    public void SetWallpaper(string? path, WallpaperFit fit)
    {
        ImagePath = path;
        Fit = fit;
        _image.Stretch = fit switch
        {
            WallpaperFit.Fit => Stretch.Uniform,
            WallpaperFit.Stretch => Stretch.Fill,
            WallpaperFit.Center => Stretch.None,
            _ => Stretch.UniformToFill,
        };
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        _image.Source = path is null ? null : new BitmapImage(new Uri(path));
    }

    public void Show() => _window.AppWindow.Show(activateWindow: false);

    public void Close()
    {
        _closeRequested = true;
        _window.Close();
    }

    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x80;
    private const nint WsExNoActivate = 0x08000000;
    private const uint SwpNoActivate = 0x10;
    private static readonly nint HwndBottom = 1;

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);
}

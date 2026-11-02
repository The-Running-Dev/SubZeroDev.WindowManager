using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>
/// One panel: a borderless WinUI window that never appears in Alt+Tab, holding a <see cref="WmPanel"/>.
/// It is always on top, except a <c>desktop</c> panel, which is pinned to the bottom of the z-order
/// (every z-order change is turned into "bottom"). The surface manager decides where it goes; this class
/// only shows it there.
/// </summary>
public sealed unsafe class PanelWindow
{
    private static readonly ConcurrentDictionary<nint, (nint Previous, bool Pinned)> Subclassed = new();

    private readonly Window _window;
    private readonly OverlappedPresenter _presenter;
    private bool _closeRequested;

    public PanelWindow(PanelDefinition definition, MonitorInfo monitor, PanelEdge edge, WmPanel panel)
    {
        Definition = definition;
        Monitor = monitor;
        Edge = edge;
        Panel = panel;
        ThicknessPx = (int)Math.Round(definition.Thickness * monitor.Dpi / 96.0, MidpointRounding.AwayFromZero);

        _window = new Window { Title = "SubZeroDev.WindowManager panel", Content = panel };
        _presenter = OverlappedPresenter.Create();
        _presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        _presenter.IsResizable = false;
        _presenter.IsMaximizable = false;
        _presenter.IsMinimizable = false;
        // An auto-hide panel collapses to a strip a couple of pixels thick.
        _presenter.PreferredMinimumHeight = 0;
        _presenter.PreferredMinimumWidth = 0;
        WantsTopmost = definition.Behaviour != PanelBehaviour.Desktop;
        _presenter.IsAlwaysOnTop = WantsTopmost;
        _window.AppWindow.SetPresenter(_presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        // Panels are shell surfaces: Alt+F4 and WM_CLOSE from elsewhere do not close them.
        _window.AppWindow.Closing += (_, e) => e.Cancel = !_closeRequested;
        _window.Closed += (_, _) => Closed?.Invoke(this, EventArgs.Empty);
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        // A tool window is left out of window lists, so a panel never lists itself or takes "active" from the app.
        SetWindowLongPtrW(Handle, GwlExStyle, GetWindowLongPtrW(Handle, GwlExStyle) | WsExToolWindow);
        Subclass(pinned: definition.Behaviour == PanelBehaviour.Desktop);
    }

    private const int GwlExStyle = -20;
    private const int GwlpWndProc = -4;
    private const nint WsExToolWindow = 0x80;
    private const nint WsExTopmost = 0x8;
    private const uint WmWindowPosChanging = 0x46;
    private const uint WmDestroy = 0x2;
    private const uint SwpNoSize = 0x1;
    private const uint SwpNoMove = 0x2;
    private const uint SwpNoZOrder = 0x4;
    private const uint SwpNoActivate = 0x10;
    private static readonly nint HwndBottom = 1;
    private static readonly nint HwndTopmost = -1;
    private static readonly nint HwndNoTopmost = -2;

    /// <summary>Raised while the window closes, before its handle is destroyed.</summary>
    public event EventHandler? Closed;

    public PanelDefinition Definition { get; }

    public MonitorInfo Monitor { get; }

    public PanelEdge Edge { get; }

    public WmPanel Panel { get; }

    public nint Handle { get; }

    public int ThicknessPx { get; }

    /// <summary>The rectangle the shell granted, when the panel reserves space.</summary>
    public RectInt? AppBarRect { get; set; }

    /// <summary>Why a reserve panel fell back to overlay (a <c>CoreErrorCode</c> name), or null.</summary>
    public string? ReserveError { get; set; }

    /// <summary>Where the panel sits when shown in full (for an auto-hide panel, when expanded).</summary>
    public RectInt Home { get; set; }

    /// <summary>An auto-hide panel that is down to its strip.</summary>
    public bool Collapsed { get; set; }

    /// <summary>Whether the window has <c>WS_EX_TOPMOST</c> right now.</summary>
    public bool IsTopmost => (GetWindowLongPtrW(Handle, GwlExStyle) & WsExTopmost) != 0;

    /// <summary>Whether the panel should be topmost: false for a desktop panel and while its monitor is full-screen.</summary>
    public bool WantsTopmost { get; private set; }

    /// <summary>Puts the panel above or among ordinary windows; a desktop panel is never topmost.</summary>
    public void SetTopmost(bool topmost)
    {
        WantsTopmost = topmost && Definition.Behaviour != PanelBehaviour.Desktop;
        _presenter.IsAlwaysOnTop = WantsTopmost;
        ApplyTopmost();
    }

    public RectInt Bounds
    {
        get
        {
            var p = _window.AppWindow.Position;
            var s = _window.AppWindow.Size;
            return new RectInt(p.X, p.Y, p.X + s.Width, p.Y + s.Height);
        }
    }

    /// <summary>
    /// Moves the window in physical pixels. A move onto a monitor with another scale makes Windows rescale
    /// the window as it lands, so the size is applied a second time once it is there.
    /// </summary>
    public void MoveTo(RectInt rect)
    {
        for (var pass = 0; pass < 2; pass++)
        {
            SetWindowPos(Handle, 0, rect.Left, rect.Top, rect.Width, rect.Height, SwpNoZOrder | SwpNoActivate);
        }
    }

    /// <summary>
    /// Shows the panel without taking focus. The presenter's always-on-top flag set before the first show does
    /// not always reach the window, so the flag is applied directly once shown, and again once the queue is
    /// idle because the presenter can apply its own state after the show returns.
    /// </summary>
    public void Show()
    {
        _window.AppWindow.Show(activateWindow: false);
        ApplyTopmost();
        _window.DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, ApplyTopmost);
    }

    /// <summary>Makes <c>WS_EX_TOPMOST</c> match <see cref="WantsTopmost"/>.</summary>
    public void ApplyTopmost()
    {
        if (!_closeRequested && WantsTopmost != IsTopmost)
        {
            SetWindowPos(Handle, WantsTopmost ? HwndTopmost : HwndNoTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    public void Close()
    {
        _closeRequested = true;
        _window.Close();
    }

    /// <summary>
    /// Applies the theme's backdrop. <c>transparent</c> needs a custom composition backdrop that S1
    /// does not build, so it shows as <see cref="PanelBackdrop.None"/>.
    /// </summary>
    public void SetBackdrop(PanelBackdrop backdrop) => _window.SystemBackdrop = backdrop switch
    {
        PanelBackdrop.Mica => new MicaBackdrop(),
        PanelBackdrop.MicaAlt => new MicaBackdrop { Kind = MicaKind.BaseAlt },
        PanelBackdrop.Acrylic => new DesktopAcrylicBackdrop(),
        _ => null,
    };

    /// <summary>
    /// Takes over the window procedure so the window can be any size (Windows otherwise keeps it a few pixels
    /// tall at least, too thick for an auto-hide strip). A pinned window is also sent to the bottom and kept
    /// there by rewriting every z-order change it is asked to make.
    /// </summary>
    private void Subclass(bool pinned)
    {
        delegate* unmanaged<nint, uint, nuint, nint, nint> procedure = &PanelProcedure;
        Subclassed[Handle] = (GetWindowLongPtrW(Handle, GwlpWndProc), pinned);
        SetWindowLongPtrW(Handle, GwlpWndProc, (nint)procedure);
        if (pinned)
        {
            SetWindowPos(Handle, HwndBottom, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);
        }
    }

    [UnmanagedCallersOnly]
    private static nint PanelProcedure(nint hwnd, uint message, nuint wParam, nint lParam)
    {
        if (!Subclassed.TryGetValue(hwnd, out var subclass))
        {
            return 0;
        }

        if (message == WmWindowPosChanging && lParam != 0)
        {
            var position = (WindowPos*)lParam;
            if (subclass.Pinned && (position->Flags & SwpNoZOrder) == 0)
            {
                position->InsertAfter = HwndBottom;
            }

            // The size asked for is the size given: nothing below may hold the window at a minimum height.
            var (width, height) = (position->Width, position->Height);
            var result = CallWindowProcW(subclass.Previous, hwnd, message, wParam, lParam);
            if ((position->Flags & SwpNoSize) == 0)
            {
                position->Width = width;
                position->Height = height;
            }

            return result;
        }

        if (message == WmDestroy)
        {
            Subclassed.TryRemove(hwnd, out _);
            SetWindowLongPtrW(hwnd, GwlpWndProc, subclass.Previous);
        }

        return CallWindowProcW(subclass.Previous, hwnd, message, wParam, lParam);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowPos
    {
        public nint Window;
        public nint InsertAfter;
        public int X;
        public int Y;
        public int Width;
        public int Height;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    private static extern nint CallWindowProcW(nint previous, nint hwnd, uint message, nuint wParam, nint lParam);
}

using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>
/// One panel: a borderless, always-on-top WinUI window that never appears in Alt+Tab, holding a
/// <see cref="WmPanel"/>. The surface manager decides where it goes; this class only shows it there.
/// </summary>
public sealed class PanelWindow
{
    private readonly Window _window;

    public PanelWindow(PanelDefinition definition, MonitorInfo monitor, PanelEdge edge, WmPanel panel)
    {
        Definition = definition;
        Monitor = monitor;
        Edge = edge;
        Panel = panel;
        ThicknessPx = (int)Math.Round(definition.Thickness * monitor.Dpi / 96.0, MidpointRounding.AwayFromZero);

        _window = new Window { Title = "SubZeroDev.WindowManager panel", Content = panel };
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
    }

    public PanelDefinition Definition { get; }

    public MonitorInfo Monitor { get; }

    public PanelEdge Edge { get; }

    public WmPanel Panel { get; }

    public nint Handle { get; }

    public int ThicknessPx { get; }

    /// <summary>The rectangle the shell granted, when the panel reserves space.</summary>
    public RectInt? AppBarRect { get; set; }

    public RectInt Bounds
    {
        get
        {
            var p = _window.AppWindow.Position;
            var s = _window.AppWindow.Size;
            return new RectInt(p.X, p.Y, p.X + s.Width, p.Y + s.Height);
        }
    }

    public void MoveTo(RectInt rect) =>
        _window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(rect.Left, rect.Top, rect.Width, rect.Height));

    public void Show() => _window.AppWindow.Show(activateWindow: false);

    public void Close() => _window.Close();

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
}

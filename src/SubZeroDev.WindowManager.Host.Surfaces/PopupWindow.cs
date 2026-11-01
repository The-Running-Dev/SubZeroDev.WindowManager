using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>
/// One plugin popup: a borderless, always-on-top tool window holding a <see cref="WmPopup"/> around the
/// popup's view. It is created once and hidden between uses, so opening it again is cheap. Its width
/// comes from the <c>Wm.Popup</c> style (480 DIP when the style sets none) and its height follows the
/// view. Losing activation, Escape and Alt+F4 raise <see cref="Dismissed"/>; the surface manager decides
/// what closing means. Call on the UI thread.
/// </summary>
public sealed class PopupWindow
{
    private const double DefaultWidth = 480;
    private const double MinimumHeight = 40;
    private const int MaxFocusRetries = 20;

    private readonly Window _window;
    private MonitorInfo? _monitor;
    private Func<int, int, RectInt>? _place;
    private bool _active;
    private bool _closeRequested;
    private bool _focusPending;
    private int _focusRetries;

    public PopupWindow(string pluginId, string popupId, ContributionView view)
    {
        PluginId = pluginId;
        PopupId = popupId;
        View = view;
        Frame = new WmPopup { Content = view.Root, VerticalAlignment = VerticalAlignment.Top };
        Frame.SizeChanged += (_, _) => Fit();
        Frame.PreviewKeyDown += OnPreviewKeyDown;

        _window = new Window { Title = "SubZeroDev.WindowManager popup", Content = new Grid { Children = { Frame } } };
        var presenter = OverlappedPresenter.Create();
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        presenter.IsResizable = false;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsAlwaysOnTop = true;
        _window.AppWindow.SetPresenter(presenter);
        _window.AppWindow.IsShownInSwitchers = false;
        _window.AppWindow.Closing += (_, e) =>
        {
            if (_closeRequested)
            {
                return;
            }

            // Alt+F4 dismisses like Escape; the window itself lives on.
            e.Cancel = true;
            Dismissed?.Invoke(this, PopupCloseReason.Escape);
        };
        _window.Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated)
            {
                _active = true;
                TryFocus();
                return;
            }

            if (_active && IsOpen)
            {
                _active = false;
                Dismissed?.Invoke(this, PopupCloseReason.LightDismiss);
            }
        };
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        SetWindowLongPtrW(Handle, GwlExStyle, GetWindowLongPtrW(Handle, GwlExStyle) | WsExToolWindow);
    }

    private const int GwlExStyle = -20;
    private const nint WsExToolWindow = 0x80;

    /// <summary>Raised when the user dismisses the popup: by clicking elsewhere, Escape or Alt+F4.</summary>
    public event EventHandler<PopupCloseReason>? Dismissed;

    public string PluginId { get; }

    public string PopupId { get; }

    public ContributionView View { get; }

    public WmPopup Frame { get; }

    public nint Handle { get; }

    public bool IsOpen { get; private set; }

    /// <summary>When the popup last took keyboard focus, in Unix milliseconds; for timing checks.</summary>
    public long ReadyAtUtcMs { get; private set; }

    /// <summary>The reason the owner was last told the popup closed, or null before the first close.</summary>
    public PopupCloseReason? LastCloseReason { get; internal set; }

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
    /// Shows the popup on <paramref name="monitor"/> where <paramref name="place"/> puts a window of the
    /// given pixel width and height, and activates it. The placement is asked again whenever the view's
    /// height changes, so a popup anchored above an element grows upwards.
    /// </summary>
    public void Open(MonitorInfo monitor, Func<int, int, RectInt> place)
    {
        _monitor = monitor;
        _place = place;
        Fit();
        if (!IsOpen)
        {
            IsOpen = true;
            _active = false;
            _window.AppWindow.Show(activateWindow: true);
        }

        _window.Activate();
    }

    /// <summary>
    /// Puts keyboard focus on the first TextInput, or on the first focusable element. Content that is not
    /// loaded yet, or a view that has not arrived, is focused once it is; <see cref="ReadyAtUtcMs"/> is
    /// set when focus lands.
    /// </summary>
    public void FocusContent()
    {
        _focusPending = true;
        _focusRetries = 0;
        TryFocus();
    }

    /// <summary>Retries a pending <see cref="FocusContent"/> after the view rendered again.</summary>
    public void ViewRendered() => TryFocus();

    private void TryFocus()
    {
        if (!_focusPending || !IsOpen)
        {
            return;
        }

        var target = View.TextInputs().FirstOrDefault() as Control ?? FocusManager.FindFirstFocusableElement(Frame) as Control;
        if (target is null)
        {
            return;
        }

        if (!target.IsLoaded)
        {
            target.Loaded += OnTargetLoaded;
            return;
        }

        if (target.Focus(FocusState.Programmatic))
        {
            _focusPending = false;
            ReadyAtUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        }
        else if (_focusRetries++ < MaxFocusRetries)
        {
            _window.DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, TryFocus);
        }
    }

    private void OnTargetLoaded(object sender, RoutedEventArgs e)
    {
        ((FrameworkElement)sender).Loaded -= OnTargetLoaded;
        TryFocus();
    }

    /// <summary>Hides the popup without raising <see cref="Dismissed"/>.</summary>
    public void Hide()
    {
        if (!IsOpen)
        {
            return;
        }

        IsOpen = false;
        _active = false;
        _focusPending = false;
        _window.AppWindow.Hide();
    }

    public void Close()
    {
        IsOpen = false;
        _closeRequested = true;
        _window.Close();
    }

    private void Fit()
    {
        if (_monitor is null || _place is null)
        {
            return;
        }

        var width = double.IsNaN(Frame.Width) ? DefaultWidth : Frame.Width;
        var height = Math.Max(Frame.ActualHeight, MinimumHeight);
        var rect = _place((int)Math.Round(width * _monitor.Scale), (int)Math.Round(height * _monitor.Scale));
        _window.AppWindow.MoveAndResize(new Windows.Graphics.RectInt32(rect.Left, rect.Top, rect.Width, rect.Height));
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Escape)
        {
            e.Handled = true;
            Dismissed?.Invoke(this, PopupCloseReason.Escape);
        }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint GetWindowLongPtrW(nint hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern nint SetWindowLongPtrW(nint hwnd, int index, nint value);
}

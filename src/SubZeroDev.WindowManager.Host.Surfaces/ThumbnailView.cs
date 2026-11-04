using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>
/// What a <see cref="ContributionView"/> needs for the elements that reach past the plugin: live window
/// thumbnails and hover popups. Every member is optional; without them a Thumbnail shows its card and a
/// hoverPopup does nothing.
/// </summary>
/// <param name="Thumbnails">Registers DWM thumbnails.</param>
/// <param name="Windows">Resolves <c>windowKey</c> references.</param>
/// <param name="Log">Where a Thumbnail's problems are logged, once per element.</param>
/// <param name="HostWindowOf">The handle of the host window an element is shown in, or 0.</param>
/// <param name="HoverPopup">Opens the view's plugin's popup for a hoverPopup prop.</param>
public sealed record ViewServices(
    IThumbnailService? Thumbnails = null,
    IWindowModel? Windows = null,
    IHostLog? Log = null,
    Func<FrameworkElement, nint>? HostWindowOf = null,
    HoverPopupOpener? HoverPopup = null);

/// <summary>A popup opened by hovering, as its opener sees it: where the pointer is, and when it closed.</summary>
public interface IHoverPopupSurface
{
    event EventHandler? PointerEntered;

    event EventHandler? PointerExited;

    /// <summary>Raised once, when the popup hides for any reason.</summary>
    event EventHandler? Closed;

    void Close();
}

/// <summary>
/// Opens one of the view's plugin's popups anchored to <paramref name="anchor"/> without taking focus;
/// null when it cannot (undeclared, not anchored, already open). <paramref name="itemKey"/> is the key of the list item
/// the element belongs to, which the plugin is told so it can fill the popup for that item.
/// </summary>
public delegate Task<IHoverPopupSurface?> HoverPopupOpener(string popupId, FrameworkElement anchor, string? itemKey);

/// <summary>
/// Keeps one Thumbnail element's DWM registration in step with its window, its size and its place on
/// screen. A registration is made only for a live window, moved and hidden with the element, and
/// released when the window changes or the element unloads or leaves the view (I36). Call on the UI thread.
/// </summary>
internal sealed class ThumbnailBinding
{
    private readonly WmThumbnail _element;
    private readonly ViewServices _services;
    private readonly Func<string, object?> _lookup;
    private readonly Action<nint> _showIcon;
    private readonly string _pluginId;
    private readonly ThumbnailProblemLog _logged = new();
    private string? _key;
    private string? _size;
    private IThumbnailRegistration? _registration;
    private WindowKey? _registered;
    private RectInt? _lastBounds;
    private bool _lastVisible;
    private bool _loaded;
    private bool _viewportEmpty;
    private bool _detached;
    private nint _iconHandle = -1;

    public ThumbnailBinding(WmThumbnail element, ViewServices services, Func<string, object?> lookup, string pluginId, Action<nint> showIcon)
    {
        _element = element;
        _services = services;
        _lookup = lookup;
        _pluginId = pluginId;
        _showIcon = showIcon;
        element.Loaded += OnLoaded;
        element.Unloaded += OnUnloaded;
        element.LayoutUpdated += OnLayoutUpdated;
        element.EffectiveViewportChanged += OnViewportChanged;
        ApplySize();
    }

    /// <summary>"live" while the window is drawn over the element, otherwise "card".</summary>
    public string Mode => _element.Live ? "live" : "card";

    /// <summary>The <c>windowKey</c> the element shows, or null while it shows none.</summary>
    public string? Window => _key;

    public void SetWindow(string? key)
    {
        if (key == _key)
        {
            return;
        }

        _key = key;
        Release();
        Evaluate();
    }

    public void SetSize(string? size)
    {
        _size = size;
        _element.Size = ThumbnailPolicy.SizeSuffix(size).ToLowerInvariant();
        ApplySize();
        UpdateBounds();
    }

    /// <summary>The element left the view: stop listening and release the registration for good.</summary>
    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        _detached = true;
        _element.Loaded -= OnLoaded;
        _element.Unloaded -= OnUnloaded;
        _element.LayoutUpdated -= OnLayoutUpdated;
        _element.EffectiveViewportChanged -= OnViewportChanged;
        Unsubscribe();
        Release();
    }

    private void ApplySize()
    {
        var (width, height) = ThumbnailPolicy.SizeOf(_size, _lookup);
        _element.Width = width;
        _element.Height = height;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _loaded = true;
        Unsubscribe();
        if (_services.Windows is { } windows)
        {
            windows.Changed += OnWindowsChanged;
        }

        Evaluate();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _loaded = false;
        Unsubscribe();
        Release();
    }

    private void Unsubscribe()
    {
        if (_services.Windows is { } windows)
        {
            windows.Changed -= OnWindowsChanged;
        }
    }

    private void OnWindowsChanged(object? sender, WindowSnapshot e)
    {
        // Raised on the UI thread; a view torn down since is ignored.
        if (!_detached && _loaded)
        {
            Evaluate();
        }
    }

    private void OnLayoutUpdated(object? sender, object e) => UpdateBounds();

    private void OnViewportChanged(FrameworkElement sender, EffectiveViewportChangedEventArgs args)
    {
        _viewportEmpty = args.EffectiveViewport.Width <= 0 || args.EffectiveViewport.Height <= 0;
        UpdateBounds();
    }

    private void Evaluate()
    {
        if (_detached)
        {
            return;
        }

        var decision = ThumbnailPolicy.Decide(_key, _services.Windows is { } windows ? windows.Resolve : null, _services.Thumbnails is not null);
        Report(decision.Problem, decision.Detail);
        ShowCard(decision);
        if (!decision.Live || !_loaded)
        {
            Release();
            return;
        }

        var source = decision.Window!.Key;
        if (_registration is null || _registered != source)
        {
            Release();
            var destination = _services.HostWindowOf?.Invoke(_element) ?? 0;
            if (destination == 0)
            {
                Report(ThumbnailProblem.ThumbnailUnavailable, "The element is not shown in a host window.");
                return;
            }

            Outcome<IThumbnailRegistration> registered;
            try
            {
                registered = _services.Thumbnails!.Register(source, destination);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                registered = Outcome<IThumbnailRegistration>.Fail(CoreErrorCode.CommandFailed, e.Message);
            }

            if (!registered.Ok)
            {
                Report(ThumbnailProblem.ThumbnailUnavailable, registered.Error!.Detail);
                return;
            }

            _registration = registered.Value;
            _registered = source;
            _lastBounds = null;
        }

        if (!_registration!.SourceAvailable)
        {
            Report(ThumbnailProblem.ThumbnailUnavailable, "DWM has nothing to draw for the window.");
            Release();
            return;
        }

        _element.Live = true;
        UpdateBounds();
    }

    private void ShowCard(ThumbnailDecision decision)
    {
        _element.Title = decision.Window?.Title ?? string.Empty;
        if (decision.Handle != _iconHandle)
        {
            _iconHandle = decision.Handle;
            _showIcon(decision.Handle);
        }
    }

    /// <summary>Moves the live thumbnail onto the element, in client pixels, or hides it while the element cannot be seen.</summary>
    private void UpdateBounds()
    {
        if (_registration is null || _element.XamlRoot is not { } root)
        {
            return;
        }

        var bounds = default(RectInt);
        var visible = false;
        if (_loaded && VisibleInTree())
        {
            try
            {
                var origin = _element.TransformToVisual(null).TransformPoint(new Windows.Foundation.Point(0, 0));
                var scale = root.RasterizationScale;
                bounds = ThumbnailPolicy.ClientPixels(origin.X, origin.Y, _element.ActualWidth, _element.ActualHeight, scale);
                visible = ThumbnailPolicy.ShouldDraw(_loaded, visibleInTree: true, _viewportEmpty, bounds,
                    (int)Math.Round(root.Size.Width * scale), (int)Math.Round(root.Size.Height * scale));
            }
            catch (ArgumentException)
            {
                // Not in the visual tree any more.
            }
        }

        if (_lastBounds == bounds && _lastVisible == visible)
        {
            return;
        }

        _lastBounds = bounds;
        _lastVisible = visible;
        _registration.SetBounds(bounds, visible);
    }

    private bool VisibleInTree()
    {
        for (DependencyObject? at = _element; at is not null; at = VisualTreeHelper.GetParent(at))
        {
            if (at is UIElement { Visibility: Visibility.Collapsed })
            {
                return false;
            }
        }

        return true;
    }

    private void Release()
    {
        _element.Live = false;
        if (_registration is null)
        {
            return;
        }

        var registration = _registration;
        _registration = null;
        _registered = null;
        _lastBounds = null;
        try
        {
            registration.Dispose();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            _services.Log?.Write(LogLevel.Warning, "surfaces", $"Releasing a thumbnail of {_pluginId}: {e.Message}");
        }
    }

    private void Report(ThumbnailProblem? problem, string detail)
    {
        if (problem is { } found && _logged.First(found))
        {
            _services.Log?.Write(LogLevel.Warning, "surfaces",
                $"{found}: thumbnail '{_element.Name}' of {_pluginId} shows its card: {detail}");
        }
    }
}

/// <summary>
/// Runs one element's hoverPopup: opens the popup after the pointer rests on the element for the delay,
/// and closes it once the pointer has left both the element and the popup (<see cref="HoverPopupState"/>).
/// Call on the UI thread.
/// </summary>
internal sealed class HoverPopupController
{
    private readonly FrameworkElement _element;
    private readonly HoverPopupOpener _open;
    private readonly Func<string?> _itemKey;
    private readonly HoverPopupState _state = new();
    private DispatcherQueueTimer? _timer;
    private IHoverPopupSurface? _surface;
    private string _popupId = string.Empty;
    private TimeSpan _delay = TimeSpan.FromMilliseconds(ThumbnailPolicy.DefaultHoverDelayMs);
    private int _generation;
    private bool _detached;

    public HoverPopupController(FrameworkElement element, HoverPopupOpener open, Func<string?>? itemKey = null)
    {
        _element = element;
        _open = open;
        _itemKey = itemKey ?? (() => null);
        element.PointerEntered += OnEntered;
        element.PointerExited += OnExited;
        element.PointerCanceled += OnExited;
        element.Unloaded += OnUnloaded;
    }

    /// <summary>Sets the popup and delay; null turns the hover off. A change closes a popup it opened.</summary>
    public void Configure((string PopupId, TimeSpan Delay)? hover)
    {
        var (popupId, delay) = hover ?? (string.Empty, TimeSpan.FromMilliseconds(ThumbnailPolicy.DefaultHoverDelayMs));
        if (popupId == _popupId && delay == _delay)
        {
            return;
        }

        CloseNow();
        _popupId = popupId;
        _delay = delay;
    }

    public void Detach()
    {
        if (_detached)
        {
            return;
        }

        CloseNow();
        _detached = true;
        _element.PointerEntered -= OnEntered;
        _element.PointerExited -= OnExited;
        _element.PointerCanceled -= OnExited;
        _element.Unloaded -= OnUnloaded;
    }

    private void OnEntered(object sender, PointerRoutedEventArgs e)
    {
        if (_popupId.Length > 0)
        {
            Run(_state.ElementEntered());
        }
    }

    private void OnExited(object sender, PointerRoutedEventArgs e) => Run(_state.ElementExited());

    private void OnUnloaded(object sender, RoutedEventArgs e) => CloseNow();

    private void Run(HoverAction action)
    {
        if (_detached)
        {
            return;
        }

        switch (action)
        {
            case HoverAction.StartOpenTimer:
                Start(_delay);
                break;
            case HoverAction.StartCloseTimer:
                Start(HoverPopupState.CloseGrace);
                break;
            case HoverAction.StopTimer:
                _timer?.Stop();
                break;
            case HoverAction.Open:
                _ = OpenAsync();
                break;
            case HoverAction.Close:
                if (_surface is { } surface)
                {
                    surface.Close();
                }
                else
                {
                    // Still opening: the open is dropped when it lands.
                    _generation++;
                }

                break;
        }
    }

    private void Start(TimeSpan interval)
    {
        if (_timer is null)
        {
            _timer = _element.DispatcherQueue.CreateTimer();
            _timer.IsRepeating = false;
            _timer.Tick += (_, _) => Run(_state.TimerElapsed());
        }

        _timer.Stop();
        _timer.Interval = interval;
        _timer.Start();
    }

    private async Task OpenAsync()
    {
        var generation = ++_generation;
        IHoverPopupSurface? surface;
        try
        {
            surface = await _open(_popupId, _element, _itemKey());
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            surface = null;
        }

        if (surface is null)
        {
            if (generation == _generation)
            {
                _state.Closed();
            }

            return;
        }

        if (generation != _generation || _detached)
        {
            surface.Close();
            return;
        }

        _surface = surface;
        surface.PointerEntered += OnPopupEntered;
        surface.PointerExited += OnPopupExited;
        surface.Closed += OnPopupClosed;
    }

    private void OnPopupEntered(object? sender, EventArgs e) => Run(_state.PopupEntered());

    private void OnPopupExited(object? sender, EventArgs e) => Run(_state.PopupExited());

    private void OnPopupClosed(object? sender, EventArgs e)
    {
        if (sender is IHoverPopupSurface surface)
        {
            surface.PointerEntered -= OnPopupEntered;
            surface.PointerExited -= OnPopupExited;
            surface.Closed -= OnPopupClosed;
        }

        if (ReferenceEquals(sender, _surface))
        {
            _surface = null;
            _timer?.Stop();
            _state.Closed();
        }
    }

    private void CloseNow()
    {
        _generation++;
        _timer?.Stop();
        var surface = _surface;
        _state.Closed();
        surface?.Close();
    }
}

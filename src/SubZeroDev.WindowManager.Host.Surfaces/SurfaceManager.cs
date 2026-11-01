using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.Surfaces;

public interface ISurfaceManager
{
    Task ApplyLayoutAsync(string layoutId, CancellationToken ct);

    /// <summary>
    /// Shows the plugin's popup at the anchor, activates it and focuses its first TextInput, then tells
    /// the worker (I18 first). Opening an open popup moves it. NotFound when the anchor element is not
    /// shown, Undeclared when the plugin does not declare the popup.
    /// </summary>
    Task<Outcome> OpenPopupAsync(string pluginId, string popupId, PopupAnchor anchor, PopupTrigger trigger, CancellationToken ct);

    /// <summary>Hides the popup and tells the worker why, except for <c>OwnerFaulted</c>. Closing a closed popup succeeds.</summary>
    Task<Outcome> ClosePopupAsync(string pluginId, string popupId, PopupCloseReason reason, CancellationToken ct);

    void FocusPanel(string panelId);
}

/// <summary>
/// Creates one <see cref="PanelWindow"/> per panel in the active layout and places it. Places on the
/// primary monitor only and treats every behaviour other than <c>reserve</c> as an overlay. Each
/// contribution gets a <see cref="ContributionView"/>; the placements go to the contribution host, and
/// its instance changes are rendered on the UI thread. Each plugin popup gets one
/// <see cref="PopupWindow"/>, made on first open and hidden between uses. Call on the UI thread, except
/// <see cref="ServeWorkerAsync"/>.
/// </summary>
public sealed class SurfaceManager : ISurfaceManager
{
    private readonly IConfigStore _config;
    private readonly IMonitorService _monitors;
    private readonly IAppBarService _appBars;
    private readonly IThemeEngine _theme;
    private readonly IHostLog _log;
    private readonly IContributionHost _contributions;
    private readonly DispatcherQueue _dispatcher;
    private readonly Action<string>? _retry;
    private readonly IElementActions? _actions;
    private readonly IconResolver? _icons;
    private readonly IForegroundService? _foreground;
    private readonly List<PanelWindow> _panels = [];
    private readonly Dictionary<Guid, ContributionView> _views = [];
    private readonly Dictionary<(string Plugin, string Popup), PopupWindow> _popups = [];
    private readonly Lock _pendingGate = new();
    private readonly HashSet<Guid> _pending = [];
    private readonly HashSet<(string Plugin, string Popup)> _pendingPopups = [];

    public SurfaceManager(IConfigStore config, IMonitorService monitors, IAppBarService appBars, IThemeEngine theme,
        IContributionHost contributions, IHostLog log, Action<string>? retry = null, IElementActions? actions = null,
        IForegroundService? foreground = null, IconResolver? icons = null)
    {
        _retry = retry;
        _icons = icons;
        _actions = actions;
        _foreground = foreground;
        _config = config;
        _monitors = monitors;
        _appBars = appBars;
        _theme = theme;
        _contributions = contributions;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _theme.AppearanceChanged += (_, _) => Restyle();
        _contributions.InstanceChanged += (_, snapshot) => QueueRender(snapshot.InstanceId);
        _contributions.PopupChanged += (_, snapshot) => QueuePopupRender((snapshot.PluginId, snapshot.PopupId));
        _contributions.PopupOwnerStopped += (_, pluginId) => _dispatcher.TryEnqueue(() => OwnerStopped(pluginId));
    }

    public IReadOnlyList<PanelWindow> Panels => _panels;

    public IReadOnlyCollection<ContributionView> Views => _views.Values;

    public IReadOnlyCollection<PopupWindow> Popups => _popups.Values;

    public Task<Outcome> OpenPopupAsync(string pluginId, string popupId, PopupAnchor anchor, PopupTrigger trigger, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(anchor);
        if ((anchor.Element is null) == (anchor.Monitor is null))
        {
            return Task.FromResult(Outcome.Fail(CoreErrorCode.CommandFailed, "A popup anchor names exactly one of element and monitor."));
        }

        FrameworkElement? element = null;
        if (anchor.Element is { } named)
        {
            element = Guid.TryParse(named.SurfaceId, out var instanceId) && _views.TryGetValue(instanceId, out var view)
                ? view.Find(named.ElementId)
                : null;
            if (element is null)
            {
                return Task.FromResult(Outcome.Fail(CoreErrorCode.NotFound,
                    $"Element '{named.ElementId}' on surface {named.SurfaceId} is not shown."));
            }
        }

        return OpenPopupAsync(pluginId, popupId, element, anchor.Monitor ?? MonitorAnchor.Pointer, trigger, ct);
    }

    /// <summary>Opens a popup anchored to an element shown in a panel, or on a monitor when <paramref name="element"/> is null.</summary>
    internal async Task<Outcome> OpenPopupAsync(string pluginId, string popupId, FrameworkElement? element, MonitorAnchor monitorAnchor,
        PopupTrigger trigger, CancellationToken ct)
    {
        if (!_contributions.DeclaresPopup(pluginId, popupId))
        {
            return Outcome.Fail(CoreErrorCode.Undeclared, $"{pluginId} does not declare popup '{popupId}'.");
        }

        var key = (pluginId, popupId);
        if (!_popups.TryGetValue(key, out var window))
        {
            var view = ContributionView.ForPopup(pluginId, Lookup, _actions,
                (elementId, kind, value, source, itemKey) =>
                    _contributions.SendPopupEventAsync(pluginId, popupId, elementId, kind, value, source, itemKey),
                _icons);
            window = new PopupWindow(pluginId, popupId, view);
            window.Dismissed += (_, reason) => _ = ClosePopupAsync(pluginId, popupId, reason, CancellationToken.None);
            _popups[key] = window;
        }

        window.Frame.Style = Lookup("Wm.Popup") as Style;
        window.View.Render(_contributions.DescribePopup(pluginId, popupId));
        var placed = element is not null ? PlaceBy(element) : PlaceOn(monitorAnchor);
        if (placed is not { } placement)
        {
            return Outcome.Fail(CoreErrorCode.NotFound, "No monitor is connected.");
        }

        window.Open(placement.Monitor, placement.Place);
        if (_foreground is not null && GetForegroundWindow() != window.Handle)
        {
            var activated = await _foreground.ActivateWindowAsync(window.Handle, ct);
            if (!activated.Ok)
            {
                _log.Write(LogLevel.Warning, "surfaces", $"Activating popup {pluginId}/{popupId}: {activated.Error!.Detail}");
            }
        }

        window.FocusContent();
        var told = await _contributions.NotifyPopupOpenedAsync(pluginId, popupId, trigger);
        if (!told.Ok)
        {
            window.Hide();
        }

        return told;
    }

    public async Task<Outcome> ClosePopupAsync(string pluginId, string popupId, PopupCloseReason reason, CancellationToken ct)
    {
        if (!_contributions.DeclaresPopup(pluginId, popupId))
        {
            return Outcome.Fail(CoreErrorCode.Undeclared, $"{pluginId} does not declare popup '{popupId}'.");
        }

        if (!_popups.TryGetValue((pluginId, popupId), out var window) || !window.IsOpen)
        {
            return Outcome.Success;
        }

        window.Hide();
        if (reason == PopupCloseReason.OwnerFaulted)
        {
            return Outcome.Success;
        }

        var told = await _contributions.NotifyPopupClosedAsync(pluginId, popupId, reason);
        if (told.Ok)
        {
            window.LastCloseReason = reason;
        }
        else
        {
            _log.Write(LogLevel.Warning, "surfaces", $"Telling {pluginId} that popup '{popupId}' closed: {told.Error!.Detail}");
        }

        return Outcome.Success;
    }

    /// <summary>Serves <c>popup.open</c> and <c>popup.close</c> from a worker, on the UI thread. Call from any thread.</summary>
    public Task<RpcMessage> ServeWorkerAsync(string pluginId, RpcMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var done = new TaskCompletionSource<RpcMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_dispatcher.TryEnqueue(async () =>
            {
                try
                {
                    done.TrySetResult(await ServeAsync(pluginId, request));
                }
                catch (Exception e)
                {
                    _log.Write(LogLevel.Error, "surfaces", $"Serving {request.Method} for {pluginId} failed: {e}");
                    done.TrySetResult(RpcMessage.Failure(request.Id, WireErrorCodes.CommandFailed, e.Message));
                }
            }))
        {
            done.TrySetResult(RpcMessage.Failure(request.Id, WireErrorCodes.Cancelled, "The host is shutting down."));
        }

        return done.Task;
    }

    private async Task<RpcMessage> ServeAsync(string pluginId, RpcMessage request)
    {
        Outcome outcome;
        try
        {
            if (request.Method == Methods.PopupOpen && request.ParamsAs<PopupOpenParams>() is { Anchor: not null } open
                && !string.IsNullOrEmpty(open.PopupId))
            {
                outcome = await OpenPopupAsync(pluginId, open.PopupId, open.Anchor, PopupTrigger.Command, CancellationToken.None);
            }
            else if (request.Method == Methods.PopupClose && request.ParamsAs<PopupCloseParams>() is { } close
                && !string.IsNullOrEmpty(close.PopupId))
            {
                outcome = await ClosePopupAsync(pluginId, close.PopupId, PopupCloseReason.Requested, CancellationToken.None);
            }
            else
            {
                return RpcMessage.Failure(request.Id, WireErrorCodes.InvalidParams, $"{request.Method} needs a popupId and, to open, an anchor.");
            }
        }
        catch (JsonException e)
        {
            return RpcMessage.Failure(request.Id, WireErrorCodes.InvalidParams, e.Message);
        }

        return outcome.Ok
            ? RpcMessage.Success(request.Id!.Value, null)
            : RpcMessage.Failure(request.Id, WorkerChannel.WireCodeOf(outcome.Error!.Code), outcome.Error.Detail,
                new JsonObject { ["retryable"] = outcome.Error.Retryable });
    }

    /// <summary>
    /// Below the element when it fits on the monitor, otherwise above it, and always above an element in a
    /// bottom panel; kept on the monitor sideways.
    /// </summary>
    private (MonitorInfo Monitor, Func<int, int, RectInt> Place)? PlaceBy(FrameworkElement element)
    {
        var panel = _panels.FirstOrDefault(window => window.Panel.XamlRoot == element.XamlRoot);
        if (panel is null)
        {
            return PlaceOn(MonitorAnchor.Active);
        }

        var scale = element.XamlRoot?.RasterizationScale ?? panel.Monitor.Scale;
        var box = element.TransformToVisual(null).TransformBounds(
            new Windows.Foundation.Rect(0, 0, element.ActualWidth, element.ActualHeight));
        var origin = panel.Bounds;
        var anchor = new RectInt(origin.Left + (int)Math.Round(box.Left * scale), origin.Top + (int)Math.Round(box.Top * scale),
            origin.Left + (int)Math.Round(box.Right * scale), origin.Top + (int)Math.Round(box.Bottom * scale));
        var monitor = panel.Monitor;
        var above = panel.Edge == PanelEdge.Bottom;
        return (monitor, (width, height) =>
        {
            var left = Math.Clamp(anchor.Left, monitor.Bounds.Left, Math.Max(monitor.Bounds.Left, monitor.Bounds.Right - width));
            var top = !above && anchor.Bottom + height <= monitor.Bounds.Bottom
                ? anchor.Bottom
                : anchor.Top - height;
            return new RectInt(left, top, left + width, top + height);
        });
    }

    /// <summary>Centred across the monitor, a fifth of the way down its work area.</summary>
    private (MonitorInfo Monitor, Func<int, int, RectInt> Place)? PlaceOn(MonitorAnchor anchor)
    {
        var monitors = _monitors.Monitors;
        var primary = monitors.FirstOrDefault(m => m.IsPrimary) ?? monitors.FirstOrDefault();
        if (primary is null)
        {
            return null;
        }

        var point = anchor switch
        {
            MonitorAnchor.Pointer when GetCursorPos(out var cursor) => cursor,
            MonitorAnchor.Active when GetWindowRect(GetForegroundWindow(), out var rect) =>
                new NativePoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2),
            _ => (NativePoint?)null,
        };
        var monitor = point is { } p
            ? monitors.FirstOrDefault(m => p.X >= m.Bounds.Left && p.X < m.Bounds.Right && p.Y >= m.Bounds.Top && p.Y < m.Bounds.Bottom) ?? primary
            : primary;
        return (monitor, (width, height) =>
        {
            var area = monitor.WorkArea;
            var left = area.Left + (area.Width - width) / 2;
            var top = area.Top + area.Height / 5;
            return new RectInt(left, top, left + width, top + height);
        });
    }

    private void QueuePopupRender((string Plugin, string Popup) key)
    {
        lock (_pendingGate)
        {
            if (!_pendingPopups.Add(key))
            {
                return;
            }
        }

        if (!_dispatcher.TryEnqueue(() => RenderPopup(key)))
        {
            lock (_pendingGate)
            {
                _pendingPopups.Remove(key);
            }
        }
    }

    private void RenderPopup((string Plugin, string Popup) key)
    {
        lock (_pendingGate)
        {
            _pendingPopups.Remove(key);
        }

        try
        {
            if (_popups.TryGetValue(key, out var window))
            {
                var hadInput = window.View.TextInputs().Count > 0;
                window.View.Render(_contributions.DescribePopup(key.Plugin, key.Popup));
                if (window.IsOpen && !hadInput)
                {
                    window.FocusContent();
                }
                else
                {
                    window.ViewRendered();
                }
            }
        }
        catch (Exception e)
        {
            _log.Write(LogLevel.Error, "surfaces", $"Rendering popup {key.Plugin}/{key.Popup} failed: {e}");
        }
    }

    /// <summary>A stopped or faulted worker's popups close at once; the worker is not told (it is gone).</summary>
    private void OwnerStopped(string pluginId)
    {
        foreach (var window in _popups.Values.Where(window => window.PluginId == pluginId))
        {
            window.Hide();
            window.View.Render((PopupSnapshot?)null);
        }
    }

    public async Task ApplyLayoutAsync(string layoutId, CancellationToken ct)
    {
        var state = _config.Get(new DocumentId(DocumentKind.Layout, layoutId));
        var problems = new List<string>();
        var layout = state is { Health: DocumentHealth.Valid, Content: not null }
            ? Documents.ParseLayout(state.Content, layoutId, problems)
            : null;
        if (layout is null)
        {
            _log.Write(LogLevel.Error, "surfaces", $"Layout '{layoutId}' is {state.Health}: {string.Join("; ", problems)}");
            return;
        }

        await ApplyLayoutAsync(layout, ct);
    }

    public async Task ApplyLayoutAsync(LayoutDocument layout, CancellationToken ct)
    {
        await ShutdownAsync(ct);
        var primary = _monitors.Monitors.FirstOrDefault(m => m.IsPrimary) ?? _monitors.Monitors.FirstOrDefault();
        if (primary is null)
        {
            _log.Write(LogLevel.Error, "surfaces", "No monitor is connected.");
            return;
        }

        var placements = new List<InstancePlacement>();
        foreach (var definition in layout.Panels)
        {
            var edge = definition.Placement.Edge ?? PanelEdge.Bottom;
            var axis = edge is PanelEdge.Left or PanelEdge.Right ? Orientation.Vertical : Orientation.Horizontal;
            var context = new SurfaceContext(primary.StableId, primary.Scale, axis, edge);
            var panel = new WmPanel
            {
                Edge = edge.ToString().ToLowerInvariant(),
                Behaviour = definition.Behaviour.ToString().ToLowerInvariant(),
                Start = BuildZone(definition, "start", context, placements),
                Center = BuildZone(definition, "center", context, placements),
                End = BuildZone(definition, "end", context, placements),
            };

            var window = new PanelWindow(definition, primary, edge, panel);
            window.Closed += (_, _) => ReleaseSpace(window);
            _panels.Add(window);
            Style(window);
            window.SetBackdrop(_theme.CurrentBackdrop);

            if (definition.Behaviour == PanelBehaviour.Reserve)
            {
                var granted = await _appBars.RegisterAsync(window.Handle, primary.StableId, edge, window.ThicknessPx, ct);
                if (granted.Ok)
                {
                    window.AppBarRect = granted.Value;
                    window.MoveTo(granted.Value);
                }
                else
                {
                    window.ReserveError = granted.Error!.Code.ToString();
                    window.Panel.Behaviour = "overlay";
                    _log.Write(LogLevel.Error, "surfaces", $"Panel {definition.Id} could not reserve space, shown as overlay: {granted.Error}");
                    window.MoveTo(OverlayRect(primary.Bounds, edge, window.ThicknessPx));
                }
            }
            else
            {
                window.MoveTo(OverlayRect(primary.WorkArea, edge, window.ThicknessPx));
            }

            window.Show();
        }

        _contributions.SetPlacements(placements);
    }

    public void FocusPanel(string panelId)
    {
        var window = _panels.FirstOrDefault(p => p.Definition.Id.ToString() == panelId);
        if (window is not null)
        {
            _ = FocusAsync(window);
        }
    }

    /// <summary>Activates the panel's window, then puts keyboard focus on its first Button or Toggle.</summary>
    private async Task FocusAsync(PanelWindow window)
    {
        if (_foreground is not null)
        {
            var activated = await _foreground.ActivateWindowAsync(window.Handle, CancellationToken.None).ConfigureAwait(false);
            if (!activated.Ok)
            {
                _log.Write(LogLevel.Warning, "surfaces", $"Focusing panel {window.Definition.Id}: {activated.Error!.Detail}");
            }
        }

        _dispatcher.TryEnqueue(() =>
        {
            if (FocusManager.FindFirstFocusableElement(window.Panel) is Microsoft.UI.Xaml.Controls.Control first)
            {
                first.Focus(FocusState.Keyboard);
            }
        });
    }

    /// <summary>Gives back reserved space and closes every panel.</summary>
    public async Task ShutdownAsync(CancellationToken ct)
    {
        foreach (var window in _panels)
        {
            if (window.AppBarRect is not null)
            {
                await _appBars.RemoveAsync(window.Handle, ct);
                window.AppBarRect = null;
            }

            window.Close();
        }

        foreach (var popup in _popups.Values)
        {
            popup.Close();
        }

        _panels.Clear();
        _views.Clear();
        _popups.Clear();
    }

    /// <summary>A panel closed by any path other than <see cref="ShutdownAsync"/> still gives its space back.</summary>
    private void ReleaseSpace(PanelWindow window)
    {
        if (window.AppBarRect is not null)
        {
            _appBars.RemoveNow(window.Handle);
            window.AppBarRect = null;
        }
    }

    private WmZone? BuildZone(PanelDefinition definition, string key, SurfaceContext context, List<InstancePlacement> placements)
    {
        if (!definition.Zones.TryGetValue(key, out var instances) || instances.Count == 0)
        {
            return null;
        }

        var zone = new WmZone();
        var axis = context.PanelAxis == Orientation.Vertical
            ? Microsoft.UI.Xaml.Controls.Orientation.Vertical
            : Microsoft.UI.Xaml.Controls.Orientation.Horizontal;
        foreach (var instance in instances)
        {
            var view = new ContributionView(instance, axis, Lookup, _retry, _actions, _icons,
                (popupId, anchor, _) => OpenPopupAsync(instance.Plugin, popupId, anchor, MonitorAnchor.Active, PopupTrigger.Element,
                    CancellationToken.None));
            _views[instance.Id] = view;
            view.Render(_contributions.Describe(instance.Id));
            placements.Add(new InstancePlacement(instance, context));
            zone.Items.Add(view.Root);
        }

        if (Lookup("Wm.Zone") is Style zoneStyle)
        {
            zone.Style = zoneStyle;
        }

        return zone;
    }

    private void Restyle()
    {
        foreach (var window in _panels)
        {
            Style(window);
            window.SetBackdrop(_theme.CurrentBackdrop);
            foreach (var zone in new[] { window.Panel.Start, window.Panel.Center, window.Panel.End }.OfType<WmZone>())
            {
                zone.Style = Lookup("Wm.Zone") as Style;
            }
        }

        foreach (var view in _views.Values)
        {
            view.Render(_contributions.Describe(view.Instance.Id), restyle: true);
        }

        foreach (var popup in _popups.Values)
        {
            popup.Frame.Style = Lookup("Wm.Popup") as Style;
            popup.View.Render(_contributions.DescribePopup(popup.PluginId, popup.PopupId), restyle: true);
        }
    }

    /// <summary>Queues one render per instance; changes that arrive before it runs fold into it, so the UI thread never falls behind.</summary>
    private void QueueRender(Guid instanceId)
    {
        lock (_pendingGate)
        {
            if (!_pending.Add(instanceId))
            {
                return;
            }
        }

        if (!_dispatcher.TryEnqueue(() => Render(instanceId)))
        {
            lock (_pendingGate)
            {
                _pending.Remove(instanceId);
            }
        }
    }

    /// <summary>Runs from the dispatcher queue, where an escaped exception would end the host without UnhandledException.</summary>
    private void Render(Guid instanceId)
    {
        lock (_pendingGate)
        {
            _pending.Remove(instanceId);
        }

        try
        {
            if (_views.TryGetValue(instanceId, out var view))
            {
                view.Render(_contributions.Describe(instanceId));
            }
        }
        catch (Exception e)
        {
            _log.Write(LogLevel.Error, "surfaces", $"Rendering instance {instanceId} failed: {e}");
        }
    }

    private static void Style(PanelWindow window)
    {
        var edgeKey = "Wm.Panel." + char.ToUpperInvariant(window.Panel.Edge[0]) + window.Panel.Edge[1..];
        window.Panel.Style = (Lookup(edgeKey) ?? Lookup("Wm.Panel")) as Style;
    }

    private static object? Lookup(string key) =>
        Application.Current.Resources.TryGetValue(key, out var value) ? value : null;

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativePoint(int X, int Y);

    [StructLayout(LayoutKind.Sequential)]
    private readonly record struct NativeRect(int Left, int Top, int Right, int Bottom);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out NativePoint point);

    [DllImport("user32.dll")]
    private static extern nint GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);

    private static RectInt OverlayRect(RectInt area, PanelEdge edge, int thickness) => edge switch
    {
        PanelEdge.Top => area with { Bottom = area.Top + thickness },
        PanelEdge.Left => area with { Right = area.Left + thickness },
        PanelEdge.Right => area with { Left = area.Right - thickness },
        _ => area with { Top = area.Bottom - thickness },
    };
}

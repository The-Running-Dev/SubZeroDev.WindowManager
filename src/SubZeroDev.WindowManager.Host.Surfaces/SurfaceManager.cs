using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>The S1 part of the contract's <c>ISurfaceManager</c>; popups arrive with their slice.</summary>
public interface ISurfaceManager
{
    Task ApplyLayoutAsync(string layoutId, CancellationToken ct);

    void FocusPanel(string panelId);
}

/// <summary>
/// Creates one <see cref="PanelWindow"/> per panel in the active layout and places it. Places on the
/// primary monitor only and treats every behaviour other than <c>reserve</c> as an overlay. Each
/// contribution gets a <see cref="ContributionView"/>; the placements go to the contribution host, and
/// its instance changes are rendered on the UI thread. Call on the UI thread.
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
    private readonly List<PanelWindow> _panels = [];
    private readonly Dictionary<Guid, ContributionView> _views = [];

    public SurfaceManager(IConfigStore config, IMonitorService monitors, IAppBarService appBars, IThemeEngine theme,
        IContributionHost contributions, IHostLog log)
    {
        _config = config;
        _monitors = monitors;
        _appBars = appBars;
        _theme = theme;
        _contributions = contributions;
        _log = log;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        _theme.AppearanceChanged += (_, _) => Restyle();
        _contributions.InstanceChanged += (_, snapshot) => _dispatcher.TryEnqueue(() => Render(snapshot.InstanceId));
    }

    public IReadOnlyList<PanelWindow> Panels => _panels;

    public IReadOnlyCollection<ContributionView> Views => _views.Values;

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
                    _log.Write(LogLevel.Warning, "surfaces", $"Panel {definition.Id} could not reserve space: {granted.Error}");
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
        window?.Panel.Focus(FocusState.Programmatic);
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

        _panels.Clear();
        _views.Clear();
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
            var view = new ContributionView(instance, axis, Lookup);
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
    }

    /// <summary>Runs from the dispatcher queue, where an escaped exception would end the host without UnhandledException.</summary>
    private void Render(Guid instanceId)
    {
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

    private static RectInt OverlayRect(RectInt area, PanelEdge edge, int thickness) => edge switch
    {
        PanelEdge.Top => area with { Bottom = area.Top + thickness },
        PanelEdge.Left => area with { Right = area.Left + thickness },
        PanelEdge.Right => area with { Left = area.Right - thickness },
        _ => area with { Top = area.Bottom - thickness },
    };
}

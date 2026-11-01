using Microsoft.UI.Xaml;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Surfaces;

public enum PlaceholderReason { Starting, Disabled, Incompatible, Invalid, Faulted, Unavailable, NotSupportedInMode, Missing }

/// <summary>The S1 part of the contract's <c>ISurfaceManager</c>; popups arrive with their slice.</summary>
public interface ISurfaceManager
{
    Task ApplyLayoutAsync(string layoutId, CancellationToken ct);

    void FocusPanel(string panelId);
}

/// <summary>
/// Creates one <see cref="PanelWindow"/> per panel in the active layout and places it. S1 places on
/// the primary monitor only, treats every behaviour other than <c>reserve</c> as an overlay, and shows
/// a <c>Missing</c> placeholder for every contribution because no plugin can run yet. Call on the UI thread.
/// </summary>
public sealed class SurfaceManager : ISurfaceManager
{
    private readonly IConfigStore _config;
    private readonly IMonitorService _monitors;
    private readonly IAppBarService _appBars;
    private readonly IThemeEngine _theme;
    private readonly IHostLog _log;
    private readonly List<PanelWindow> _panels = [];

    public SurfaceManager(IConfigStore config, IMonitorService monitors, IAppBarService appBars, IThemeEngine theme, IHostLog log)
    {
        _config = config;
        _monitors = monitors;
        _appBars = appBars;
        _theme = theme;
        _log = log;
        _theme.AppearanceChanged += (_, _) => Restyle();
    }

    public IReadOnlyList<PanelWindow> Panels => _panels;

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

        foreach (var definition in layout.Panels)
        {
            var edge = definition.Placement.Edge ?? PanelEdge.Bottom;
            var panel = new WmPanel
            {
                Edge = edge.ToString().ToLowerInvariant(),
                Behaviour = definition.Behaviour.ToString().ToLowerInvariant(),
                Start = BuildZone(definition, "start"),
                Center = BuildZone(definition, "center"),
                End = BuildZone(definition, "end"),
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

    private static WmZone? BuildZone(PanelDefinition definition, string key)
    {
        if (!definition.Zones.TryGetValue(key, out var instances) || instances.Count == 0)
        {
            return null;
        }

        var zone = new WmZone();
        foreach (var instance in instances)
        {
            var placeholder = new WmPlaceholder { PluginName = instance.Plugin, Reason = nameof(PlaceholderReason.Missing) };
            if (Lookup("Wm.Placeholder") is Style placeholderStyle)
            {
                placeholder.Style = placeholderStyle;
            }

            zone.Items.Add(new WmContribution { Content = placeholder, Style = Lookup("Wm.Contribution") as Style });
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
                foreach (var contribution in zone.Items.OfType<WmContribution>())
                {
                    contribution.Style = Lookup("Wm.Contribution") as Style;
                    if (contribution.Content is WmPlaceholder placeholder)
                    {
                        placeholder.Style = Lookup("Wm.Placeholder") as Style;
                    }
                }
            }
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

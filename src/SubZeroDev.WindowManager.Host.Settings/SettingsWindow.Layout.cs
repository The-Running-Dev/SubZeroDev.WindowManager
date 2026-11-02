using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SubZeroDev.WindowManager.Host.Core;
using PanelEdge = SubZeroDev.WindowManager.Protocol.PanelEdge;

namespace SubZeroDev.WindowManager.Host.Settings;

/// <summary>
/// The Layout page: the active layout's panels and the instances on the selected one. Instances are added from
/// any plugin's panel items, moved within a zone or to another panel, and removed; panels are added on an edge
/// and removed. Each change is written to the layout file and the layout applied again.
/// </summary>
public sealed partial class SettingsWindow
{
    public const string LayoutPage = "layout";

    private readonly ComboBox _layoutPanel = new() { Header = "Panel", MinWidth = 280 };
    private readonly ListView _layoutInstances = new() { SelectionMode = ListViewSelectionMode.Single, MinHeight = 120, MaxHeight = 320 };
    private readonly ComboBox _layoutContribution = new() { Header = "Panel item", MinWidth = 280 };
    private readonly ComboBox _layoutZone = new() { Header = "Zone", MinWidth = 140 };
    private readonly ComboBox _layoutTargetPanel = new() { Header = "Move to panel", MinWidth = 280 };
    private readonly ComboBox _newPanelEdge = new() { Header = "Edge", MinWidth = 140 };
    private Guid? _wantedPanel;
    private Guid? _wantedInstance;
    private bool _refreshingLayout;

    private Guid? SelectedLayoutPanel => Guid.TryParse((_layoutPanel.SelectedItem as ComboBoxItem)?.Tag as string, out var id) ? id : null;

    private Guid? SelectedLayoutInstance => Guid.TryParse((_layoutInstances.SelectedItem as ListViewItem)?.Tag as string, out var id) ? id : null;

    private StackPanel BuildLayoutPage()
    {
        Name(_layoutPanel, "Panel", "Layout.Panel");
        Name(_layoutInstances, "Instances on the panel", "Layout.Instances");
        Name(_layoutContribution, "Panel item to add", "Layout.Contribution");
        Name(_layoutZone, "Zone", "Layout.TargetZone");
        Name(_layoutTargetPanel, "Panel to move to", "Layout.TargetPanel");
        Name(_newPanelEdge, "Edge of the new panel", "Layout.NewPanelEdge");
        foreach (var zone in Documents.ZoneKeys)
        {
            _layoutZone.Items.Add(Choice(zone, zone));
        }

        foreach (var edge in new[] { "top", "bottom", "left", "right" })
        {
            _newPanelEdge.Items.Add(Choice(edge, edge));
        }

        _layoutZone.SelectedIndex = 0;
        _newPanelEdge.SelectedIndex = 0;
        _layoutPanel.SelectionChanged += (_, _) =>
        {
            if (!_refreshingLayout)
            {
                RefreshLayout();
            }
        };

        var fresh = Guid.Empty;
        var add = LayoutButton("Add", "Layout.Add", layout =>
        {
            if (SelectedLayoutPanel is not { } panel || (_layoutContribution.SelectedItem as ComboBoxItem)?.Tag is not string item)
            {
                return Outcome<JsonObject>.Fail(CoreErrorCode.InvalidState, "Choose a panel and a panel item first.");
            }

            var (plugin, contribution) = (item[..item.IndexOf('/', StringComparison.Ordinal)], item[(item.IndexOf('/', StringComparison.Ordinal) + 1)..]);
            fresh = Guid.NewGuid();
            _wantedInstance = fresh;
            return LayoutEditor.Add(layout, panel, Zone(), plugin, contribution, fresh);
        }, () => $"Added instance {fresh}.");
        var moveUp = LayoutButton("Move up", "Layout.MoveUp", layout => Shift(layout, -1), () => "Moved up.");
        var moveDown = LayoutButton("Move down", "Layout.MoveDown", layout => Shift(layout, 1), () => "Moved down.");
        var move = LayoutButton("Move", "Layout.Move", layout =>
            SelectedLayoutInstance is { } instance && Guid.TryParse((_layoutTargetPanel.SelectedItem as ComboBoxItem)?.Tag as string, out var target)
                ? LayoutEditor.Move(layout, instance, target, Zone(), int.MaxValue)
                : Outcome<JsonObject>.Fail(CoreErrorCode.InvalidState, "Choose an instance and a panel to move it to first."), () => "Moved.");
        var remove = LayoutButton("Remove", "Layout.Remove", layout =>
            SelectedLayoutInstance is { } instance
                ? LayoutEditor.Remove(layout, instance)
                : Outcome<JsonObject>.Fail(CoreErrorCode.InvalidState, "Choose an instance first."), () => "Removed the instance.");
        var addPanel = LayoutButton("Add panel", "Layout.AddPanel", layout =>
        {
            var edge = Enum.Parse<PanelEdge>((string)((ComboBoxItem)_newPanelEdge.SelectedItem).Tag, ignoreCase: true);
            var panel = Guid.NewGuid();
            _wantedPanel = panel;
            return LayoutEditor.AddPanel(layout, edge, panel);
        }, () => "Added a panel.");
        var removePanel = LayoutButton("Remove panel", "Layout.RemovePanel", layout =>
            SelectedLayoutPanel is { } panel
                ? LayoutEditor.RemovePanel(layout, panel)
                : Outcome<JsonObject>.Fail(CoreErrorCode.InvalidState, "Choose a panel first."), () => "Removed the panel.");

        return new StackPanel
        {
            Spacing = 12,
            Padding = new Thickness(16),
            Children =
            {
                Heading("Panels"),
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _layoutPanel } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom, Children = { _newPanelEdge, addPanel, removePanel } },
                Heading("Instances"),
                _layoutInstances,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { moveUp, moveDown, remove } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _layoutContribution, _layoutZone, add } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _layoutTargetPanel, move } },
            },
        };
    }

    private string Zone() => (_layoutZone.SelectedItem as ComboBoxItem)?.Tag as string ?? "start";

    private Outcome<JsonObject> Shift(JsonObject layout, int by) =>
        SelectedLayoutInstance is { } instance && LayoutEditor.Placements(layout).FirstOrDefault(p => p.Instance == instance) is { } placement
            ? LayoutEditor.Move(layout, instance, placement.Panel, placement.Zone, Math.Max(0, placement.Index + by))
            : Outcome<JsonObject>.Fail(CoreErrorCode.InvalidState, "Choose an instance first.");

    private Button LayoutButton(string label, string automationId, Func<JsonObject, Outcome<JsonObject>> edit, Func<string> success)
    {
        var button = new Button { Content = label, VerticalAlignment = VerticalAlignment.Bottom };
        Name(button, label, automationId);
        button.Click += async (_, _) =>
        {
            _wantedInstance = SelectedLayoutInstance;
            var outcome = await _services.Settings.EditLayoutAsync(edit, CancellationToken.None);
            if (outcome.Ok)
            {
                await _services.InstanceSaved();
            }

            Report(outcome, success());
            RefreshLayout();
        };
        return button;
    }

    private void RefreshLayout()
    {
        _refreshingLayout = true;
        try
        {
            var layout = _services.Settings.ActiveLayout();
            var panels = layout is null ? [] : LayoutEditor.PanelIds(layout);
            var placements = layout is null ? [] : LayoutEditor.Placements(layout);
            var panel = _wantedPanel is { } wanted && panels.Contains(wanted) ? wanted
                : SelectedLayoutPanel is { } current && panels.Contains(current) ? current
                : panels.Count > 0 ? panels[0] : (Guid?)null;
            var target = Guid.TryParse((_layoutTargetPanel.SelectedItem as ComboBoxItem)?.Tag as string, out var chosen) ? chosen : (Guid?)null;
            _wantedPanel = null;

            var labels = panels.Select((id, i) => (Id: id.ToString("D"), Label: $"Panel {i + 1} ({Edge(layout!, id)})")).ToList();
            Fill(_layoutPanel, labels, panel?.ToString("D"));
            Fill(_layoutTargetPanel, labels, (target is { } t && panels.Contains(t) ? t : panel)?.ToString("D"));

            var instance = _wantedInstance ?? SelectedLayoutInstance;
            _wantedInstance = null;
            _layoutInstances.Items.Clear();
            foreach (var placement in placements.Where(p => p.Panel == panel))
            {
                var text = $"{placement.Zone} {placement.Index + 1}: {ContributionName(placement.Plugin, placement.Contribution)} ({placement.Plugin})";
                var item = new ListViewItem { Tag = placement.Instance.ToString("D"), Content = new TextBlock { Text = text } };
                AutomationProperties.SetName(item, text);
                AutomationProperties.SetAutomationId(item, $"Layout.Instance.{placement.Instance:D}");
                _layoutInstances.Items.Add(item);
            }

            _layoutInstances.SelectedItem = _layoutInstances.Items.OfType<ListViewItem>().FirstOrDefault(item => (string)item.Tag == instance?.ToString("D"));

            var items = _services.Catalog.All(PackageKind.Plugin).OrderBy(record => record.Id, StringComparer.Ordinal)
                .SelectMany(record => _services.Catalog.GetManifest(record.Id)?.Contributions.Where(c => c.Kind == "panelItem")
                    .Select(c => (Id: $"{record.Id}/{c.Id}", Label: $"{c.DisplayName} ({record.Id})")) ?? [])
                .ToList();
            Fill(_layoutContribution, items, (_layoutContribution.SelectedItem as ComboBoxItem)?.Tag as string ?? items.FirstOrDefault().Id);
        }
        finally
        {
            _refreshingLayout = false;
        }
    }

    private JsonObject LayoutSnapshot()
    {
        var layout = _services.Settings.ActiveLayout();
        return new JsonObject
        {
            ["panels"] = new JsonArray([.. (layout is null ? [] : LayoutEditor.PanelIds(layout)).Select(id => (JsonNode?)id.ToString("D"))]),
            ["selectedPanel"] = SelectedLayoutPanel?.ToString("D"),
            ["selectedInstance"] = SelectedLayoutInstance?.ToString("D"),
            ["instances"] = new JsonArray([.. _layoutInstances.Items.OfType<ListViewItem>().Select(item => (JsonNode?)(string)item.Tag)]),
            ["contributions"] = new JsonArray([.. _layoutContribution.Items.OfType<ComboBoxItem>().Select(item => (JsonNode?)(string)item.Tag)]),
        };
    }

    private string ContributionName(string plugin, string contribution) =>
        _services.Catalog.GetManifest(plugin)?.Contributions.FirstOrDefault(c => c.Id == contribution)?.DisplayName ?? contribution;

    private static string Edge(JsonObject layout, Guid panel) =>
        (layout["panels"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(item => item["id"] is JsonValue id && id.TryGetValue<string>(out var text) && Guid.TryParse(text, out var parsed) && parsed == panel)?
            ["placement"]?["edge"] is JsonValue edge && edge.TryGetValue<string>(out var name) ? name : "floating";

    private static ComboBoxItem Choice(string id, string label)
    {
        var item = new ComboBoxItem { Content = label, Tag = id };
        AutomationProperties.SetName(item, label);
        return item;
    }

    /// <summary>Replaces a combo box's choices when they changed, then selects <paramref name="selected"/>.</summary>
    private static void Fill(ComboBox box, IReadOnlyList<(string Id, string Label)> choices, string? selected)
    {
        if (!box.Items.OfType<ComboBoxItem>().Select(item => ((string)item.Tag, item.Content as string))
            .SequenceEqual(choices.Select(choice => (choice.Id, (string?)choice.Label))))
        {
            box.Items.Clear();
            foreach (var (id, label) in choices)
            {
                box.Items.Add(Choice(id, label));
            }
        }

        box.SelectedItem = box.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == selected);
    }
}

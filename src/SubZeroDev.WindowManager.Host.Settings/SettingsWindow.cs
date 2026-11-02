using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;

namespace SubZeroDev.WindowManager.Host.Settings;

/// <summary>What the settings window reads and changes; the app supplies the presets and what follows a save.</summary>
public sealed record SettingsServices(
    IPackageCatalog Catalog,
    IPluginSupervisor Supervisor,
    PluginLogs Logs,
    SettingsService Settings,
    Func<IReadOnlyList<(string Id, string Name)>> Presets,
    Func<string> ActivePreset,
    Func<string, CancellationToken, Task<Outcome>> ApplyPreset,
    Func<Task> InstanceSaved);

/// <summary>
/// <c>host.settings.open</c>: one window with a Plugins page (every plugin package with its version, source,
/// state and problems; the selected one's switches, instance settings, recent faults and log tail) and an
/// Appearance page (the active preset). Closing hides it; it lives until <see cref="Close"/>. Every control
/// has an automation name and id and is reachable with Tab. Call on the UI thread.
/// </summary>
public sealed class SettingsWindow
{
    public const string PluginsPage = "plugins";
    public const string AppearancePage = "appearance";

    private readonly SettingsServices _services;
    private readonly Window _window;
    private readonly NavigationView _navigation;
    private readonly NavigationViewItem _pluginsItem;
    private readonly NavigationViewItem _appearanceItem;
    private readonly Grid _pluginsPage;
    private readonly StackPanel _appearancePage;
    private readonly ListView _pluginList;
    private readonly StackPanel _detail = new() { Spacing = 12, Padding = new Thickness(16, 0, 16, 16) };
    private readonly TextBlock _detailHeader = new() { Style = null, FontSize = 20, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _detailState = new() { TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel _problems = new() { Spacing = 4 };
    private readonly Button _enable;
    private readonly Button _disable;
    private readonly Button _retry;
    private readonly StackPanel _instances = new() { Spacing = 12 };
    private readonly StackPanel _faults = new() { Spacing = 4 };
    private readonly TextBox _log;
    private readonly ComboBox _presets;
    private readonly TextBlock _message = new() { TextWrapping = TextWrapping.Wrap };
    private readonly List<(InstanceSettings Instance, SettingsForm Form)> _forms = [];
    private IReadOnlyList<string> _logLines = [];
    private bool _closeRequested;
    private bool _refreshingPresets;

    public SettingsWindow(SettingsServices services)
    {
        _services = services ?? throw new ArgumentNullException(nameof(services));

        _pluginList = new ListView { SelectionMode = ListViewSelectionMode.Single, MinWidth = 280 };
        Name(_pluginList, "Plugins", "Settings.PluginList");
        _pluginList.SelectionChanged += (_, _) => ShowDetail();

        _enable = ActionButton("Enable", "Plugin.Enable", () => _services.Supervisor.EnableAsync(SelectedPlugin!, CancellationToken.None));
        _disable = ActionButton("Disable", "Plugin.Disable", () => _services.Supervisor.DisableAsync(SelectedPlugin!, CancellationToken.None));
        _retry = ActionButton("Retry", "Plugin.Retry", () => _services.Supervisor.RetryAsync(SelectedPlugin!, CancellationToken.None));
        _log = new TextBox
        {
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            Height = 240,
            FontFamily = new FontFamily("Cascadia Mono, Consolas"),
            FontSize = 12,
        };
        Name(_log, "Log", "Plugin.Log");
        ScrollViewer.SetHorizontalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        ScrollViewer.SetVerticalScrollBarVisibility(_log, ScrollBarVisibility.Auto);
        AutomationProperties.SetHeadingLevel(_detailHeader, AutomationHeadingLevel.Level2);
        AutomationProperties.SetLiveSetting(_message, AutomationLiveSetting.Polite);
        AutomationProperties.SetAutomationId(_message, "Settings.Message");

        _detail.Children.Add(_detailHeader);
        _detail.Children.Add(_detailState);
        _detail.Children.Add(_problems);
        _detail.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _enable, _disable, _retry } });
        _detail.Children.Add(Heading("Instances"));
        _detail.Children.Add(_instances);
        _detail.Children.Add(Heading("Recent faults"));
        _detail.Children.Add(_faults);
        _detail.Children.Add(Heading("Log"));
        _detail.Children.Add(_log);

        var detailScroll = new ScrollViewer { Content = _detail };
        Grid.SetColumn(detailScroll, 1);
        _pluginsPage = new Grid
        {
            ColumnDefinitions = { new ColumnDefinition { Width = GridLength.Auto }, new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) } },
            Children = { _pluginList, detailScroll },
        };

        _presets = new ComboBox { Header = "Preset", MinWidth = 280 };
        Name(_presets, "Preset", "Appearance.Preset");
        _presets.SelectionChanged += async (_, _) =>
        {
            if (!_refreshingPresets && _presets.SelectedItem is ComboBoxItem { Tag: string presetId } && presetId != _services.ActivePreset())
            {
                Report(await _services.ApplyPreset(presetId, CancellationToken.None), $"Preset {presetId} applied.");
            }
        };
        _appearancePage = new StackPanel { Spacing = 12, Padding = new Thickness(16), Children = { _presets } };

        _pluginsItem = new NavigationViewItem { Content = "Plugins", Tag = PluginsPage, Icon = new SymbolIcon(Symbol.AllApps) };
        Name(_pluginsItem, "Plugins page", "Settings.Nav.Plugins");
        _appearanceItem = new NavigationViewItem { Content = "Appearance", Tag = AppearancePage, Icon = new SymbolIcon(Symbol.FontColor) };
        Name(_appearanceItem, "Appearance page", "Settings.Nav.Appearance");
        _navigation = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            IsPaneToggleButtonVisible = false,
            OpenPaneLength = 200,
            MenuItems = { _pluginsItem, _appearanceItem },
        };
        Name(_navigation, "Settings sections", "Settings.Nav");
        _navigation.SelectionChanged += (_, e) => ShowPage(e.SelectedItem is NavigationViewItem { Tag: string page } ? page : PluginsPage);

        var root = new Grid
        {
            RowDefinitions = { new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }, new RowDefinition { Height = GridLength.Auto } },
            Children = { _navigation },
        };
        var footer = new Border { Padding = new Thickness(16, 8, 16, 8), Child = _message };
        Grid.SetRow(footer, 1);
        root.Children.Add(footer);

        _window = new Window { Title = "SubZeroDev.WindowManager settings", Content = root };
        _window.AppWindow.Resize(new Windows.Graphics.SizeInt32(1100, 760));
        _window.AppWindow.Closing += (_, e) =>
        {
            if (!_closeRequested)
            {
                e.Cancel = true;
                _window.AppWindow.Hide();
                IsOpen = false;
            }
        };
        Handle = WinRT.Interop.WindowNative.GetWindowHandle(_window);
        _navigation.SelectedItem = _pluginsItem;
    }

    public nint Handle { get; }

    public bool IsOpen { get; private set; }

    public string Page => _navigation.SelectedItem is NavigationViewItem { Tag: string page } ? page : PluginsPage;

    public string? SelectedPlugin => (_pluginList.SelectedItem as ListViewItem)?.Tag as string;

    /// <summary>The last outcome shown at the foot of the window.</summary>
    public string Message => _message.Text;

    /// <summary>Shows and activates the window on <paramref name="page"/>, with <paramref name="pluginId"/> selected when given.</summary>
    public void Open(string? page, string? pluginId)
    {
        _navigation.SelectedItem = page == AppearancePage ? _appearanceItem : _pluginsItem;
        ShowPage(Page);
        if (pluginId is not null)
        {
            Select(pluginId);
        }

        _window.AppWindow.Show(activateWindow: true);
        _window.Activate();
        IsOpen = true;
    }

    /// <summary>Reads the plugins' states again; open forms keep what was typed into them.</summary>
    public void Refresh()
    {
        RefreshPluginList();
        RefreshStatus();
        RefreshPresets();
    }

    public void Select(string pluginId)
    {
        RefreshPluginList();
        _pluginList.SelectedItem = _pluginList.Items.OfType<ListViewItem>().FirstOrDefault(item => (string)item.Tag == pluginId);
        RefreshStatus();
    }

    public void Close()
    {
        _closeRequested = true;
        _window.Close();
    }

    /// <summary>
    /// Acts as a user would, for the test hook: <c>{ page }</c>, <c>{ select: pluginId }</c>,
    /// <c>{ invoke: automationId }</c> presses a button, <c>{ set: automationId, value }</c> sets a toggle,
    /// choice, number or text.
    /// </summary>
    public string? Drive(JsonObject request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request["page"] is JsonValue page)
        {
            Open(page.GetValue<string>(), null);
        }

        if (request["select"] is JsonValue plugin)
        {
            Select(plugin.GetValue<string>());
        }

        // A window opened by this request has not been laid out yet, so its controls are not in the visual tree.
        _window.Content.UpdateLayout();
        if (request["invoke"] is JsonValue invoke)
        {
            if (Find(invoke.GetValue<string>()) is not Button button || !button.IsEnabled)
            {
                return $"No enabled button {invoke}.";
            }

            ((IInvokeProvider)new ButtonAutomationPeer(button).GetPattern(PatternInterface.Invoke)).Invoke();
        }

        if (request["set"] is JsonValue set)
        {
            var value = request["value"];
            switch (Find(set.GetValue<string>()))
            {
                case ToggleSwitch toggle:
                    toggle.IsOn = value!.GetValue<bool>();
                    break;
                case ComboBox box:
                    var wanted = value!.GetValue<string>();
                    box.SelectedItem = box.Items.FirstOrDefault(item => item as string == wanted || (item as ComboBoxItem)?.Tag as string == wanted);
                    break;
                case NumberBox number:
                    number.Value = value!.GetValue<double>();
                    break;
                case TextBox text:
                    text.Text = value!.GetValue<string>();
                    break;
                default:
                    return $"No settable control {set}.";
            }
        }

        return null;
    }

    /// <summary>What the window shows, for the test hook.</summary>
    public JsonObject Snapshot()
    {
        var selected = SelectedPlugin;
        return new JsonObject
        {
            ["open"] = IsOpen,
            ["hwnd"] = Handle.ToInt64(),
            ["page"] = Page,
            ["message"] = Message,
            ["plugins"] = new JsonArray([.. Rows().Select(row => (JsonNode?)new JsonObject
            {
                ["id"] = row.Record.Id,
                ["version"] = row.Record.Version.ToString(),
                ["source"] = row.Record.Source.ToString(),
                ["state"] = row.Status.State.ToString(),
                ["text"] = RowText(row.Record, row.Status),
                ["problems"] = new JsonArray([.. Problems(row.Record, row.Status).Select(problem => (JsonNode?)new JsonObject
                {
                    ["code"] = problem.Code.ToString(),
                    ["detail"] = problem.Detail,
                })]),
            })]),
            ["detail"] = selected is null ? null : new JsonObject
            {
                ["pluginId"] = selected,
                ["header"] = _detailHeader.Text,
                ["state"] = _detailState.Text,
                ["problems"] = new JsonArray([.. _problems.Children.OfType<TextBlock>().Select(text => (JsonNode?)text.Text)]),
                ["enable"] = _enable.IsEnabled,
                ["disable"] = _disable.IsEnabled,
                ["retry"] = _retry.IsEnabled,
                ["faults"] = new JsonArray([.. _faults.Children.OfType<TextBlock>().Select(text => (JsonNode?)text.Text)]),
                ["logLines"] = new JsonArray([.. _logLines.Select(line => (JsonNode?)line)]),
                ["instances"] = new JsonArray([.. _forms.Select(form => (JsonNode?)new JsonObject
                {
                    ["instanceId"] = form.Instance.InstanceId,
                    ["contribution"] = form.Instance.Contribution,
                    ["fields"] = new JsonArray([.. form.Form.Fields.Select(field => (JsonNode?)new JsonObject
                    {
                        ["name"] = field.Name,
                        ["kind"] = field.Kind,
                        ["automationId"] = field.AutomationId,
                        ["value"] = field.Value?.DeepClone(),
                    })]),
                })]),
            },
            ["presets"] = new JsonObject
            {
                ["items"] = new JsonArray([.. _presets.Items.OfType<ComboBoxItem>().Select(item => (JsonNode?)(string)item.Tag)]),
                ["selected"] = (_presets.SelectedItem as ComboBoxItem)?.Tag as string,
            },
        };
    }

    private void ShowPage(string page)
    {
        _navigation.Content = page == AppearancePage ? _appearancePage : _pluginsPage;
        Refresh();
    }

    private IEnumerable<(PackageRecord Record, PluginStatus Status)> Rows() =>
        _services.Catalog.All(PackageKind.Plugin).OrderBy(record => record.Id, StringComparer.Ordinal)
            .Select(record => (record, _services.Supervisor.Describe(record.Id)));

    private static IReadOnlyList<CoreError> Problems(PackageRecord record, PluginStatus status) =>
        status.Problems.Count > 0 ? status.Problems : record.Problems;

    private static string RowText(PackageRecord record, PluginStatus status) =>
        $"{record.Id}  {record.Version}  {record.Source}  {status.State}";

    private void RefreshPluginList()
    {
        var rows = Rows().ToList();
        var items = _pluginList.Items.OfType<ListViewItem>().ToList();
        if (items.Select(item => (string)item.Tag).SequenceEqual(rows.Select(row => row.Record.Id)))
        {
            for (var i = 0; i < rows.Count; i++)
            {
                Fill(items[i], rows[i].Record, rows[i].Status);
            }

            return;
        }

        var selected = SelectedPlugin;
        _pluginList.Items.Clear();
        foreach (var (record, status) in rows)
        {
            var item = new ListViewItem { Tag = record.Id };
            Fill(item, record, status);
            _pluginList.Items.Add(item);
        }

        _pluginList.SelectedItem = _pluginList.Items.OfType<ListViewItem>().FirstOrDefault(item => (string)item.Tag == selected)
            ?? _pluginList.Items.FirstOrDefault();
    }

    private static void Fill(ListViewItem item, PackageRecord record, PluginStatus status)
    {
        var problems = Problems(record, status);
        var text = RowText(record, status) + (problems.Count > 0 ? $"  ({problems.Count} problem{(problems.Count == 1 ? "" : "s")})" : "");
        if (item.Content is TextBlock block)
        {
            block.Text = text;
        }
        else
        {
            item.Content = new TextBlock { Text = text };
        }

        AutomationProperties.SetName(item, text);
        AutomationProperties.SetAutomationId(item, $"Plugin.{record.Id}");
    }

    /// <summary>The selected plugin changed: everything in the detail is built again, forms included.</summary>
    private void ShowDetail()
    {
        _instances.Children.Clear();
        _forms.Clear();
        if (SelectedPlugin is { } pluginId)
        {
            var instances = _services.Settings.InstancesOf(pluginId);
            foreach (var instance in instances)
            {
                _instances.Children.Add(InstanceSection(pluginId, instance));
            }

            if (instances.Count == 0)
            {
                _instances.Children.Add(new TextBlock { Text = "No instances in the active layout.", Opacity = 0.7 });
            }
        }

        RefreshStatus();
    }

    private StackPanel InstanceSection(string pluginId, InstanceSettings instance)
    {
        var form = new SettingsForm(instance.Schema, instance.Settings, $"Instance.{instance.InstanceId}");
        _forms.Add((instance, form));
        var save = new Button { Content = "Save" };
        Name(save, $"Save {instance.ContributionName} settings", $"Instance.{instance.InstanceId}.Save");
        save.Click += async (_, _) =>
        {
            var outcome = await _services.Settings.SaveInstanceAsync(pluginId, instance.InstanceId, form.Read(), CancellationToken.None);
            if (outcome.Ok)
            {
                await _services.InstanceSaved();
            }

            Report(outcome, $"Saved the settings of {instance.ContributionName}.");
        };
        return new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = $"{instance.ContributionName} ({instance.InstanceId})", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold },
                form.Root,
                save,
            },
        };
    }

    private void RefreshStatus()
    {
        _problems.Children.Clear();
        _faults.Children.Clear();
        if (SelectedPlugin is not { } pluginId
            || _services.Catalog.All(PackageKind.Plugin).FirstOrDefault(record => record.Id == pluginId) is not { } record)
        {
            _detailHeader.Text = "No plugin selected.";
            _detailState.Text = string.Empty;
            _enable.IsEnabled = _disable.IsEnabled = _retry.IsEnabled = false;
            _logLines = [];
            _log.Text = string.Empty;
            return;
        }

        var status = _services.Supervisor.Describe(pluginId);
        _detailHeader.Text = $"{record.Id} {record.Version}";
        _detailState.Text = $"{record.Source}, {status.State}" + (status.ProcessId is { } pid ? $", process {pid}" : "");
        foreach (var problem in Problems(record, status))
        {
            _problems.Children.Add(new TextBlock { Text = $"{problem.Code}: {problem.Detail}", TextWrapping = TextWrapping.Wrap });
        }

        _enable.IsEnabled = status.State == PluginState.Disabled;
        _disable.IsEnabled = status.State is not (PluginState.Disabled or PluginState.Invalid);
        _retry.IsEnabled = status.State is PluginState.Unavailable or PluginState.Faulted;

        var faults = _services.Supervisor.FaultsOf(pluginId);
        foreach (var fault in faults.Reverse())
        {
            _faults.Children.Add(new TextBlock
            {
                Text = $"{fault.At.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}  {fault.Reason}: {fault.Detail}",
                TextWrapping = TextWrapping.Wrap,
            });
        }

        if (faults.Count == 0)
        {
            _faults.Children.Add(new TextBlock { Text = "None since the host started.", Opacity = 0.7 });
        }

        _logLines = _services.Logs.Tail(pluginId);
        _log.Text = string.Join("\r", _logLines);
    }

    private void RefreshPresets()
    {
        _refreshingPresets = true;
        try
        {
            var active = _services.ActivePreset();
            var presets = _services.Presets();
            if (!_presets.Items.OfType<ComboBoxItem>().Select(item => (string)item.Tag).SequenceEqual(presets.Select(preset => preset.Id)))
            {
                _presets.Items.Clear();
                foreach (var (id, name) in presets)
                {
                    var item = new ComboBoxItem { Content = name, Tag = id };
                    AutomationProperties.SetName(item, name);
                    _presets.Items.Add(item);
                }
            }

            _presets.SelectedItem = _presets.Items.OfType<ComboBoxItem>().FirstOrDefault(item => (string)item.Tag == active);
        }
        finally
        {
            _refreshingPresets = false;
        }
    }

    private Button ActionButton(string label, string automationId, Func<Task<Outcome>> act)
    {
        var button = new Button { Content = label };
        Name(button, label, automationId);
        button.Click += async (_, _) =>
        {
            var pluginId = SelectedPlugin;
            if (pluginId is null)
            {
                return;
            }

            Report(await act(), $"{label}: {pluginId} done.");
            Refresh();
        };
        return button;
    }

    private void Report(Outcome outcome, string success) =>
        _message.Text = outcome.Ok ? success : $"{outcome.Error!.Code}: {outcome.Error.Detail}";

    private static TextBlock Heading(string text)
    {
        var heading = new TextBlock { Text = text, FontSize = 16, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(0, 8, 0, 0) };
        AutomationProperties.SetHeadingLevel(heading, AutomationHeadingLevel.Level3);
        return heading;
    }

    private static void Name(DependencyObject element, string name, string automationId)
    {
        AutomationProperties.SetName(element, name);
        AutomationProperties.SetAutomationId(element, automationId);
    }

    private FrameworkElement? Find(string automationId) => Find(_window.Content, automationId);

    private static FrameworkElement? Find(DependencyObject? node, string automationId)
    {
        if (node is null)
        {
            return null;
        }

        if (node is FrameworkElement element && AutomationProperties.GetAutomationId(element) == automationId)
        {
            return element;
        }

        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(node); i++)
        {
            if (Find(VisualTreeHelper.GetChild(node, i), automationId) is { } found)
            {
                return found;
            }
        }

        return null;
    }
}

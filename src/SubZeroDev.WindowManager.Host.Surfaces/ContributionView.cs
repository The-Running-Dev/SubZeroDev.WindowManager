using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>
/// Renders one contribution instance into its <see cref="WmContribution"/>. A placeholder shows while
/// the instance has a reason; otherwise the retained view is built into element controls. A new view
/// revision rebuilds the tree, and a state change at the same revision updates only the bound props.
/// Each element is styled from <c>Wm.Element.&lt;Kind&gt;.&lt;class&gt;</c> (classes in order), then
/// <c>Wm.Element.&lt;Kind&gt;</c>, then the built-in style. Kinds that arrive with later slices render
/// as nothing. A Faulted or Unavailable placeholder's Retry runs <c>retry</c> with the plugin id, and its
/// detail shows as the tooltip. Call on the UI thread.
/// </summary>
public sealed class ContributionView
{
    private readonly Func<string, object?> _lookup;
    private readonly Action<string>? _retry;
    private readonly Orientation _panelAxis;
    private readonly List<Binding> _bindings = [];
    private readonly List<WmText> _texts = [];
    private readonly Dictionary<FrameworkElement, string?> _styleKeys = [];
    private long _revision = -1;

    public ContributionView(ContributionInstance instance, Orientation panelAxis, Func<string, object?> lookup,
        Action<string>? retry = null)
    {
        Instance = instance;
        _panelAxis = panelAxis;
        _lookup = lookup;
        _retry = retry;
        Root = new WmContribution { Style = lookup("Wm.Contribution") as Style };
    }

    public ContributionInstance Instance { get; }

    public WmContribution Root { get; }

    /// <summary>The placeholder reason shown now, or null while a view is shown.</summary>
    public string? Reason { get; private set; }

    /// <summary>The placeholder shown now, or null while a view is shown.</summary>
    public WmPlaceholder? Placeholder => Root.Content as WmPlaceholder;

    /// <summary>The text of every Text element shown now, in tree order.</summary>
    public IReadOnlyList<string> Texts() => _texts.Select(text => text.Text).ToList();

    /// <summary>Every Text element shown now, in tree order, with the theme style key it resolved to (null for the built-in style).</summary>
    public IReadOnlyList<(WmText Element, string? StyleKey)> TextElements() =>
        _texts.Select(text => (text, _styleKeys.GetValueOrDefault(text))).ToList();

    /// <summary>Shows <paramref name="snapshot"/>; <paramref name="restyle"/> rebuilds even at the same revision.</summary>
    public void Render(InstanceSnapshot? snapshot, bool restyle = false)
    {
        Root.Style = _lookup("Wm.Contribution") as Style;
        if (snapshot is null || snapshot.Reason is not null || snapshot.View is null)
        {
            ShowPlaceholder(snapshot?.Reason ?? PlaceholderReason.Starting, snapshot?.Detail);
            return;
        }

        if (restyle || Reason is not null || snapshot.ViewRevision != _revision)
        {
            _bindings.Clear();
            _texts.Clear();
            _styleKeys.Clear();
            Reason = null;
            _revision = snapshot.ViewRevision;
            Root.Content = Build(snapshot.View, snapshot.State);
            return;
        }

        foreach (var binding in _bindings)
        {
            snapshot.State.TryGetValue(binding.Key, out var value);
            var json = value?.ToJsonString();
            if (json != binding.Last)
            {
                binding.Last = json;
                binding.Apply(value);
            }
        }
    }

    private void ShowPlaceholder(PlaceholderReason reason, string? detail)
    {
        _bindings.Clear();
        _texts.Clear();
        _styleKeys.Clear();
        _revision = -1;
        Reason = reason.ToString();
        var placeholder = new WmPlaceholder
        {
            PluginName = Instance.Plugin,
            Reason = Reason,
            Detail = detail ?? string.Empty,
            Style = _lookup("Wm.Placeholder") as Style,
        };
        if (_retry is not null && reason is PlaceholderReason.Faulted or PlaceholderReason.Unavailable)
        {
            placeholder.RetryCommand = new RetryCommand(() => _retry(Instance.Plugin));
        }

        if (!string.IsNullOrEmpty(detail))
        {
            ToolTipService.SetToolTip(placeholder, detail);
        }

        Root.Content = placeholder;
    }

    private FrameworkElement Build(JsonNode? node, IReadOnlyDictionary<string, JsonNode?> state)
    {
        if (node is not JsonObject element)
        {
            return Nothing();
        }

        var kind = element["kind"]?.GetValue<string>() ?? string.Empty;
        var props = element["props"] as JsonObject ?? [];
        FrameworkElement built;
        switch (kind)
        {
            case "text":
                var text = new WmText();
                _texts.Add(text);
                Bind(props["text"], state, value => text.Text = AsString(value));
                Bind(props["wrap"], state, value => text.Wrap = AsBool(value));
                built = text;
                break;
            case "icon":
                var icon = new WmIcon();
                Bind(props["icon"], state, value => icon.IconName = value?["named"]?.GetValue<string>() ?? string.Empty);
                Bind(props["size"], state, value => icon.Size = value is null ? "medium" : AsString(value));
                built = icon;
                break;
            case "stack":
                var stack = new WmStack();
                Bind(props["orientation"], state, value => stack.Orientation = AsString(value) switch
                {
                    "horizontal" => Orientation.Horizontal,
                    "vertical" => Orientation.Vertical,
                    _ => _panelAxis,
                });
                if (props["orientation"] is null)
                {
                    stack.Orientation = _panelAxis;
                }

                foreach (var child in element["children"] as JsonArray ?? [])
                {
                    stack.Children.Add(Build(child, state));
                }

                built = stack;
                break;
            case "spacer":
                var spacer = new WmSpacer();
                Bind(props["flex"], state, value => spacer.Flex = AsBool(value));
                built = spacer;
                break;
            case "separator":
                built = new WmSeparator();
                break;
            default:
                return Nothing();
        }

        Bind(props["tooltip"], state, value => ToolTipService.SetToolTip(built, value is null ? null : AsString(value)));
        Bind(props["automationName"], state, value => AutomationProperties.SetName(built, AsString(value)));
        Bind(props["visible"], state, value => built.Visibility = value is null || AsBool(value) ? Visibility.Visible : Visibility.Collapsed);
        built.Name = element["id"]?.GetValue<string>() ?? string.Empty;
        var (styleKey, style) = ResolveStyle(kind, element["classes"] as JsonArray);
        built.Style = style;
        _styleKeys[built] = styleKey;
        return built;
    }

    private (string? Key, Style? Style) ResolveStyle(string kind, JsonArray? classes)
    {
        var key = "Wm.Element." + char.ToUpperInvariant(kind[0]) + kind[1..];
        foreach (var item in classes ?? [])
        {
            var classKey = key + "." + item?.GetValue<string>();
            if (_lookup(classKey) is Style classStyle)
            {
                return (classKey, classStyle);
            }
        }

        return _lookup(key) is Style style ? (key, style) : (null, null);
    }

    /// <summary>Applies a literal once, or applies a <c>{"$state":"key"}</c> binding now and on every later change.</summary>
    private void Bind(JsonNode? prop, IReadOnlyDictionary<string, JsonNode?> state, Action<JsonNode?> apply)
    {
        if (prop is null)
        {
            return;
        }

        if (prop is JsonObject binding && binding["$state"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var key))
        {
            state.TryGetValue(key, out var value);
            _bindings.Add(new Binding(key, apply) { Last = value?.ToJsonString() });
            apply(value);
            return;
        }

        apply(prop);
    }

    private static FrameworkElement Nothing() => new Border { Visibility = Visibility.Collapsed };

    private static string AsString(JsonNode? value) => value switch
    {
        null => string.Empty,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text,
        _ => value.ToJsonString(),
    };

    private static bool AsBool(JsonNode? value) => value is JsonValue scalar && scalar.TryGetValue<bool>(out var flag) && flag;

    private sealed partial class RetryCommand(Action retry) : System.Windows.Input.ICommand
    {
        public event EventHandler? CanExecuteChanged
        {
            add { }
            remove { }
        }

        public bool CanExecute(object? parameter) => true;

        public void Execute(object? parameter) => retry();
    }

    private sealed class Binding(string key, Action<JsonNode?> apply)
    {
        public string Key { get; } = key;

        public Action<JsonNode?> Apply { get; } = apply;

        public string? Last { get; set; }
    }
}

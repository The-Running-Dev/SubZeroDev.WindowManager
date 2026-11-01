using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Protocol;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>
/// Renders one contribution instance into its <see cref="WmContribution"/>. A placeholder shows while
/// the instance has a reason; otherwise the retained view is built into element controls. A new view
/// revision rebuilds the tree, and a state change at the same revision updates only the bound props.
/// Each element is styled from <c>Wm.Element.&lt;Kind&gt;.&lt;class&gt;</c> (classes in order), then
/// <c>Wm.Element.&lt;Kind&gt;</c>, then the built-in style. Kinds that arrive with later slices render
/// as nothing. A Faulted or Unavailable placeholder's Retry runs <c>retry</c> with the plugin id, and its
/// detail shows as the tooltip. A Button or Toggle sends its action through <c>actions</c>; when that
/// fails, a <see cref="WmNotice"/> shows next to the element for a few seconds and nothing is resent
/// (I19). Call on the UI thread.
/// </summary>
public sealed class ContributionView
{
    private readonly Func<string, object?> _lookup;
    private readonly Action<string>? _retry;
    private readonly Orientation _panelAxis;
    private readonly List<Binding> _bindings = [];
    private readonly List<WmText> _texts = [];
    private readonly Dictionary<FrameworkElement, string?> _styleKeys = [];
    private readonly IElementActions? _actions;
    private readonly List<Control> _interactive = [];
    private readonly List<ElementNotice> _notices = [];
    private IReadOnlyDictionary<string, JsonNode?> _state = new Dictionary<string, JsonNode?>();
    private long _revision = -1;

    public static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);

    public ContributionView(ContributionInstance instance, Orientation panelAxis, Func<string, object?> lookup,
        Action<string>? retry = null, IElementActions? actions = null)
    {
        Instance = instance;
        _panelAxis = panelAxis;
        _lookup = lookup;
        _retry = retry;
        _actions = actions;
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

    /// <summary>Every Button and Toggle shown now, in tree order.</summary>
    public IReadOnlyList<Control> InteractiveElements() => [.. _interactive];

    /// <summary>The notices shown so far, oldest first (the last 20).</summary>
    public IReadOnlyList<ElementNotice> Notices() => [.. _notices];

    /// <summary>Shows <paramref name="snapshot"/>; <paramref name="restyle"/> rebuilds even at the same revision.</summary>
    public void Render(InstanceSnapshot? snapshot, bool restyle = false)
    {
        Root.Style = _lookup("Wm.Contribution") as Style;
        if (snapshot is null || snapshot.Reason is not null || snapshot.View is null)
        {
            ShowPlaceholder(snapshot?.Reason ?? PlaceholderReason.Starting, snapshot?.Detail);
            return;
        }

        _state = snapshot.State;
        if (restyle || Reason is not null || snapshot.ViewRevision != _revision)
        {
            _bindings.Clear();
            _texts.Clear();
            _interactive.Clear();
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
        _interactive.Clear();
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
            case "button":
                var button = new WmButton();
                _interactive.Add(button);
                Bind(props["label"], state, value => button.Label = AsString(value));
                Bind(props["icon"], state, value => button.IconName = IconNameOf(value));
                Bind(props["active"], state, value => button.Active = AsBool(value));
                Bind(props["attention"], state, value => button.Attention = AsBool(value));
                Bind(props["badge"], state, value => button.Badge = AsString(value));
                var buttonAction = element["action"] as JsonObject;
                button.Click += (_, _) => Act(button, buttonAction, ElementEventKind.Invoked, null);
                built = button;
                break;
            case "toggle":
                var toggle = new WmToggle();
                _interactive.Add(toggle);
                Bind(props["label"], state, value => toggle.Label = AsString(value));
                Bind(props["icon"], state, value => toggle.IconName = IconNameOf(value));
                Bind(props["checked"], state, value => toggle.Checked = AsBool(value));
                var toggleAction = element["action"] as JsonObject;
                toggle.Click += (_, _) => Act(toggle, toggleAction, ElementEventKind.Toggled, JsonValue.Create(!toggle.Checked));
                built = toggle;
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

    /// <summary>
    /// Runs an element's action. A click arrives as <c>pointer</c> unless the element had keyboard focus,
    /// as it does after Tab, when Enter or Space produce the click. A Toggle never flips itself: it sends
    /// the value it asks for and shows whatever the plugin's state says next. Popups arrive with S8.
    /// </summary>
    private void Act(Control control, JsonObject? action, ElementEventKind kind, JsonNode? toggled)
    {
        if (_actions is null || action is null)
        {
            return;
        }

        var source = control.FocusState == FocusState.Keyboard ? InvocationSource.Keyboard : InvocationSource.Pointer;
        var elementId = control.Name;
        Task<Outcome> pending;
        if (action["invoke"] is JsonObject invoke && invoke["command"] is JsonValue command && command.TryGetValue<string>(out var commandId))
        {
            pending = InvokeAsync(commandId, Resolve(invoke["args"]) as JsonObject, source);
        }
        else if (action["event"] is JsonValue name && name.TryGetValue<string>(out var eventName))
        {
            pending = _actions.SendEventAsync(Instance.Id, elementId, kind,
                kind == ElementEventKind.Toggled ? toggled : JsonValue.Create(eventName), source);
        }
        else
        {
            return;
        }

        _ = ReportAsync(control, elementId, pending);
    }

    private async Task<Outcome> InvokeAsync(string commandId, JsonObject? args, InvocationSource source)
    {
        var result = await _actions!.InvokeCommandAsync(commandId, args, source);
        return result.Ok ? Outcome.Success : new Outcome(result.Error);
    }

    private async Task ReportAsync(Control control, string elementId, Task<Outcome> pending)
    {
        Outcome outcome;
        try
        {
            outcome = await pending;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            outcome = Outcome.Fail(CoreErrorCode.CommandFailed, e.Message);
        }

        if (outcome.Ok || control.XamlRoot is null)
        {
            return;
        }

        var notice = new WmNotice { Message = outcome.Error!.Detail, Style = _lookup("Wm.Notice") as Style };
        var flyout = new Flyout { Content = notice, ShouldConstrainToRootBounds = false, ShowMode = FlyoutShowMode.Transient };
        flyout.ShowAt(control);
        var timer = control.DispatcherQueue.CreateTimer();
        timer.Interval = NoticeDuration;
        timer.IsRepeating = false;
        timer.Tick += (_, _) => flyout.Hide();
        timer.Start();
        _notices.Add(new ElementNotice(elementId, outcome.Error.Code, outcome.Error.Detail));
        if (_notices.Count > 20)
        {
            _notices.RemoveAt(0);
        }
    }

    /// <summary>Copies command args, replacing each <c>{"$state":"key"}</c> with the state's current value.</summary>
    private JsonNode? Resolve(JsonNode? node) => node switch
    {
        JsonObject binding when binding.Count == 1 && binding["$state"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var key) =>
            _state.TryGetValue(key, out var value) ? value?.DeepClone() : null,
        JsonObject body => new JsonObject(body.Select(p => KeyValuePair.Create(p.Key, Resolve(p.Value)))),
        JsonArray items => new JsonArray([.. items.Select(Resolve)]),
        _ => node?.DeepClone(),
    };

    private static string IconNameOf(JsonNode? value) => value?["named"] is JsonValue named && named.TryGetValue<string>(out var name) ? name : string.Empty;

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

/// <summary>A notice shown because an element's action failed.</summary>
public sealed record ElementNotice(string ElementId, CoreErrorCode Code, string Message);

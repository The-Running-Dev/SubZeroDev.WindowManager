using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using SubZeroDev.WindowManager.Host.Core;
using SubZeroDev.WindowManager.Host.Surfaces.Controls;
using SubZeroDev.WindowManager.Host.Theming;
using SubZeroDev.WindowManager.Protocol;
using Orientation = Microsoft.UI.Xaml.Controls.Orientation;

namespace SubZeroDev.WindowManager.Host.Surfaces;

/// <summary>
/// Renders one contribution instance into its <see cref="WmContribution"/>. A placeholder shows while
/// the instance has a reason; otherwise the retained view is built into element controls. A new view
/// revision rebuilds the tree; a state change at the same revision re-evaluates the bound props and
/// applies only those whose value changed. A prop is bound when it holds <c>{"$state":"key"}</c> or,
/// inside a List's item template, <c>{"$item":"field"}</c>, at any depth. A List keeps one element per
/// item key: an item whose key stays keeps its element and gets its changed props, a new key is built,
/// a gone key is dropped, and the elements follow the items' order. Each element is styled from
/// <c>Wm.Element.&lt;Kind&gt;.&lt;class&gt;</c> (classes in order), then <c>Wm.Element.&lt;Kind&gt;</c>,
/// then the built-in style. A <c>window</c> or <c>app</c> icon is resolved through
/// <see cref="IconResolver"/> off the UI thread; a <c>named</c> icon is left to the theme through
/// <c>IconName</c>. A Faulted or Unavailable placeholder's Retry runs <c>retry</c> with the plugin id,
/// and its detail shows as the tooltip. A Button or Toggle sends its action through <c>actions</c>, with
/// <c>$item</c> args taken from its item and its events carrying the item key; when that fails, a
/// <see cref="WmNotice"/> shows next to the element for a few seconds and nothing is resent (I19). Call
/// on the UI thread.
/// </summary>
public sealed class ContributionView
{
    private const int ButtonIconPx = 32;
    private readonly Func<string, object?> _lookup;
    private readonly Action<string>? _retry;
    private readonly Orientation _panelAxis;
    private readonly IElementActions? _actions;
    private readonly IconResolver? _icons;
    private readonly Dictionary<FrameworkElement, ElementInfo> _info = [];
    private readonly List<ElementNotice> _notices = [];
    private Scope _root = new(null);
    private IReadOnlyDictionary<string, JsonNode?> _state = new Dictionary<string, JsonNode?>();
    private long _revision = -1;

    public static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);

    public ContributionView(ContributionInstance instance, Orientation panelAxis, Func<string, object?> lookup,
        Action<string>? retry = null, IElementActions? actions = null, IconResolver? icons = null)
    {
        Instance = instance;
        _panelAxis = panelAxis;
        _lookup = lookup;
        _retry = retry;
        _actions = actions;
        _icons = icons;
        Root = new WmContribution { Style = lookup("Wm.Contribution") as Style };
    }

    public ContributionInstance Instance { get; }

    public WmContribution Root { get; }

    /// <summary>The placeholder reason shown now, or null while a view is shown.</summary>
    public string? Reason { get; private set; }

    /// <summary>The placeholder shown now, or null while a view is shown.</summary>
    public WmPlaceholder? Placeholder => Root.Content as WmPlaceholder;

    /// <summary>The text of every Text element shown now, in tree order.</summary>
    public IReadOnlyList<string> Texts() => [.. Walk().OfType<WmText>().Select(text => text.Text)];

    /// <summary>Every Text element shown now, in tree order, with the theme style key it resolved to (null for the built-in style).</summary>
    public IReadOnlyList<(WmText Element, string? StyleKey)> TextElements() =>
        [.. Walk().OfType<WmText>().Select(text => (text, InfoOf(text)?.StyleKey))];

    /// <summary>Every Button and Toggle shown now, in tree order.</summary>
    public IReadOnlyList<Control> InteractiveElements() => [.. Walk().Where(element => element is WmButton or WmToggle).Cast<Control>()];

    /// <summary>What the view knows about an element it built: its style key, item key and icon source.</summary>
    public ElementInfo? InfoOf(FrameworkElement element) => _info.GetValueOrDefault(element);

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
            _info.Clear();
            Reason = null;
            _revision = snapshot.ViewRevision;
            _root = new Scope(null);
            Root.Content = Build(snapshot.View, _root);
            return;
        }

        Refresh(_root);
    }

    private void ShowPlaceholder(PlaceholderReason reason, string? detail)
    {
        _info.Clear();
        _root = new Scope(null);
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

    /// <summary>The built elements in tree order: stacks and lists, then their children.</summary>
    private IEnumerable<FrameworkElement> Walk()
    {
        if (Reason is not null || Root.Content is not FrameworkElement top)
        {
            yield break;
        }

        var pending = new Stack<FrameworkElement>();
        pending.Push(top);
        while (pending.Count > 0)
        {
            var element = pending.Pop();
            yield return element;
            IEnumerable<UIElement> children = element switch
            {
                WmStack stack => stack.Children,
                WmList list => list.Children,
                _ => [],
            };
            foreach (var child in children.Reverse().OfType<FrameworkElement>())
            {
                pending.Push(child);
            }
        }
    }

    private FrameworkElement Build(JsonNode? node, Scope scope)
    {
        if (node is not JsonObject element)
        {
            return Nothing();
        }

        var kind = element["kind"]?.GetValue<string>() ?? string.Empty;
        var props = element["props"] as JsonObject ?? [];
        var info = new ElementInfo(scope.ItemKey);
        FrameworkElement built;
        switch (kind)
        {
            case "text":
                var text = new WmText();
                Bind(props["text"], scope, value => text.Text = AsString(value));
                Bind(props["wrap"], scope, value => text.Wrap = AsBool(value));
                built = text;
                break;
            case "icon":
                var icon = new WmIcon();
                Bind(props["size"], scope, value => icon.Size = value is null ? "medium" : AsString(value));
                Bind(props["icon"], scope, value => SetIcon(icon, info, value, 2 * LogicalSize(icon.Size),
                    (name, image) => (icon.IconName, icon.Icon) = (name, image)));
                built = icon;
                break;
            case "stack":
                var stack = new WmStack { Orientation = _panelAxis };
                Bind(props["orientation"], scope, value => stack.Orientation = OrientationOf(value));
                foreach (var child in element["children"] as JsonArray ?? [])
                {
                    stack.Children.Add(Build(child, scope));
                }

                built = stack;
                break;
            case "list":
                var list = new WmList { Orientation = _panelAxis };
                Bind(props["orientation"], scope, value => list.Orientation = OrientationOf(value));
                var items = new ListItems(list, element["itemTemplate"], scope);
                Bind(props["items"], scope, value => Reconcile(items, value as JsonArray ?? []));
                built = list;
                break;
            case "spacer":
                var spacer = new WmSpacer();
                Bind(props["flex"], scope, value => spacer.Flex = AsBool(value));
                built = spacer;
                break;
            case "separator":
                built = new WmSeparator();
                break;
            case "button":
                var button = new WmButton();
                Bind(props["label"], scope, value => button.Label = AsString(value));
                Bind(props["icon"], scope, value => SetIcon(button, info, value, ButtonIconPx,
                    (name, image) => (button.IconName, button.Icon) = (name, image)));
                Bind(props["active"], scope, value => button.Active = AsBool(value));
                Bind(props["attention"], scope, value => button.Attention = AsBool(value));
                Bind(props["badge"], scope, value => button.Badge = AsString(value));
                var buttonAction = element["action"] as JsonObject;
                button.Click += (_, _) => Act(button, scope, buttonAction, ElementEventKind.Invoked, null);
                built = button;
                break;
            case "toggle":
                var toggle = new WmToggle();
                Bind(props["label"], scope, value => toggle.Label = AsString(value));
                Bind(props["icon"], scope, value => SetIcon(toggle, info, value, ButtonIconPx,
                    (name, image) => (toggle.IconName, toggle.Icon) = (name, image)));
                Bind(props["checked"], scope, value => toggle.Checked = AsBool(value));
                var toggleAction = element["action"] as JsonObject;
                toggle.Click += (_, _) => Act(toggle, scope, toggleAction, ElementEventKind.Toggled, JsonValue.Create(!toggle.Checked));
                built = toggle;
                break;
            default:
                return Nothing();
        }

        Bind(props["tooltip"], scope, value => ToolTipService.SetToolTip(built, value is null ? null : AsString(value)));
        Bind(props["automationName"], scope, value => AutomationProperties.SetName(built, AsString(value)));
        Bind(props["visible"], scope, value => built.Visibility = value is null || AsBool(value) ? Visibility.Visible : Visibility.Collapsed);
        built.Name = element["id"]?.GetValue<string>() ?? string.Empty;
        var (styleKey, style) = ResolveStyle(kind, element["classes"] as JsonArray);
        built.Style = style;
        info.StyleKey = styleKey;
        _info[built] = info;
        scope.Elements.Add(built);
        return built;
    }

    /// <summary>Matches the list's elements to <paramref name="items"/> by key; an item without a string key is skipped.</summary>
    private void Reconcile(ListItems list, JsonArray items)
    {
        var next = new Dictionary<string, Scope>(StringComparer.Ordinal);
        var order = new List<UIElement>();
        foreach (var node in items)
        {
            if (node is not JsonObject item || item["key"] is not JsonValue keyValue || !keyValue.TryGetValue<string>(out var key) || next.ContainsKey(key))
            {
                continue;
            }

            // A kept item's props are refreshed with the owner's other item scopes, right after this.
            if (list.Items.Remove(key, out var kept))
            {
                kept.Item = item;
            }
            else
            {
                kept = new Scope(item);
                kept.Element = Build(list.Template, kept);
                list.Owner.Children.Add(kept);
            }

            next[key] = kept;
            order.Add(kept.Element!);
        }

        foreach (var gone in list.Items.Values)
        {
            list.Owner.Children.Remove(gone);
            Forget(gone);
        }

        list.Items.Clear();
        foreach (var (key, scope) in next)
        {
            list.Items[key] = scope;
        }

        list.Control.SetItems(order);
    }

    private void Forget(Scope scope)
    {
        foreach (var element in scope.Elements)
        {
            if (_info.Remove(element, out var info))
            {
                info.IconRequest++;
            }
        }

        foreach (var child in scope.Children)
        {
            Forget(child);
        }
    }

    /// <summary>Re-evaluates a scope's bindings, then its item scopes'.</summary>
    private void Refresh(Scope scope)
    {
        foreach (var binding in scope.Bindings)
        {
            var value = Resolve(binding.Prop, scope);
            var json = value?.ToJsonString();
            if (json != binding.Last)
            {
                binding.Last = json;
                binding.Apply(value);
            }
        }

        foreach (var child in scope.Children.ToList())
        {
            Refresh(child);
        }
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

    /// <summary>Applies a literal once, or a prop holding a binding now and whenever its value changes.</summary>
    private void Bind(JsonNode? prop, Scope scope, Action<JsonNode?> apply)
    {
        if (prop is null)
        {
            return;
        }

        if (!HasBinding(prop))
        {
            apply(prop);
            return;
        }

        var value = Resolve(prop, scope);
        scope.Bindings.Add(new Binding(prop, apply) { Last = value?.ToJsonString() });
        apply(value);
    }

    /// <summary>
    /// Shows an icon ref. A <c>named</c> id goes to <c>IconName</c> for the theme to map; a <c>window</c>
    /// or <c>app</c> ref is extracted at <paramref name="sizePx"/> and, failing that, gets the default
    /// application icon. A newer ref, or the element leaving the view, makes an older answer stale.
    /// </summary>
    private void SetIcon(FrameworkElement element, ElementInfo info, JsonNode? value, int sizePx, Action<string, ImageSource?> show)
    {
        var request = ++info.IconRequest;
        var reference = IconRef.FromJson(value);
        if (reference is IconRef.Named named)
        {
            info.IconSource = "named";
            show(named.Id, null);
            return;
        }

        if (reference is null || _icons is null)
        {
            info.IconSource = "none";
            show(string.Empty, null);
            return;
        }

        _ = LoadIconAsync();

        async Task LoadIconAsync()
        {
            ResolvedIcon resolved;
            try
            {
                resolved = await _icons.ResolveAsync(reference, sizePx, CancellationToken.None);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                resolved = new ResolvedIcon(null, IconOrigin.None);
            }

            if (info.IconRequest != request || !_info.ContainsKey(element))
            {
                return;
            }

            info.IconSource = resolved.Source.ToString().ToLowerInvariant();
            show(string.Empty, resolved.Image);
        }
    }

    /// <summary>The theme's <c>Wm.IconSize.*</c> for an Icon size, in DIPs (16, 20 or 32 by default).</summary>
    private int LogicalSize(string size)
    {
        var (key, fallback) = size switch
        {
            "small" => ("Small", 16),
            "large" => ("Large", 32),
            _ => ("Medium", 20),
        };
        return _lookup("Wm.IconSize." + key) is double themed ? (int)Math.Round(themed) : fallback;
    }

    /// <summary>
    /// Runs an element's action. A click arrives as <c>pointer</c> unless the element had keyboard focus,
    /// as it does after Tab, when Enter or Space produce the click. A Toggle never flips itself: it sends
    /// the value it asks for and shows whatever the plugin's state says next. Popups arrive with S8.
    /// </summary>
    private void Act(Control control, Scope scope, JsonObject? action, ElementEventKind kind, JsonNode? toggled)
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
            pending = InvokeAsync(commandId, Resolve(invoke["args"], scope) as JsonObject, source);
        }
        else if (action["event"] is JsonValue name && name.TryGetValue<string>(out var eventName))
        {
            pending = _actions.SendEventAsync(Instance.Id, elementId, kind,
                kind == ElementEventKind.Toggled ? toggled : JsonValue.Create(eventName), source, scope.ItemKey);
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

    /// <summary>
    /// Copies a prop, replacing each <c>{"$state":"key"}</c> with the state's current value and each
    /// <c>{"$item":"field"}</c> with the field of the scope's item (null outside an item).
    /// </summary>
    private JsonNode? Resolve(JsonNode? node, Scope scope) => node switch
    {
        JsonObject binding when binding.Count == 1 && binding["$state"] is JsonValue keyValue && keyValue.TryGetValue<string>(out var key) =>
            _state.TryGetValue(key, out var value) ? value?.DeepClone() : null,
        JsonObject binding when binding.Count == 1 && binding["$item"] is JsonValue fieldValue && fieldValue.TryGetValue<string>(out var field) =>
            scope.Item?[field]?.DeepClone(),
        JsonObject body => new JsonObject(body.Select(p => KeyValuePair.Create(p.Key, Resolve(p.Value, scope)))),
        JsonArray items => new JsonArray([.. items.Select(item => Resolve(item, scope))]),
        _ => node?.DeepClone(),
    };

    private static bool HasBinding(JsonNode? node) => node switch
    {
        JsonObject binding when binding.Count == 1 && (binding.ContainsKey("$state") || binding.ContainsKey("$item")) => true,
        JsonObject body => body.Any(p => HasBinding(p.Value)),
        JsonArray items => items.Any(HasBinding),
        _ => false,
    };

    private Orientation OrientationOf(JsonNode? value) => AsString(value) switch
    {
        "horizontal" => Orientation.Horizontal,
        "vertical" => Orientation.Vertical,
        _ => _panelAxis,
    };

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

    private sealed class Binding(JsonNode prop, Action<JsonNode?> apply)
    {
        public JsonNode Prop { get; } = prop;

        public Action<JsonNode?> Apply { get; } = apply;

        public string? Last { get; set; }
    }

    /// <summary>The bindings and elements made for the whole view, or for one list item.</summary>
    private sealed class Scope(JsonObject? item)
    {
        public JsonObject? Item { get; set; } = item;

        public string? ItemKey => Item?["key"] is JsonValue key && key.TryGetValue<string>(out var text) ? text : null;

        public FrameworkElement? Element { get; set; }

        public List<Binding> Bindings { get; } = [];

        public List<FrameworkElement> Elements { get; } = [];

        public List<Scope> Children { get; } = [];
    }

    private sealed class ListItems(WmList control, JsonNode? template, Scope owner)
    {
        public WmList Control { get; } = control;

        public JsonNode? Template { get; } = template;

        public Scope Owner { get; } = owner;

        public Dictionary<string, Scope> Items { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>What a view knows about an element it built.</summary>
public sealed class ElementInfo(string? itemKey)
{
    /// <summary>The theme style key the element resolved to, or null for the built-in style.</summary>
    public string? StyleKey { get; internal set; }

    /// <summary>The key of the list item the element belongs to, or null outside a list.</summary>
    public string? ItemKey { get; } = itemKey;

    /// <summary>Where its icon came from: <c>named</c>, <c>extracted</c>, <c>default</c> or <c>none</c>; null without an icon prop.</summary>
    public string? IconSource { get; internal set; }

    internal int IconRequest { get; set; }
}

/// <summary>A notice shown because an element's action failed.</summary>
public sealed record ElementNotice(string ElementId, CoreErrorCode Code, string Message);

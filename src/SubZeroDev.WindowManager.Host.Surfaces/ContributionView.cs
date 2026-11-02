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
/// <see cref="WmNotice"/> shows next to the element for a few seconds and nothing is resent (I19). A
/// Button whose action names a popup opens it anchored to the Button. A single-selection List keeps one
/// selected item (the first when its <c>selectedKey</c> names none); a TextInput whose <c>navigates</c>
/// names that List moves the selection with Up and Down, and Enter runs the selected item's action. The
/// same view renders a plugin's popup surface (<see cref="ForPopup"/>). Call on the UI thread.
/// </summary>
public sealed class ContributionView
{
    private const int ButtonIconPx = 32;
    private readonly ContributionInstance? _instance;
    private readonly Func<string, object?> _lookup;
    private readonly Action<string>? _retry;
    private readonly Orientation _panelAxis;
    private readonly IElementActions? _actions;
    private readonly ElementEventSink? _sendEvent;
    private readonly PopupOpener? _openPopup;
    private readonly IconResolver? _icons;
    private readonly Dictionary<FrameworkElement, ElementInfo> _info = [];
    private readonly Dictionary<string, ListItems> _lists = new(StringComparer.Ordinal);
    private readonly Dictionary<FrameworkElement, Action<InvocationSource>> _invokers = [];
    private readonly List<ElementNotice> _notices = [];
    private Scope _root = new(null);
    private IReadOnlyDictionary<string, JsonNode?> _state = new Dictionary<string, JsonNode?>();
    private long _revision = -1;

    public static readonly TimeSpan NoticeDuration = TimeSpan.FromSeconds(4);

    public ContributionView(ContributionInstance instance, Orientation panelAxis, Func<string, object?> lookup,
        Action<string>? retry = null, IElementActions? actions = null, IconResolver? icons = null, PopupOpener? openPopup = null)
        : this(instance, instance.Plugin, panelAxis, lookup, retry, actions,
            actions is null ? null : (elementId, kind, value, source, itemKey) => actions.SendEventAsync(instance.Id, elementId, kind, value, source, itemKey),
            openPopup, icons)
    {
    }

    private ContributionView(ContributionInstance? instance, string pluginId, Orientation panelAxis, Func<string, object?> lookup,
        Action<string>? retry, IElementActions? actions, ElementEventSink? sendEvent, PopupOpener? openPopup, IconResolver? icons)
    {
        _instance = instance;
        PluginId = pluginId;
        _panelAxis = panelAxis;
        _lookup = lookup;
        _retry = retry;
        _actions = actions;
        _sendEvent = sendEvent;
        _openPopup = openPopup;
        _icons = icons;
        Root = new WmContribution { Style = lookup("Wm.Contribution") as Style };
    }

    /// <summary>A view for a plugin's popup surface: vertical by default, events sent through <paramref name="sendEvent"/>.</summary>
    public static ContributionView ForPopup(string pluginId, Func<string, object?> lookup, IElementActions? actions,
        ElementEventSink? sendEvent, IconResolver? icons = null) =>
        new(null, pluginId, Orientation.Vertical, lookup, null, actions, sendEvent, null, icons);

    /// <summary>The instance shown; a popup view has none.</summary>
    public ContributionInstance Instance => _instance ?? throw new InvalidOperationException("A popup view has no contribution instance.");

    public string PluginId { get; }

    public WmContribution Root { get; }

    /// <summary>The placeholder reason shown now, or null while a view is shown.</summary>
    public string? Reason { get; private set; }

    /// <summary>When the current live view was first shown (Unix ms, UTC), or null while a placeholder or nothing is shown.</summary>
    public long? LiveSinceUtcMs { get; private set; }

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

    /// <summary>Every TextInput shown now, in tree order.</summary>
    public IReadOnlyList<WmTextInput> TextInputs() => [.. Walk().OfType<WmTextInput>()];

    /// <summary>The key of the selected item of a single-selection List, or null.</summary>
    public string? SelectedKey(string listId) => _lists.TryGetValue(listId, out var list) ? list.Shown : null;

    /// <summary>The first element with <paramref name="elementId"/>, in tree order.</summary>
    public FrameworkElement? Find(string elementId) => Walk().FirstOrDefault(element => element.Name == elementId);

    /// <summary>Shows <paramref name="snapshot"/>; <paramref name="restyle"/> rebuilds even at the same revision.</summary>
    public void Render(InstanceSnapshot? snapshot, bool restyle = false)
    {
        Root.Style = _lookup("Wm.Contribution") as Style;
        if (snapshot is null || snapshot.Reason is not null || snapshot.View is null)
        {
            ShowPlaceholder(snapshot?.Reason ?? PlaceholderReason.Starting, snapshot?.Detail);
            return;
        }

        Show(snapshot.View, snapshot.ViewRevision, snapshot.State, restyle);
    }

    /// <summary>Shows a popup's <paramref name="snapshot"/>; nothing while it has no view.</summary>
    public void Render(PopupSnapshot? snapshot, bool restyle = false)
    {
        Root.Style = _lookup("Wm.Contribution") as Style;
        if (snapshot?.View is null)
        {
            Reset();
            _revision = -1;
            Root.Content = null;
            LiveSinceUtcMs = null;
            return;
        }

        Show(snapshot.View, snapshot.ViewRevision, snapshot.State, restyle);
    }

    private void Show(JsonNode view, long revision, IReadOnlyDictionary<string, JsonNode?> state, bool restyle)
    {
        _state = state;
        if (restyle || Reason is not null || Root.Content is null || revision != _revision)
        {
            var wasLive = Reason is null && Root.Content is not null;
            Reset();
            _revision = revision;
            Root.Content = Build(view, _root);
            if (!wasLive)
            {
                LiveSinceUtcMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            }

            return;
        }

        Refresh(_root);
    }

    private void Reset()
    {
        _info.Clear();
        _lists.Clear();
        _invokers.Clear();
        _root = new Scope(null);
        Reason = null;
    }

    private void ShowPlaceholder(PlaceholderReason reason, string? detail)
    {
        Reset();
        _revision = -1;
        Reason = reason.ToString();
        LiveSinceUtcMs = null;
        var placeholder = new WmPlaceholder
        {
            PluginName = PluginId,
            Reason = Reason,
            Detail = detail ?? string.Empty,
            Style = _lookup("Wm.Placeholder") as Style,
        };
        if (_retry is not null && reason is PlaceholderReason.Faulted or PlaceholderReason.Unavailable)
        {
            placeholder.RetryCommand = new RetryCommand(() => _retry(PluginId));
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
                Bind(props["selection"], scope, value =>
                {
                    items.Single = AsString(value) == "single";
                    ShowSelection(items);
                });
                Bind(props["selectedKey"], scope, value =>
                {
                    items.SelectedKey = value is null ? null : AsString(value);
                    ShowSelection(items);
                });
                Bind(props["items"], scope, value => Reconcile(items, value as JsonArray ?? []));
                if (element["id"] is JsonValue listIdValue && listIdValue.TryGetValue<string>(out var listId))
                {
                    _lists[listId] = items;
                }

                built = list;
                break;
            case "textInput":
                var input = new WmTextInput();
                Bind(props["placeholder"], scope, value => input.PlaceholderText = AsString(value));
                Bind(props["value"], scope, value => input.Apply(AsString(value)));
                var navigates = props["navigates"] is JsonValue navigatesValue && navigatesValue.TryGetValue<string>(out var navigatesId)
                    ? navigatesId
                    : null;
                input.Edited += (_, text) => Send(input, ElementEventKind.TextChanged, JsonValue.Create(text), null);
                input.Navigate += (_, delta) => Navigate(navigates, delta);
                input.Submit += (_, _) => Submit(input, navigates);
                built = input;
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
                _invokers[button] = source => Act(button, scope, buttonAction, ElementEventKind.Invoked, null, source);
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
        list.Order.Clear();
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
            list.Order.Add(key);
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
        ShowSelection(list);
    }

    /// <summary>
    /// Marks the selected item of a single-selection list: the plugin's <c>selectedKey</c> when it names
    /// an item, otherwise the first item.
    /// </summary>
    private static void ShowSelection(ListItems list)
    {
        list.Shown = list.Single && list.Order.Count > 0
            ? list.SelectedKey is { } selected && list.Items.ContainsKey(selected) ? selected : list.Order[0]
            : null;
        foreach (var (key, scope) in list.Items)
        {
            if (scope.Elements.OfType<WmButton>().FirstOrDefault() is { } button)
            {
                button.Selected = key == list.Shown;
            }
        }
    }

    /// <summary>Moves a single-selection list's selection by <paramref name="delta"/> and tells the plugin.</summary>
    private void Navigate(string? listId, int delta)
    {
        if (listId is null || !_lists.TryGetValue(listId, out var list) || !list.Single || list.Order.Count == 0)
        {
            return;
        }

        var at = list.Shown is { } shown ? list.Order.IndexOf(shown) : -1;
        var next = list.Order[Math.Clamp(at + delta, 0, list.Order.Count - 1)];
        if (next == list.Shown)
        {
            return;
        }

        list.SelectedKey = next;
        ShowSelection(list);
        if (_sendEvent is not null)
        {
            _ = _sendEvent(listId, ElementEventKind.SelectionChanged, JsonValue.Create(next), InvocationSource.Keyboard, next);
        }
    }

    /// <summary>
    /// Enter in a TextInput: runs the action of the selected item of the list it navigates, or sends
    /// <c>ItemInvoked</c> when that item has none; without such a list, sends <c>TextSubmitted</c>.
    /// </summary>
    private void Submit(WmTextInput input, string? listId)
    {
        if (listId is not null && _lists.TryGetValue(listId, out var list) && list.Single)
        {
            if (list.Shown is not { } key || !list.Items.TryGetValue(key, out var item))
            {
                return;
            }

            if (item.Elements.Select(element => _invokers.GetValueOrDefault(element)).FirstOrDefault(invoker => invoker is not null) is { } invoke)
            {
                invoke(InvocationSource.Keyboard);
            }
            else if (_sendEvent is not null)
            {
                _ = ReportAsync(input, listId, _sendEvent(listId, ElementEventKind.ItemInvoked, JsonValue.Create(key), InvocationSource.Keyboard, key));
            }

            return;
        }

        Send(input, ElementEventKind.TextSubmitted, JsonValue.Create(input.Text), null);
    }

    private void Send(Control control, ElementEventKind kind, JsonNode? value, string? itemKey)
    {
        if (_sendEvent is not null)
        {
            _ = ReportAsync(control, control.Name, _sendEvent(control.Name, kind, value, InvocationSource.Keyboard, itemKey));
        }
    }

    private void Forget(Scope scope)
    {
        foreach (var element in scope.Elements)
        {
            if (_info.Remove(element, out var info))
            {
                info.IconRequest++;
            }

            _invokers.Remove(element);
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
    /// Shows an icon ref through the asset-pack chain. A <c>named</c> id also goes to <c>IconName</c>, so a
    /// theme can still map one no pack has; a <c>window</c> or <c>app</c> ref falls back to the extracted
    /// icon at <paramref name="sizePx"/>, then the default application icon. A newer ref, or the element
    /// leaving the view, makes an older answer stale.
    /// </summary>
    private void SetIcon(FrameworkElement element, ElementInfo info, JsonNode? value, int sizePx, Action<string, ImageSource?> show)
    {
        var request = ++info.IconRequest;
        var reference = IconRef.FromJson(value);
        var name = (reference as IconRef.Named)?.Id ?? string.Empty;
        if (reference is IconRef.Named && _icons is null)
        {
            info.IconSource = "named";
            show(name, null);
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
            // A cached or pack icon resolves without awaiting; yield so the element is registered first.
            await Task.Yield();
            ResolvedIcon resolved;
            try
            {
                resolved = await _icons.ResolveAsync(reference, sizePx, CancellationToken.None);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                resolved = ResolvedIcon.None;
            }

            if (info.IconRequest != request || !_info.ContainsKey(element))
            {
                return;
            }

            info.IconSource = resolved.Image is null && name.Length > 0 ? "named" : resolved.Source;
            show(name, resolved.Image);
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
    /// as it does after Tab, when Enter or Space produce the click; Enter in a TextInput passes
    /// <c>keyboard</c>. A Toggle never flips itself: it sends the value it asks for and shows whatever the
    /// plugin's state says next. A popup action opens the plugin's popup anchored to the element.
    /// </summary>
    private void Act(Control control, Scope scope, JsonObject? action, ElementEventKind kind, JsonNode? toggled,
        InvocationSource? source = null)
    {
        if (action is null)
        {
            return;
        }

        var from = source ?? (control.FocusState == FocusState.Keyboard ? InvocationSource.Keyboard : InvocationSource.Pointer);
        var elementId = control.Name;
        Task<Outcome> pending;
        if (action["invoke"] is JsonObject invoke && invoke["command"] is JsonValue command && command.TryGetValue<string>(out var commandId))
        {
            if (_actions is null)
            {
                return;
            }

            pending = InvokeAsync(commandId, Resolve(invoke["args"], scope) as JsonObject, from);
        }
        else if (action["event"] is JsonValue name && name.TryGetValue<string>(out var eventName))
        {
            if (_sendEvent is null)
            {
                return;
            }

            pending = _sendEvent(elementId, kind, kind == ElementEventKind.Toggled ? toggled : JsonValue.Create(eventName), from, scope.ItemKey);
        }
        else if (action["popup"] is JsonValue popup && popup.TryGetValue<string>(out var popupId))
        {
            if (_openPopup is null)
            {
                return;
            }

            pending = _openPopup(popupId, control, from);
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

        /// <summary>The item keys in the order shown.</summary>
        public List<string> Order { get; } = [];

        public bool Single { get; set; }

        /// <summary>The key the plugin (or the last Up or Down) selected.</summary>
        public string? SelectedKey { get; set; }

        /// <summary>The key shown as selected, or null.</summary>
        public string? Shown { get; set; }
    }
}

/// <summary>Sends what the user did to an element to the plugin that owns the view.</summary>
public delegate Task<Outcome> ElementEventSink(string elementId, ElementEventKind kind, JsonNode? value, InvocationSource source,
    string? itemKey);

/// <summary>Opens one of the view's plugin's popups anchored to <paramref name="anchor"/>.</summary>
public delegate Task<Outcome> PopupOpener(string popupId, FrameworkElement anchor, InvocationSource source);

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

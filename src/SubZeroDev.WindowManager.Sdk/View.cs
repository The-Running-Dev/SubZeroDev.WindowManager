using System.Globalization;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Sdk;

public enum IconSize { Small, Medium, Large }

public enum StackOrientation { Horizontal, Vertical, Panel }

public enum ListSelection { None, Single }

public abstract record IconRef
{
    public sealed record Named(string Id) : IconRef;

    public sealed record App(string? Exe, string? Aumid) : IconRef;

    public sealed record Window(long Hwnd) : IconRef;

    public sealed record File(string PackagePath) : IconRef;

    internal JsonNode ToJson() => this switch
    {
        Named named => new JsonObject { ["named"] = named.Id },
        App app => new JsonObject { ["app"] = new JsonObject { ["exe"] = app.Exe, ["aumid"] = app.Aumid } },
        Window window => new JsonObject { ["window"] = window.Hwnd },
        File file => new JsonObject { ["file"] = file.PackagePath },
        _ => throw new InvalidOperationException("Unknown icon reference."),
    };
}

/// <summary>A literal prop value, or a binding to a state key or a list item field.</summary>
public readonly struct Prop<T>
{
    private readonly JsonNode? _node;

    private Prop(JsonNode? node) => _node = node;

    public static implicit operator Prop<T>(T literal) => new(Literal(literal));

    public static Prop<T> State(string key) => new(new JsonObject { ["$state"] = key });

    public static Prop<T> Item(string field) => new(new JsonObject { ["$item"] = field });

    internal JsonNode? ToJson() => _node?.DeepClone();

    private static JsonNode? Literal(T value) => value switch
    {
        null => null,
        IconRef icon => icon.ToJson(),
        JsonNode node => node.DeepClone(),
        string text => JsonValue.Create(text),
        bool flag => JsonValue.Create(flag),
        double number => JsonValue.Create(number),
        int number => JsonValue.Create(number),
        long number => JsonValue.Create(number),
        _ => JsonValue.Create(Convert.ToString(value, CultureInfo.InvariantCulture)),
    };
}

/// <summary>Builders for <see cref="ViewNode"/> trees.</summary>
public static class View
{
    public static ViewNode Text(string id, Prop<string> text, params string[] classes) =>
        Node(id, ElementKind.Text, classes, ("text", text.ToJson()));

    public static ViewNode Icon(string id, Prop<IconRef> icon, IconSize size = IconSize.Medium) =>
        Node(id, ElementKind.Icon, [], ("icon", icon.ToJson()), ("size", Lower(size)));

    public static ViewNode Button(string id, string automationName, ElementAction action,
        Prop<string>? label = null, Prop<IconRef>? icon = null, Prop<bool>? active = null, params string[] classes) =>
        Node(id, ElementKind.Button, classes,
            ("automationName", automationName), ("label", label?.ToJson()), ("icon", icon?.ToJson()), ("active", active?.ToJson()))
        with { Action = action };

    /// <summary>The button with an <c>attention</c> prop: true asks for the user's attention, as a flashing window does.</summary>
    public static ViewNode WithAttention(this ViewNode button, Prop<bool> attention)
    {
        var props = new Dictionary<string, JsonNode?>(button.Props ?? new Dictionary<string, JsonNode?>(), StringComparer.Ordinal)
        {
            ["attention"] = attention.ToJson(),
        };
        return button with { Props = props };
    }

    public static ViewNode Toggle(string id, string automationName, Prop<bool> isChecked, ElementAction action,
        Prop<string>? label = null, Prop<IconRef>? icon = null, params string[] classes) =>
        Node(id, ElementKind.Toggle, classes,
            ("automationName", automationName), ("checked", isChecked.ToJson()), ("label", label?.ToJson()), ("icon", icon?.ToJson()))
        with { Action = action };

    public static ViewNode Stack(string id, StackOrientation orientation, params ViewNode[] children) =>
        Node(id, ElementKind.Stack, [], ("orientation", Lower(orientation))) with { Children = children };

    public static ViewNode List(string id, string automationName, Prop<JsonArray> items, ViewNode itemTemplate,
        StackOrientation orientation = StackOrientation.Panel, ListSelection selection = ListSelection.None,
        Prop<string>? selectedKey = null) =>
        Node(id, ElementKind.List, [],
            ("automationName", automationName), ("items", items.ToJson()), ("orientation", Lower(orientation)), ("selection", Lower(selection)),
            ("selectedKey", selectedKey?.ToJson()))
        with { ItemTemplate = itemTemplate };

    public static ViewNode TextInput(string id, string automationName, Prop<string> value,
        string? placeholder = null, string? navigates = null) =>
        Node(id, ElementKind.TextInput, [],
            ("automationName", automationName), ("value", value.ToJson()), ("placeholder", placeholder), ("navigates", navigates));

    public static ViewNode Image(string id, Prop<IconRef> source, string fit = "contain") =>
        Node(id, ElementKind.Image, [], ("source", source.ToJson()), ("fit", fit));

    public static ViewNode Progress(string id, Prop<double?> value) =>
        Node(id, ElementKind.Progress, [], ("value", value.ToJson()));

    public static ViewNode Separator(string id) => Node(id, ElementKind.Separator, []);

    public static ViewNode Spacer(string id, bool flex = false) => Node(id, ElementKind.Spacer, [], ("flex", flex));

    public static ElementAction Invoke(string commandId, JsonObject? args = null) => new(Invoke: new InvokeAction(commandId, args));

    public static ElementAction Event(string name) => new(Event: name);

    public static ElementAction Popup(string popupId) => new(Popup: popupId);

    private static string Lower<TEnum>(TEnum value) where TEnum : struct, Enum =>
        value.ToString().ToLowerInvariant();

    private static ViewNode Node(string id, ElementKind kind, string[] classes, params (string Name, JsonNode? Value)[] props)
    {
        var map = new Dictionary<string, JsonNode?>(StringComparer.Ordinal);
        foreach (var (name, value) in props)
        {
            if (value is not null)
            {
                map[name] = value;
            }
        }

        return new ViewNode(id, kind, classes.Length == 0 ? null : classes, map.Count == 0 ? null : map);
    }
}

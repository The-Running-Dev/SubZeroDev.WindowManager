using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SubZeroDev.WindowManager.Protocol;

public sealed record ViewNode(
    string Id, ElementKind Kind,
    IReadOnlyList<string>? Classes = null,
    IReadOnlyDictionary<string, JsonNode?>? Props = null,
    IReadOnlyList<ViewNode>? Children = null,
    ViewNode? ItemTemplate = null,
    ElementAction? Action = null,
    ElementAction? ContextAction = null);

public enum ElementKind { Text, Icon, Image, Button, Toggle, Stack, List, Progress, TextInput, Separator, Spacer }

/// <summary>Exactly one of <see cref="Invoke"/>, <see cref="Event"/> or <see cref="Popup"/> is set.</summary>
public sealed record ElementAction(InvokeAction? Invoke = null, string? Event = null, string? Popup = null);

public sealed record InvokeAction(string Command, JsonObject? Args = null);

public sealed record ValidationResult(bool IsValid, IReadOnlyList<ValidationIssue> Issues)
{
    public override string ToString() =>
        string.Join("; ", Issues.Select(issue => $"{issue.ElementId}: {issue.Code} ({issue.Detail})"));
}

public sealed record ValidationIssue(string ElementId, string Code, string Detail);

/// <summary>Enforces I11 on a view tree. The JSON overload is what the host runs on the wire form.</summary>
public static partial class ViewValidator
{
    public const int MaxDepth = 64;

    public static class Codes
    {
        public const string MissingId = "missingId";
        public const string DuplicateId = "duplicateId";
        public const string UnknownKind = "unknownKind";
        public const string ForbiddenProp = "forbiddenProp";
        public const string UnknownProp = "unknownProp";
        public const string ChildrenNotAllowed = "childrenNotAllowed";
        public const string ItemTemplateNotAllowed = "itemTemplateNotAllowed";
        public const string MissingAutomationName = "missingAutomationName";
        public const string InvalidBinding = "invalidBinding";
        public const string InvalidAction = "invalidAction";
        public const string InvalidClass = "invalidClass";
        public const string TooDeep = "tooDeep";
    }

    private static readonly HashSet<string> Forbidden = new(StringComparer.OrdinalIgnoreCase)
    {
        "background", "foreground", "fontSize", "fontFamily", "fontWeight", "fontStyle", "color", "brush", "fill",
        "stroke", "borderBrush", "borderThickness", "width", "height", "minWidth", "minHeight", "maxWidth",
        "maxHeight", "margin", "padding", "opacity", "cornerRadius",
    };

    private static readonly string[] Common = ["automationName", "tooltip", "visible"];

    private static readonly Dictionary<string, string[]> PropsByKind = new(StringComparer.Ordinal)
    {
        ["text"] = ["text", "wrap"],
        ["icon"] = ["icon", "size"],
        ["image"] = ["source", "fit"],
        ["button"] = ["label", "icon", "active", "attention", "badge"],
        ["toggle"] = ["label", "icon", "checked"],
        ["stack"] = ["orientation"],
        ["list"] = ["items", "orientation", "selection", "selectedKey"],
        ["progress"] = ["value"],
        ["textInput"] = ["value", "placeholder", "navigates"],
        ["separator"] = [],
        ["spacer"] = ["flex"],
    };

    private static readonly HashSet<string> NeedsAutomationName = new(StringComparer.Ordinal) { "button", "toggle", "textInput", "list" };

    public static ValidationResult Validate(ViewNode root) =>
        Validate(JsonSerializer.SerializeToNode(root, ProtocolJson.Options));

    public static ValidationResult Validate(JsonNode? root)
    {
        var issues = new List<ValidationIssue>();
        var ids = new HashSet<string>(StringComparer.Ordinal);
        Walk(root, depth: 1, insideTemplate: false, ids, issues);
        return new ValidationResult(issues.Count == 0, issues);
    }

    private static void Walk(JsonNode? node, int depth, bool insideTemplate, HashSet<string> ids, List<ValidationIssue> issues)
    {
        if (node is not JsonObject element)
        {
            issues.Add(new ValidationIssue(string.Empty, Codes.MissingId, "Element is not an object."));
            return;
        }

        var id = element["id"] is JsonValue idValue && idValue.TryGetValue<string>(out var text) ? text : null;
        var label = id ?? string.Empty;
        if (string.IsNullOrEmpty(id))
        {
            issues.Add(new ValidationIssue(label, Codes.MissingId, "Element has no id."));
        }
        else if (!ids.Add(id))
        {
            issues.Add(new ValidationIssue(label, Codes.DuplicateId, $"Id '{id}' is used more than once."));
        }

        if (depth > MaxDepth)
        {
            issues.Add(new ValidationIssue(label, Codes.TooDeep, $"Tree is deeper than {MaxDepth}."));
            return;
        }

        var kind = element["kind"] is JsonValue kindValue && kindValue.TryGetValue<string>(out var kindText) ? kindText : null;
        var known = kind is not null && PropsByKind.ContainsKey(kind);
        if (!known)
        {
            issues.Add(new ValidationIssue(label, Codes.UnknownKind, $"Kind '{kind}' is not known."));
        }

        if (element["classes"] is JsonArray classes)
        {
            foreach (var item in classes)
            {
                if (item is not JsonValue value || !value.TryGetValue<string>(out var name) || !ClassPattern().IsMatch(name))
                {
                    issues.Add(new ValidationIssue(label, Codes.InvalidClass, $"Class '{item?.ToJsonString()}' is not a valid name."));
                }
            }
        }

        var props = element["props"] as JsonObject;
        if (props is not null)
        {
            foreach (var (name, value) in props)
            {
                if (Forbidden.Contains(name))
                {
                    issues.Add(new ValidationIssue(label, Codes.ForbiddenProp, $"Prop '{name}' sets a visual the theme owns."));
                }
                else if (known && !Common.Contains(name, StringComparer.Ordinal) && !PropsByKind[kind!].Contains(name, StringComparer.Ordinal))
                {
                    issues.Add(new ValidationIssue(label, Codes.UnknownProp, $"Prop '{name}' is not defined for {kind}."));
                }

                CheckBindings(value, label, insideTemplate, issues);
            }
        }

        if (known && NeedsAutomationName.Contains(kind!) && props?["automationName"] is null)
        {
            issues.Add(new ValidationIssue(label, Codes.MissingAutomationName, $"{kind} needs an automationName."));
        }

        if (element["children"] is JsonArray children)
        {
            if (kind != "stack")
            {
                issues.Add(new ValidationIssue(label, Codes.ChildrenNotAllowed, "Only a Stack has children."));
            }

            foreach (var child in children)
            {
                Walk(child, depth + 1, insideTemplate, ids, issues);
            }
        }

        if (element["itemTemplate"] is { } template)
        {
            if (kind != "list")
            {
                issues.Add(new ValidationIssue(label, Codes.ItemTemplateNotAllowed, "Only a List has an item template."));
            }

            Walk(template, depth + 1, insideTemplate: true, ids, issues);
        }

        if (element["action"] is { } action)
        {
            CheckAction(action, label, insideTemplate, issues);
        }

        if (element["contextAction"] is { } contextAction)
        {
            CheckAction(contextAction, label, insideTemplate, issues);
        }
    }

    private static void CheckAction(JsonNode action, string label, bool insideTemplate, List<ValidationIssue> issues)
    {
        if (action is not JsonObject body)
        {
            issues.Add(new ValidationIssue(label, Codes.InvalidAction, "Action is not an object."));
            return;
        }

        var set = 0;
        if (body["invoke"] is JsonObject invoke)
        {
            set++;
            if (invoke["command"] is not JsonValue command || !command.TryGetValue<string>(out var commandId) || commandId.Length == 0)
            {
                issues.Add(new ValidationIssue(label, Codes.InvalidAction, "Invoke has no command."));
            }

            if (invoke["args"] is JsonObject args)
            {
                foreach (var (_, value) in args)
                {
                    CheckBindings(value, label, insideTemplate, issues);
                }
            }
        }

        set += IsText(body["event"]) ? 1 : 0;
        set += IsText(body["popup"]) ? 1 : 0;
        if (set != 1)
        {
            issues.Add(new ValidationIssue(label, Codes.InvalidAction, "An action sets exactly one of invoke, event or popup."));
        }
    }

    private static bool IsText(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0;

    private static void CheckBindings(JsonNode? value, string label, bool insideTemplate, List<ValidationIssue> issues)
    {
        switch (value)
        {
            case JsonObject obj when obj.Any(pair => pair.Key.StartsWith('$')):
                var valid = obj.Count == 1
                    && (obj["$state"] is { } || obj["$item"] is { })
                    && IsText(obj.First().Value);
                if (!valid)
                {
                    issues.Add(new ValidationIssue(label, Codes.InvalidBinding, $"Binding {obj.ToJsonString()} is not $state or $item with a name."));
                }
                else if (obj.ContainsKey("$item") && !insideTemplate)
                {
                    issues.Add(new ValidationIssue(label, Codes.InvalidBinding, "$item is only valid inside an item template."));
                }

                break;
            case JsonObject obj:
                foreach (var (_, child) in obj)
                {
                    CheckBindings(child, label, insideTemplate, issues);
                }

                break;
            case JsonArray array:
                foreach (var child in array)
                {
                    CheckBindings(child, label, insideTemplate, issues);
                }

                break;
        }
    }

    [GeneratedRegex("^[a-z][a-z0-9-]*$")]
    private static partial Regex ClassPattern();
}

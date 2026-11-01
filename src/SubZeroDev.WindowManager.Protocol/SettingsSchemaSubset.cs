using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SubZeroDev.WindowManager.Protocol;

/// <summary>
/// The JSON Schema subset that <c>settingsSchema</c> and <c>argsSchema</c> may use: an object root with
/// <c>properties</c>, each a string, number, integer, boolean or list of strings, plus the annotations
/// <c>title</c>, <c>description</c> and <c>default</c>. An empty schema (<c>{}</c>) declares no settings.
/// Properties a schema does not name are allowed and kept; <c>format</c> is an annotation for editors.
/// </summary>
public static class SettingsSchemaSubset
{
    private static readonly string[] Annotations = ["title", "description", "default"];

    public static bool IsEmpty(JsonObject? schema) => schema is null || schema.Count == 0;

    /// <summary>Adds a problem, naming the keyword, for anything in <paramref name="schema"/> outside the subset.</summary>
    public static void Check(JsonNode? schema, string where, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(problems);
        if (schema is null)
        {
            return;
        }

        if (schema is not JsonObject root)
        {
            problems.Add($"{where} is not an object.");
            return;
        }

        if (IsEmpty(root))
        {
            return;
        }

        foreach (var (keyword, _) in root)
        {
            if (keyword is not ("type" or "properties") && !Annotations.Contains(keyword))
            {
                problems.Add($"{where} uses '{keyword}', which is outside the settings schema subset.");
            }
        }

        if (Text(root, "type") != "object")
        {
            problems.Add($"{where} must have type 'object'.");
        }

        if (root["properties"] is not JsonObject properties)
        {
            problems.Add($"{where} must have 'properties'.");
            return;
        }

        foreach (var (name, node) in properties)
        {
            var at = $"{where} property '{name}'";
            if (node is not JsonObject property)
            {
                problems.Add($"{at} is not an object.");
                continue;
            }

            CheckProperty(property, at, problems);
        }
    }

    /// <summary>The problems with <paramref name="settings"/> against <paramref name="schema"/>; empty when they are valid.</summary>
    public static List<string> Validate(JsonObject? schema, JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var problems = new List<string>();
        if (IsEmpty(schema) || schema!["properties"] is not JsonObject properties)
        {
            return problems;
        }

        foreach (var (name, node) in properties)
        {
            if (node is JsonObject property && settings.TryGetPropertyValue(name, out var value))
            {
                ValidateValue(property, value, $"'{name}'", problems);
            }
        }

        return problems;
    }

    /// <summary>A settings object holding each property's <c>default</c>.</summary>
    public static JsonObject Defaults(JsonObject? schema)
    {
        var defaults = new JsonObject();
        if (schema?["properties"] is JsonObject properties)
        {
            foreach (var (name, node) in properties)
            {
                if (node is JsonObject property && property.TryGetPropertyValue("default", out var value))
                {
                    defaults[name] = value?.DeepClone();
                }
            }
        }

        return defaults;
    }

    /// <summary>The defaults with <paramref name="settings"/> laid over them.</summary>
    public static JsonObject WithDefaults(JsonObject? schema, JsonObject settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var merged = Defaults(schema);
        foreach (var (name, value) in settings)
        {
            merged[name] = value?.DeepClone();
        }

        return merged;
    }

    private static void CheckProperty(JsonObject property, string at, List<string> problems)
    {
        var type = Text(property, "type");
        string[] allowed = type switch
        {
            "string" => ["enum", "maxLength", "format"],
            "number" or "integer" => ["minimum", "maximum"],
            "boolean" => [],
            "array" => ["items"],
            _ => [],
        };
        if (type is not ("string" or "number" or "integer" or "boolean" or "array"))
        {
            problems.Add($"{at} has type '{type}'; the subset allows string, number, integer, boolean and array.");
        }

        foreach (var (keyword, _) in property)
        {
            if (keyword != "type" && !Annotations.Contains(keyword) && !allowed.Contains(keyword))
            {
                problems.Add($"{at} uses '{keyword}', which is outside the settings schema subset.");
            }
        }

        if (property["enum"] is { } choices
            && !(choices is JsonArray list && list.Count > 0 && list.All(item => item?.GetValueKind() == JsonValueKind.String)))
        {
            problems.Add($"{at} has an 'enum' that is not a list of strings.");
        }

        if (property["maxLength"] is { } maxLength && !(Number(maxLength) is { } length && length >= 0 && length == Math.Floor(length)))
        {
            problems.Add($"{at} has a 'maxLength' that is not a whole number.");
        }

        if (property["format"] is { } format && Text(property, "format") is not ("hotkey" or "path"))
        {
            problems.Add($"{at} has format {format.ToJsonString()}; the subset allows 'hotkey' and 'path'.");
        }

        foreach (var bound in new[] { "minimum", "maximum" })
        {
            if (property[bound] is { } limit && Number(limit) is null)
            {
                problems.Add($"{at} has a '{bound}' that is not a number.");
            }
        }

        if (type == "array")
        {
            if (property["items"] is not JsonObject items || Text(items, "type") != "string"
                || items.Any(pair => pair.Key != "type" && !Annotations.Contains(pair.Key)))
            {
                problems.Add($"{at} must have 'items' of type 'string' and nothing else.");
            }
        }

        if (property.TryGetPropertyValue("default", out var value))
        {
            var before = problems.Count;
            ValidateValue(property, value, "its default", problems);
            for (var i = before; i < problems.Count; i++)
            {
                problems[i] = $"{at}: {problems[i]}";
            }
        }
    }

    private static void ValidateValue(JsonObject property, JsonNode? value, string name, List<string> problems)
    {
        switch (Text(property, "type"))
        {
            case "string":
                if (value?.GetValueKind() != JsonValueKind.String)
                {
                    problems.Add($"{name} must be a string.");
                    return;
                }

                var text = value.GetValue<string>();
                if (property["enum"] is JsonArray choices && !choices.Any(choice => choice?.GetValueKind() == JsonValueKind.String
                        && choice.GetValue<string>() == text))
                {
                    problems.Add($"{name} must be one of {choices.ToJsonString()}.");
                }

                if (property["maxLength"] is { } maxLength && Number(maxLength) is { } length && text.Length > length)
                {
                    problems.Add($"{name} is longer than {length.ToString(CultureInfo.InvariantCulture)} characters.");
                }

                break;
            case "number" or "integer":
                if (Number(value) is not { } number)
                {
                    problems.Add($"{name} must be a number.");
                    return;
                }

                if (Text(property, "type") == "integer" && number != Math.Floor(number))
                {
                    problems.Add($"{name} must be a whole number.");
                }

                if (property["minimum"] is { } minimum && Number(minimum) is { } low && number < low)
                {
                    problems.Add($"{name} is below the minimum {low.ToString(CultureInfo.InvariantCulture)}.");
                }

                if (property["maximum"] is { } maximum && Number(maximum) is { } high && number > high)
                {
                    problems.Add($"{name} is above the maximum {high.ToString(CultureInfo.InvariantCulture)}.");
                }

                break;
            case "boolean":
                if (value?.GetValueKind() is not (JsonValueKind.True or JsonValueKind.False))
                {
                    problems.Add($"{name} must be true or false.");
                }

                break;
            case "array":
                if (value is not JsonArray items || items.Any(item => item?.GetValueKind() != JsonValueKind.String))
                {
                    problems.Add($"{name} must be a list of strings.");
                }

                break;
        }
    }

    private static double? Number(JsonNode? node) =>
        node?.GetValueKind() == JsonValueKind.Number
            ? double.Parse(node.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture)
            : null;

    private static string? Text(JsonObject json, string name) =>
        json[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

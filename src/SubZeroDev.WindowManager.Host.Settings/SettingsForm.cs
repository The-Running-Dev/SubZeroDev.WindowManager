using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace SubZeroDev.WindowManager.Host.Settings;

/// <summary>
/// A form generated from a settings schema (the subset in <c>SettingsSchemaSubset</c>): a string with
/// <c>enum</c> is a choice list, a boolean a toggle, an integer or number a number box bounded by
/// <c>minimum</c> and <c>maximum</c>, another string a text box and a list of strings a text box with one
/// item per line. Properties the schema does not name are kept as they are.
/// </summary>
public sealed class SettingsForm
{
    private readonly JsonObject _settings;
    private readonly List<Field> _fields = [];

    /// <param name="scope">Prefix for each control's automation id, unique within the window.</param>
    public SettingsForm(JsonObject? schema, JsonObject settings, string scope)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _settings = (JsonObject)settings.DeepClone();
        Root = new StackPanel { Spacing = 8 };
        if (schema?["properties"] is not JsonObject properties || properties.Count == 0)
        {
            Root.Children.Add(new TextBlock { Text = "No settings.", Opacity = 0.7 });
            return;
        }

        foreach (var (name, node) in properties)
        {
            if (node is JsonObject property)
            {
                var field = Create(name, property, _settings[name], $"{scope}.{name}");
                _fields.Add(field);
                Root.Children.Add(field.Control);
            }
        }
    }

    public StackPanel Root { get; }

    /// <summary>The fields in schema order: setting name, control kind, automation id and the value it shows.</summary>
    public IEnumerable<(string Name, string Kind, string AutomationId, JsonNode? Value)> Fields =>
        _fields.Select(item => (item.Name, item.Kind, AutomationProperties.GetAutomationId(item.Control), item.Read()));

    /// <summary>The settings as the controls hold them, laid over the ones the form started with.</summary>
    public JsonObject Read()
    {
        var result = (JsonObject)_settings.DeepClone();
        foreach (var field in _fields)
        {
            result[field.Name] = field.Read();
        }

        return result;
    }

    private static Field Create(string name, JsonObject property, JsonNode? value, string automationId)
    {
        var title = property["title"] is JsonValue t && t.TryGetValue<string>(out var text) ? text : name;
        var description = property["description"] is JsonValue d && d.TryGetValue<string>(out var about) ? about : null;
        var type = property["type"] is JsonValue k && k.TryGetValue<string>(out var kind) ? kind : null;
        FrameworkElement control;
        Func<JsonNode?> read;
        string fieldKind;
        if (type == "string" && property["enum"] is JsonArray choices)
        {
            var items = choices.Select(choice => choice?.GetValue<string>() ?? string.Empty).ToList();
            var box = new ComboBox { Header = title, ItemsSource = items, MinWidth = 200 };
            box.SelectedIndex = items.IndexOf(Text(value) ?? string.Empty);
            control = box;
            read = () => box.SelectedItem is string chosen ? JsonValue.Create(chosen) : value?.DeepClone();
            fieldKind = "choice";
        }
        else if (type == "boolean")
        {
            var toggle = new ToggleSwitch { Header = title, IsOn = value is JsonValue b && b.TryGetValue<bool>(out var on) && on };
            control = toggle;
            read = () => JsonValue.Create(toggle.IsOn);
            fieldKind = "toggle";
        }
        else if (type is "integer" or "number")
        {
            var box = new NumberBox
            {
                Header = title,
                SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Inline,
                SmallChange = 1,
                Minimum = Number(property["minimum"]) ?? double.MinValue,
                Maximum = Number(property["maximum"]) ?? double.MaxValue,
                Value = Number(value) ?? double.NaN,
                MinWidth = 200,
            };
            if (type == "integer")
            {
                box.NumberFormatter = new Windows.Globalization.NumberFormatting.DecimalFormatter
                {
                    FractionDigits = 0,
                    NumberRounder = new Windows.Globalization.NumberFormatting.IncrementNumberRounder { Increment = 1 },
                };
            }

            control = box;
            read = () => double.IsNaN(box.Value)
                ? value?.DeepClone()
                : type == "integer" ? JsonValue.Create((long)Math.Round(box.Value)) : JsonValue.Create(box.Value);
            fieldKind = "number";
        }
        else if (type == "array")
        {
            var lines = value is JsonArray list ? string.Join("\r", list.Select(Text)) : string.Empty;
            var box = new TextBox { Header = title, Text = lines, AcceptsReturn = true, MinWidth = 200 };
            control = box;
            read = () => new JsonArray([.. box.Text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Select(line => (JsonNode?)JsonValue.Create(line.Trim()))]);
            fieldKind = "list";
        }
        else
        {
            var box = new TextBox { Header = title, Text = Text(value) ?? string.Empty, MinWidth = 200 };
            control = box;
            read = () => JsonValue.Create(box.Text);
            fieldKind = "text";
        }

        AutomationProperties.SetName(control, title);
        AutomationProperties.SetAutomationId(control, automationId);
        if (description is not null)
        {
            AutomationProperties.SetHelpText(control, description);
            ToolTipService.SetToolTip(control, description);
        }

        return new Field(name, fieldKind, control, read);
    }

    private static string? Text(JsonNode? node) =>
        node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static double? Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number
            ? double.Parse(value.ToJsonString(), CultureInfo.InvariantCulture)
            : null;

    private sealed record Field(string Name, string Kind, FrameworkElement Control, Func<JsonNode?> Read);
}

using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Plugins.Tray;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<TrayPlugin>(args);
}

/// <summary>
/// Shows the host's <c>host.tray.icons</c> feed as the <c>tray</c> panel item: one button per visible
/// notification icon, and the latest balloon as a notice. Clicks go back through <c>host.tray.invoke</c>.
/// </summary>
public sealed class TrayPlugin : Plugin
{
    public const string Feed = "host.tray.icons";

    private readonly Lock _gate = new();
    private JsonArray _icons = [];
    private IDisposable? _subscription;

    internal event EventHandler? Changed;

    internal JsonArray Icons
    {
        get
        {
            lock (_gate)
            {
                return (JsonArray)_icons.DeepClone();
            }
        }
    }

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _subscription = Context.Feeds.Subscribe(Feed, (update, _) =>
        {
            lock (_gate)
            {
                _icons = update is { Available: true, Snapshot: JsonArray icons } ? (JsonArray)icons.DeepClone() : [];
            }

            Changed?.Invoke(this, EventArgs.Empty);
            return Task.CompletedTask;
        });
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        _subscription?.Dispose();
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new TrayInstance(request, this);
}

/// <summary>The icons, each with its tooltip; a click selects, a right click asks for the icon's menu.</summary>
public sealed class TrayInstance(InstanceRequest request, TrayPlugin plugin) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        var key = new JsonObject { ["$item"] = "key" };
        var tooltip = new JsonObject { ["$item"] = "tooltip" };
        var button = View.Button("icon", "Notification icon",
            View.Invoke("host.tray.invoke", new JsonObject { ["key"] = key, ["action"] = "select" }),
            icon: Prop<IconRef>.Item("icon"), classes: ["tray"])
            .WithContextAction(View.Invoke("host.tray.invoke", new JsonObject { ["key"] = key.DeepClone(), ["action"] = "contextMenu" }));
        var props = new Dictionary<string, JsonNode?>(button.Props!, StringComparer.Ordinal)
        {
            ["automationName"] = tooltip,
            ["tooltip"] = tooltip.DeepClone(),
        };

        Surface.SetView(View.Stack("tray", StackOrientation.Horizontal,
            View.Text("balloon", Prop<string>.State("balloon"), "notice"),
            View.List("icons", "Notification area", Prop<JsonArray>.State("items"), button with { Props = props },
                StackOrientation.Horizontal)));
        plugin.Changed += OnChanged;
        Show(plugin.Icons);
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        plugin.Changed -= OnChanged;
        return base.DisposeAsync();
    }

    private void OnChanged(object? sender, EventArgs e) => Show(plugin.Icons);

    private void Show(JsonArray icons)
    {
        var items = new JsonArray();
        JsonObject? latest = null;
        long latestAt = long.MinValue;
        foreach (var icon in icons.OfType<JsonObject>())
        {
            if (icon["hidden"] is JsonValue hidden && hidden.TryGetValue<bool>(out var isHidden) && isHidden)
            {
                continue;
            }

            var item = (JsonObject)icon.DeepClone();
            item["tooltip"] = Text(icon["tooltip"]) is { Length: > 0 } text ? text : "Notification icon";
            items.Add(item);
            if (icon["balloon"] is JsonObject balloon
                && balloon["at"] is JsonValue at && at.TryGetValue<long>(out var when) && when >= latestAt)
            {
                latest = balloon;
                latestAt = when;
            }
        }

        Surface.SetState("items", items);
        Surface.SetState("balloon", latest is null ? string.Empty : Balloon(Text(latest["title"]), Text(latest["text"])));
    }

    internal static string Balloon(string? title, string? text) =>
        string.IsNullOrEmpty(title) ? text ?? string.Empty
        : string.IsNullOrEmpty(text) ? title
        : $"{title}: {text}";

    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

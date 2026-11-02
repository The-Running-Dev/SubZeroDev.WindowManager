using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Plugins.RunningWindows;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<RunningWindowsPlugin>(args);
}

/// <summary>
/// Publishes <c>windows.running</c>, serves <c>windows.activate</c> and <c>windows.minimize</c>, and shows
/// the <c>windows</c> panel item: one button per window, on every monitor or only the panel's own.
/// </summary>
public sealed class RunningWindowsPlugin : Plugin
{
    public const string Feed = "windows.running";

    private readonly WindowTracker _tracker = new();

    internal WindowTracker Tracker => _tracker;

    protected override async Task OnStartAsync(CancellationToken ct)
    {
        var publisher = Context.Feeds.Publisher(Feed);
        _tracker.Changed += (_, windows) => publisher.Publish(Snapshot(windows));
        Context.Commands.Register("windows.activate", ActivateAsync);
        Context.Commands.Register("windows.minimize", (command, token) => ForwardAsync("host.window.minimize", command, token));
        await _tracker.StartAsync().ConfigureAwait(false);
        publisher.Publish(Snapshot(_tracker.Windows));
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        _tracker.Dispose();
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new WindowListInstance(request, _tracker);

    internal static JsonArray Snapshot(IEnumerable<RunningWindow> windows) => [.. windows.Select(window => window.ToJson())];

    /// <summary>Brings a window to the front, or minimises it when it already is in front, as a taskbar does.</summary>
    private Task<CommandResult> ActivateAsync(CommandContext command, CancellationToken ct)
    {
        var hwnd = Hwnd(command);
        var window = _tracker.Windows.FirstOrDefault(candidate => candidate.Hwnd == hwnd);
        var target = window is { Active: true, Minimized: false } ? "host.window.minimize" : "host.window.activate";
        return ForwardAsync(target, command, ct);
    }

    private async Task<CommandResult> ForwardAsync(string hostCommand, CommandContext command, CancellationToken ct)
    {
        var result = await Context.Commands.InvokeAsync(hostCommand, new JsonObject { ["hwnd"] = Hwnd(command) }, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw new InvalidOperationException($"{hostCommand} failed: {error.Detail}");
        }

        return new CommandResult(null);
    }

    private static long Hwnd(CommandContext command) =>
        command.Args?["hwnd"] is JsonValue value && value.TryGetValue<long>(out var hwnd) && hwnd != 0
            ? hwnd
            : throw new ArgumentException("hwnd must be a window handle.");
}

/// <summary>One button per running window, labelled with its title and icon, marked while it is active.</summary>
public sealed class WindowListInstance(InstanceRequest request, WindowTracker tracker) : ContributionInstance(request)
{
    private readonly string _monitorId = request.Context.MonitorId;
    private volatile bool _thisMonitor = ThisMonitor(request.Settings);

    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        var title = new JsonObject { ["$item"] = "title" };
        var button = View.Button("window", "Window",
            View.Invoke("windows.activate", new JsonObject { ["hwnd"] = new JsonObject { ["$item"] = "hwnd" } }),
            label: Prop<string>.Item("title"), icon: Prop<IconRef>.Item("icon"), active: Prop<bool>.Item("active"), "task")
            .WithAttention(Prop<bool>.Item("attention"));
        var props = new Dictionary<string, JsonNode?>(button.Props!, StringComparer.Ordinal)
        {
            ["automationName"] = title,
            ["tooltip"] = title.DeepClone(),
        };

        Surface.SetView(View.List("windows", "Running windows", Prop<JsonArray>.State("items"), button with { Props = props }));
        tracker.Changed += OnChanged;
        Show(tracker.Windows);
        return Task.CompletedTask;
    }

    protected override Task OnSettingsChangedAsync(JsonObject settings, CancellationToken ct)
    {
        _thisMonitor = ThisMonitor(settings);
        Show(tracker.Windows);
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        tracker.Changed -= OnChanged;
        return base.DisposeAsync();
    }

    private void OnChanged(object? sender, IReadOnlyList<RunningWindow> windows) => Show(windows);

    private void Show(IReadOnlyList<RunningWindow> windows)
    {
        var items = new JsonArray();
        foreach (var window in windows.Where(window => !_thisMonitor || string.Equals(window.MonitorId, _monitorId, StringComparison.OrdinalIgnoreCase)))
        {
            var item = window.ToJson();
            item["icon"] = new JsonObject { ["window"] = window.Hwnd };
            items.Add(item);
        }

        Surface.SetState("items", items);
    }

    private static bool ThisMonitor(JsonObject settings) =>
        settings["scope"] is JsonValue value && value.TryGetValue<string>(out var scope) && scope == "thisMonitor";
}

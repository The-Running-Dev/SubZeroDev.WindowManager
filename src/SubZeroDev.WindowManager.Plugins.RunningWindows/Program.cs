using System.Globalization;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Plugins.RunningWindows;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<RunningWindowsPlugin>(args);
}

/// <summary>
/// An adapter over the shell's window model: reads <c>host.windows</c> and <c>host.desktops</c>, publishes the taskbar's
/// view of them as <c>windows.running</c>, serves <c>windows.activate</c> and <c>windows.minimize</c> through the host
/// commands, and shows the <c>windows</c> panel item: one button per window, on every monitor or only the panel's own.
/// It tracks nothing itself (I39).
/// </summary>
public sealed class RunningWindowsPlugin : Plugin
{
    public const string Feed = "windows.running";

    private ShellViewSource? _view;

    internal ShellView? Current => _view?.Current;

    internal event Action<ShellView>? Changed;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        var publisher = Context.Feeds.Publisher(Feed);
        var view = _view = new ShellViewSource(Context.Feeds);
        view.Changed += shell =>
        {
            publisher.Publish(Snapshot(shell));
            Changed?.Invoke(shell);
        };
        Context.Commands.Register("windows.activate", ActivateAsync);
        Context.Commands.Register("windows.minimize", (command, token) => ForwardAsync("host.window.minimize", command, token));
        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        _view?.Dispose();
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new WindowListInstance(request, this);

    /// <summary>The <c>windows.running</c> snapshot: the taskbar's windows for the current desktop, in first-seen order.</summary>
    internal static JsonArray Snapshot(ShellView view) => [.. view.RunningWindows().Select(Item)];

    /// <summary>The first-release item, plus the additive <c>windowKey</c>, <c>groupKey</c> and <c>desktopId</c>.</summary>
    internal static JsonObject Item(WindowFeedItem window) => new()
    {
        ["key"] = window.Hwnd.ToString(CultureInfo.InvariantCulture),
        ["hwnd"] = window.Hwnd,
        ["title"] = window.Title,
        ["exe"] = window.Exe,
        ["aumid"] = window.Aumid,
        ["monitorId"] = window.MonitorId,
        ["active"] = window.Active,
        ["minimized"] = window.Minimized,
        ["attention"] = window.Attention,
        ["windowKey"] = window.WindowKey,
        ["groupKey"] = window.GroupKey,
        ["desktopId"] = window.DesktopId,
    };

    /// <summary>Brings a window to the front, or minimises it when it already is in front, as a taskbar does.</summary>
    private Task<CommandResult> ActivateAsync(CommandContext command, CancellationToken ct)
    {
        var window = Find(command);
        var target = window is { Active: true, Minimized: false } ? "host.window.minimize" : "host.window.activate";
        return ForwardAsync(target, command, ct);
    }

    private WindowFeedItem? Find(CommandContext command)
    {
        var windows = _view?.Current?.Windows.Windows;
        if (windows is null)
        {
            return null;
        }

        return Key(command) is { } key
            ? windows.FirstOrDefault(candidate => candidate.WindowKey == key)
            : Hwnd(command) is { } hwnd ? windows.FirstOrDefault(candidate => candidate.Hwnd == hwnd) : null;
    }

    /// <summary>Forwards by the window key when the command carries one or the model knows the window, else by the handle.</summary>
    private async Task<CommandResult> ForwardAsync(string hostCommand, CommandContext command, CancellationToken ct)
    {
        var args = new JsonObject();
        if ((Key(command) ?? Find(command)?.WindowKey) is { } key)
        {
            args["windowKey"] = key;
        }
        else if (Hwnd(command) is { } hwnd)
        {
            args["hwnd"] = hwnd;
        }
        else
        {
            throw new ArgumentException("hwnd or windowKey must identify a window.");
        }

        var result = await Context.Commands.InvokeAsync(hostCommand, args, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw new CommandFailedException(error.Name ?? error.Code.ToString(), $"{hostCommand} failed: {error.Detail}", error.Retryable);
        }

        return new CommandResult(result.Value?.Value);
    }

    private static string? Key(CommandContext command) =>
        command.Args?["windowKey"] is JsonValue value && value.TryGetValue<string>(out var key) && key.Length > 0 ? key : null;

    private static long? Hwnd(CommandContext command) =>
        command.Args?["hwnd"] is JsonValue value && value.TryGetValue<long>(out var hwnd) && hwnd != 0 ? hwnd : null;
}

/// <summary>One button per running window, labelled with its title and icon, marked while it is active.</summary>
public sealed class WindowListInstance(InstanceRequest request, RunningWindowsPlugin plugin) : ContributionInstance(request)
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
        plugin.Changed += Show;
        if (plugin.Current is { } current)
        {
            Show(current);
        }

        return Task.CompletedTask;
    }

    protected override Task OnSettingsChangedAsync(JsonObject settings, CancellationToken ct)
    {
        _thisMonitor = ThisMonitor(settings);
        if (plugin.Current is { } current)
        {
            Show(current);
        }

        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        plugin.Changed -= Show;
        return base.DisposeAsync();
    }

    private void Show(ShellView view)
    {
        var items = new JsonArray();
        foreach (var window in view.RunningWindows().Where(window => !_thisMonitor || string.Equals(window.MonitorId, _monitorId, StringComparison.OrdinalIgnoreCase)))
        {
            var item = RunningWindowsPlugin.Item(window);
            item["icon"] = new JsonObject { ["window"] = window.Hwnd };
            items.Add(item);
        }

        Surface.SetState("items", items);
    }

    private static bool ThisMonitor(JsonObject settings) =>
        settings["scope"] is JsonValue value && value.TryGetValue<string>(out var scope) && scope == "thisMonitor";
}

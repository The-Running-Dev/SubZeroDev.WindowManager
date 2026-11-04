using System.Globalization;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Plugins.Desktops;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<DesktopsPlugin>(args);
}

/// <summary>
/// The overview of the shell's virtual desktops (S26): one tile per desktop with a live picture of each of its windows,
/// opened by Win+Tab or the panel item. It owns no state about windows or desktops (I39): it reads
/// <c>host.windows</c> and <c>host.desktops</c> as one view and asks the host to switch, create and move.
/// </summary>
public sealed class DesktopsPlugin : Plugin
{
    public const string PopupId = "overview";

    private readonly Lock _gate = new();
    private ShellViewSource? _view;
    private PopupSurface _popup = null!;
    private bool _open;

    internal ShellView? Current => _view?.Current;

    internal event Action<ShellView>? Changed;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _popup = Context.Popups.Get(PopupId);
        _popup.SetView(OverviewView());
        var view = _view = new ShellViewSource(Context.Feeds);
        view.Changed += shell =>
        {
            ShowOverview(shell);
            Changed?.Invoke(shell);
        };
        Context.Commands.Register("desktops.openOverview", OpenAsync);
        Context.Commands.Register("desktops.closeOverview", CloseAsync);
        Context.Commands.Register("desktops.switchTo", SwitchToAsync);
        Context.Commands.Register("desktops.create", CreateAsync);
        Context.Commands.Register("desktops.activateWindow", ActivateWindowAsync);
        Context.Commands.Register("desktops.moveWindow", MoveWindowAsync);
        if (view.Current is { } current)
        {
            ShowOverview(current);
        }

        return Task.CompletedTask;
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        _view?.Dispose();
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new CurrentDesktopInstance(request, this);

    protected override Task OnPopupOpenedAsync(PopupSurface popup, PopupTrigger trigger, CancellationToken ct)
    {
        lock (_gate)
        {
            _open = true;
        }

        if (Current is { } current)
        {
            ShowOverview(current);
        }

        return Task.CompletedTask;
    }

    protected override Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct)
    {
        lock (_gate)
        {
            _open = false;
        }

        return Task.CompletedTask;
    }

    /// <summary>The name a desktop shows under: its own, or "Desktop" and its place in the list.</summary>
    /// <param name="view">The shell view the desktop is in.</param>
    /// <param name="index">The desktop's place in the list, from zero.</param>
    internal static string NameOf(ShellView view, int index) =>
        view.Desktops.Desktops[index].Name is { Length: > 0 } name
            ? name
            : string.Create(CultureInfo.InvariantCulture, $"Desktop {index + 1}");

    /// <summary>The windows an overview lists: titled application windows, including those the shell hid for another desktop.</summary>
    /// <param name="window">The window.</param>
    internal static bool Listed(WindowFeedItem window) =>
        window.Titled && window.Cloak == "none" && (window.Visible || window.ShellHidden)
        && (window.AppWindow || (window.OwnerKey is null && !window.ToolWindow && !window.NoActivate));

    /// <summary>The overview's items: a desktop each, with its windows in first-seen order.</summary>
    /// <param name="view">The shell view to draw.</param>
    internal static JsonArray Tiles(ShellView view)
    {
        var tiles = new JsonArray();
        for (var i = 0; i < view.Desktops.Desktops.Count; i++)
        {
            var desktop = view.Desktops.Desktops[i];
            var windows = new JsonArray();
            foreach (var window in view.Windows.Windows
                .Where(w => Listed(w) && string.Equals(w.DesktopId, desktop.Id, StringComparison.OrdinalIgnoreCase))
                .OrderBy(w => w.FirstSeen))
            {
                windows.Add(new JsonObject
                {
                    ["key"] = window.WindowKey,
                    ["windowKey"] = window.WindowKey,
                    ["title"] = window.Title,
                    ["attention"] = window.Attention,
                });
            }

            tiles.Add(new JsonObject
            {
                ["key"] = desktop.Id,
                ["id"] = desktop.Id,
                ["name"] = NameOf(view, i),
                ["current"] = string.Equals(desktop.Id, view.Desktops.Current, StringComparison.OrdinalIgnoreCase),
                ["attention"] = desktop.HasAttention,
                ["windows"] = windows,
            });
        }

        return tiles;
    }

    private static ViewNode OverviewView()
    {
        var thumbnail = View.Thumbnail("picture", Prop<string>.Item("windowKey"), ThumbnailSize.Medium);
        var moveArgs = new JsonObject { ["windowKey"] = new JsonObject { ["$item"] = "windowKey" } };
        var goArgs = new JsonObject { ["windowKey"] = new JsonObject { ["$item"] = "windowKey" } };
        var window = View.Stack("window", StackOrientation.Vertical,
            thumbnail.WithContextAction(View.Invoke("desktops.moveWindow", moveArgs)),
            View.Button("go", "Go to window", View.Invoke("desktops.activateWindow", goArgs),
                Prop<string>.Item("title"), null, null, "overview-window")
                .WithAttention(Prop<bool>.Item("attention"))
                .WithContextAction(View.Invoke("desktops.moveWindow", moveArgs)));
        var tile = View.Stack("tile", StackOrientation.Vertical,
            View.Button("desktop", "Desktop",
                View.Invoke("desktops.switchTo", new JsonObject { ["id"] = new JsonObject { ["$item"] = "id" } }),
                Prop<string>.Item("name"), null, Prop<bool>.Item("current"), "overview-desktop")
                .WithAttention(Prop<bool>.Item("attention")),
            View.List("windows", "Windows on the desktop", Prop<JsonArray>.Item("windows"), window, StackOrientation.Horizontal));
        return View.Stack("overview", StackOrientation.Vertical,
            View.List("desktops", "Desktops", Prop<JsonArray>.State("tiles"), tile, StackOrientation.Horizontal),
            View.Button("new", "Add a desktop", View.Invoke("desktops.create"),
                Prop<string>.State("newLabel"), null, null, "overview-new"));
    }

    private void ShowOverview(ShellView view)
    {
        _popup.SetState("tiles", Tiles(view));
        _popup.SetState("newLabel", "New desktop");
    }

    private async Task<CommandResult> OpenAsync(CommandContext command, CancellationToken ct)
    {
        if (Current is { } current)
        {
            ShowOverview(current);
        }

        var opened = await _popup.OpenAsync(new PopupAnchor(null, MonitorAnchor.Pointer), ct).ConfigureAwait(false);
        if (opened.Error is { } error)
        {
            throw new InvalidOperationException($"The overview could not open: {error.Detail}");
        }

        return new CommandResult(null);
    }

    private async Task<CommandResult> CloseAsync(CommandContext command, CancellationToken ct)
    {
        await CloseQuietlyAsync(ct).ConfigureAwait(false);
        return new CommandResult(null);
    }

    private async Task<CommandResult> SwitchToAsync(CommandContext command, CancellationToken ct)
    {
        var id = Text(command, "id") ?? throw new ArgumentException("id must name a desktop.");
        var result = await ForwardAsync("host.desktops.switch", new JsonObject { ["id"] = id }, ct).ConfigureAwait(false);
        await CloseQuietlyAsync(ct).ConfigureAwait(false);
        return result;
    }

    private Task<CommandResult> CreateAsync(CommandContext command, CancellationToken ct) =>
        ForwardAsync("host.desktops.create", new JsonObject { ["switchTo"] = false }, ct);

    private async Task<CommandResult> ActivateWindowAsync(CommandContext command, CancellationToken ct)
    {
        var key = Text(command, "windowKey") ?? throw new ArgumentException("windowKey must name a window.");
        var result = await ForwardAsync("host.window.activate", new JsonObject { ["windowKey"] = key }, ct).ConfigureAwait(false);
        await CloseQuietlyAsync(ct).ConfigureAwait(false);
        return result;
    }

    /// <summary>
    /// Files the window on the desktop after the one it is on. The view vocabulary has no drag, so a right click
    /// stands in for dragging a thumbnail onto another tile.
    /// </summary>
    private Task<CommandResult> MoveWindowAsync(CommandContext command, CancellationToken ct)
    {
        var key = Text(command, "windowKey") ?? throw new ArgumentException("windowKey must name a window.");
        var view = Current ?? throw new InvalidOperationException("The desktops are not known yet.");
        var window = view.Windows.Windows.FirstOrDefault(candidate => candidate.WindowKey == key)
            ?? throw new ArgumentException("The window is gone.");
        var desktops = view.Desktops.Desktops;
        var at = desktops.ToList().FindIndex(d => string.Equals(d.Id, window.DesktopId, StringComparison.OrdinalIgnoreCase));
        var target = desktops[(at + 1) % desktops.Count].Id;
        return ForwardAsync("host.window.moveToDesktop", new JsonObject { ["windowKey"] = key, ["id"] = target }, ct);
    }

    private async Task<CommandResult> ForwardAsync(string hostCommand, JsonObject args, CancellationToken ct)
    {
        var result = await Context.Commands.InvokeAsync(hostCommand, args, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            throw new CommandFailedException(error.Name ?? error.Code.ToString(), $"{hostCommand} failed: {error.Detail}", error.Retryable);
        }

        return new CommandResult(result.Value?.Value);
    }

    private async Task CloseQuietlyAsync(CancellationToken ct)
    {
        bool open;
        lock (_gate)
        {
            open = _open;
        }

        if (open)
        {
            await _popup.CloseAsync(ct).ConfigureAwait(false);
        }
    }

    private static string? Text(CommandContext command, string name) =>
        command.Args?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;
}

/// <summary>The panel item: the current desktop's name; a click opens the overview.</summary>
public sealed class CurrentDesktopInstance(InstanceRequest request, DesktopsPlugin plugin) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Button("desktop", "Desktops", View.Invoke("desktops.openOverview"),
            Prop<string>.State("name"), null, null, "desktops"));
        Surface.SetState("name", "Desktop 1");
        plugin.Changed += Show;
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
        var current = view.Desktops.Desktops.ToList()
            .FindIndex(d => string.Equals(d.Id, view.Desktops.Current, StringComparison.OrdinalIgnoreCase));
        Surface.SetState("name", current < 0 ? "Desktop" : DesktopsPlugin.NameOf(view, current));
    }
}

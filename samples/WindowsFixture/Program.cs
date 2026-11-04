using System.Globalization;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace Example.Windows;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<WindowsFixturePlugin>(args);
}

/// <summary>
/// Built outside the repository against the published SDK package alone (S25.9). It reads both shell feeds as one view
/// and acts on a window by its key, passing the host's error name back so a test can see <c>StaleWindowReference</c>
/// and <c>WindowGone</c>.
/// </summary>
public sealed class WindowsFixturePlugin : Plugin
{
    private ShellViewSource? _view;

    internal ShellView? Current => _view?.Current;

    internal event Action<ShellView>? Changed;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        var view = _view = new ShellViewSource(Context.Feeds);
        view.Changed += shell => Changed?.Invoke(shell);
        Context.Commands.Register("example.windows.activate", (command, token) => ActAsync("host.window.activate", command, token));
        Context.Commands.Register("example.windows.minimize", (command, token) => ActAsync("host.window.minimize", command, token));
        Context.Commands.Register("example.windows.switchDesktop", (command, token) => ForwardAsync(
            "host.desktops.switch", new JsonObject { ["id"] = Text(command, "id") }, token));
        Context.Commands.Register("example.windows.moveWindow", (command, token) => ForwardAsync(
            "host.window.moveToDesktop", new JsonObject { ["windowKey"] = Text(command, "windowKey"), ["id"] = Text(command, "id") }, token));
        Context.Commands.Register("example.windows.showThumbnail", (command, _) =>
        {
            // Any key will do, including one that is stale or gone: the host draws a card for it and tells nobody (S26.7).
            ThumbnailKey = Text(command, "windowKey");
            ThumbnailChanged?.Invoke(ThumbnailKey);
            return Task.FromResult(new CommandResult(null));
        });
        return Task.CompletedTask;
    }

    internal string ThumbnailKey { get; private set; } = string.Empty;

    internal event Action<string>? ThumbnailChanged;

    private static string Text(CommandContext command, string name) =>
        command.Args?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0
            ? text
            : throw new ArgumentException($"{name} must be given.");

    private async Task<CommandResult> ForwardAsync(string hostCommand, JsonObject args, CancellationToken ct)
    {
        var result = await Context.Commands.InvokeAsync(hostCommand, args, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            var name = error.Name ?? error.Code.ToString();
            throw new CommandFailedException(name, $"{name}: {error.Detail}", error.Retryable);
        }

        return new CommandResult(result.Value?.Value);
    }

    protected override Task OnStopAsync(CancellationToken ct)
    {
        _view?.Dispose();
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new StatusInstance(request, this);

    private async Task<CommandResult> ActAsync(string hostCommand, CommandContext command, CancellationToken ct)
    {
        if (command.Args?["windowKey"] is not JsonValue value || !value.TryGetValue<string>(out var key) || key.Length == 0)
        {
            throw new ArgumentException("windowKey must name a window.");
        }

        var result = await Context.Commands.InvokeAsync(hostCommand, new JsonObject { ["windowKey"] = key }, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            var name = error.Name ?? error.Code.ToString();
            throw new CommandFailedException(name, $"{name}: {error.Detail}", error.Retryable);
        }

        return new CommandResult(result.Value?.Value);
    }
}

/// <summary>Shows the revision and the window count of the latest view, and marks a stale one.</summary>
public sealed class StatusInstance(InstanceRequest request, WindowsFixturePlugin plugin) : ContributionInstance(request)
{
    private Action<ShellView>? _changed;

    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Stack("root", StackOrientation.Horizontal,
            View.Text("status", Prop<string>.State("status")),
            View.Thumbnail("picture", Prop<string>.State("thumbnail"), ThumbnailSize.Small)));
        Show(plugin.Current);
        Surface.SetState("thumbnail", plugin.ThumbnailKey);
        _changed = Show;
        plugin.Changed += _changed;
        plugin.ThumbnailChanged += ShowThumbnail;
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        if (_changed is not null)
        {
            plugin.Changed -= _changed;
        }

        plugin.ThumbnailChanged -= ShowThumbnail;
        return base.DisposeAsync();
    }

    private void ShowThumbnail(string key) => Surface.SetState("thumbnail", key);

    private void Show(ShellView? view) => Surface.SetState("status", view is null
        ? "Windows: waiting"
        : string.Create(CultureInfo.InvariantCulture, $"Windows r{view.Revision}: {view.Windows.Windows.Count}{(view.Stale ? " stale" : string.Empty)}"));
}

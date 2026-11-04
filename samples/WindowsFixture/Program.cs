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
        return Task.CompletedTask;
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
        Surface.SetView(View.Text("status", Prop<string>.State("status")));
        Show(plugin.Current);
        _changed = Show;
        plugin.Changed += _changed;
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        if (_changed is not null)
        {
            plugin.Changed -= _changed;
        }

        return base.DisposeAsync();
    }

    private void Show(ShellView? view) => Surface.SetState("status", view is null
        ? "Windows: waiting"
        : string.Create(CultureInfo.InvariantCulture, $"Windows r{view.Revision}: {view.Windows.Windows.Count}{(view.Stale ? " stale" : string.Empty)}"));
}

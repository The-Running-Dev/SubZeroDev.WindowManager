using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// Composes the window model, its tracker and the two host feeds. <see cref="StartAsync"/> enumerates once and
/// publishes revision 1 before it starts the tracker, and the caller starts it before any plugin is supervised.
/// </summary>
public sealed class WindowService : IDisposable
{
    private readonly WindowModel _model;
    private readonly IWindowTracker _tracker;
    private readonly Action<string, JsonNode?> _publish;
    private bool _started;

    /// <param name="model">The registry.</param>
    /// <param name="tracker">Finds windows and reports changes.</param>
    /// <param name="publish">Publishes a host feed (<c>FeedBroker.PublishHost</c>).</param>
    public WindowService(WindowModel model, IWindowTracker tracker, Action<string, JsonNode?> publish)
    {
        _model = model;
        _tracker = tracker;
        _publish = publish;
        _model.Changed += OnChanged;
    }

    public IWindowModel Model => _model;

    public async Task StartAsync(CancellationToken ct)
    {
        var handles = await _tracker.EnumerateAsync(ct).ConfigureAwait(false);
        await _model.SeedAsync(handles).ConfigureAwait(false);
        _started = true;
        _tracker.Start(_model.Observe);
    }

    public void Dispose()
    {
        if (_started)
        {
            _tracker.Stop();
            _started = false;
        }

        _model.Changed -= OnChanged;
    }

    private void OnChanged(object? sender, WindowSnapshot snapshot)
    {
        // Both feeds from the one snapshot, back to back, so a subscriber sees the same revision on each (I42).
        _publish(WindowFeeds.WindowsFeedId, WindowFeeds.Windows(snapshot, _model.Epoch));
        _publish(WindowFeeds.DesktopsFeedId, WindowFeeds.Desktops(snapshot));
    }
}

/// <summary>The arguments and results of <c>host.window.activate</c> and <c>host.window.minimize</c>.</summary>
public static class WindowCommands
{
    /// <summary>Reads exactly one of <c>{ windowKey }</c> or <c>{ hwnd }</c>; null with the reason when neither or both are given.</summary>
    public static (string? WindowKey, long? Hwnd)? ParseTarget(JsonObject? args, string command, out string? error)
    {
        error = null;
        var key = args?["windowKey"] is JsonValue k && k.TryGetValue<string>(out var text) ? text : null;
        var hwnd = args?["hwnd"] is JsonValue h && h.GetValueKind() == System.Text.Json.JsonValueKind.Number
            && long.TryParse(h.ToJsonString(), System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : (long?)null;
        if ((key is null) == (hwnd is null))
        {
            error = command + " needs exactly one of { windowKey: string } or { hwnd: number }.";
            return null;
        }

        return (key, hwnd);
    }

    public static async Task<Outcome<CommandResult>> ActivateAsync(IWindowModel model, IForegroundService foreground, JsonObject? args,
        CancellationToken ct)
    {
        if (ParseTarget(args, "host.window.activate", out var error) is not { } target)
        {
            return Outcome<CommandResult>.Fail(CoreErrorCode.CommandFailed, error!);
        }

        if (target.WindowKey is { } windowKey)
        {
            var resolved = model.Resolve(windowKey);
            if (!resolved.Ok)
            {
                return Outcome<CommandResult>.Fail(resolved.Error!);
            }

            var handle = resolved.Value!.Handle;
            var activation = await foreground.ActivateConfirmedAsync(handle, ct).ConfigureAwait(false);
            if (!activation.Ok)
            {
                Reconcile(model, handle, activation.Error!);
                return Outcome<CommandResult>.Fail(activation.Error!);
            }

            return Outcome<CommandResult>.Success(new CommandResult(new JsonObject
            {
                ["outcome"] = activation.Value == ActivationResult.Foreground ? "foreground" : "requested",
            }));
        }

        var legacy = model.ResolveHandle((nint)target.Hwnd!.Value);
        if (!legacy.Ok)
        {
            return Outcome<CommandResult>.Fail(legacy.Error!);
        }

        var outcome = await foreground.ActivateWindowAsync(legacy.Value!.Handle, ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            Reconcile(model, legacy.Value.Handle, outcome.Error!);
            return Outcome<CommandResult>.Fail(outcome.Error!);
        }

        return Outcome<CommandResult>.Success(new CommandResult(null));
    }

    public static async Task<Outcome<CommandResult>> MinimizeAsync(IWindowModel model, IForegroundService foreground, JsonObject? args,
        CancellationToken ct)
    {
        if (ParseTarget(args, "host.window.minimize", out var error) is not { } target)
        {
            return Outcome<CommandResult>.Fail(CoreErrorCode.CommandFailed, error!);
        }

        var resolved = target.WindowKey is { } windowKey ? model.Resolve(windowKey) : model.ResolveHandle((nint)target.Hwnd!.Value);
        if (!resolved.Ok)
        {
            return Outcome<CommandResult>.Fail(resolved.Error!);
        }

        var outcome = await foreground.MinimizeWindowAsync(resolved.Value!.Handle, ct).ConfigureAwait(false);
        if (!outcome.Ok)
        {
            Reconcile(model, resolved.Value.Handle, outcome.Error!);
            return Outcome<CommandResult>.Fail(outcome.Error!);
        }

        return Outcome<CommandResult>.Success(new CommandResult(null));
    }

    /// <summary>A window that closed under the command leaves nothing behind: the model re-checks its entry.</summary>
    private static void Reconcile(IWindowModel model, nint handle, CoreError error)
    {
        if (error.Code == CoreErrorCode.WindowGone)
        {
            model.Observe(new WindowObservation(handle, ObservationKind.RescanMissing));
        }
    }
}

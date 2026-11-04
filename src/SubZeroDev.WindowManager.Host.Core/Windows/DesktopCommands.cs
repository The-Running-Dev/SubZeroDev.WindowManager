using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// The arguments and results of the <c>host.desktops.*</c> commands and the window commands that act on a desktop. The
/// host registers them in replacement mode only; elsewhere they answer NotInReplacementMode.
/// </summary>
public static class DesktopCommands
{
    public const string Switch = "host.desktops.switch";
    public const string Create = "host.desktops.create";
    public const string Remove = "host.desktops.remove";
    public const string Rename = "host.desktops.rename";
    public const string MoveToDesktop = "host.window.moveToDesktop";
    public const string PinToAll = "host.window.pinToAllDesktops";
    public const string Close = "host.window.close";

    /// <summary>The <c>id</c> that names whichever desktop is current when the command runs (Win+Ctrl+F4).</summary>
    public const string CurrentDesktop = "current";

    public static async Task<Outcome<CommandResult>> SwitchAsync(IDesktopSet desktops, JsonObject? args, CancellationToken ct)
    {
        var id = Id(args, "id");
        var direction = Text(args, "direction");
        if ((id is null) == (direction is null) || (direction is not null and not ("previous" or "next")))
        {
            return Bad(Switch + " needs exactly one of { id: string } or { direction: \"previous\" | \"next\" }.");
        }

        var target = id;
        if (direction is not null)
        {
            var list = desktops.Desktops;
            var index = list.ToList().FindIndex(d => d.Id == desktops.CurrentId);
            target = list[(index + (direction == "next" ? 1 : list.Count - 1)) % list.Count].Id;
        }

        return Done(await desktops.SwitchAsync(target!.Value, ct).ConfigureAwait(false));
    }

    public static async Task<Outcome<CommandResult>> CreateAsync(IDesktopSet desktops, JsonObject? args, CancellationToken ct)
    {
        var switchTo = args?["switchTo"] is JsonValue s && s.TryGetValue<bool>(out var flag) && flag;
        var created = desktops.Create(Text(args, "name"));
        if (!created.Ok)
        {
            return Outcome<CommandResult>.Fail(created.Error!);
        }

        if (switchTo)
        {
            var switched = await desktops.SwitchAsync(created.Value, ct).ConfigureAwait(false);
            if (!switched.Ok)
            {
                return Outcome<CommandResult>.Fail(switched.Error!);
            }
        }

        return Outcome<CommandResult>.Success(new CommandResult(new JsonObject { ["id"] = created.Value.ToString("D") }));
    }

    public static async Task<Outcome<CommandResult>> RemoveAsync(IDesktopSet desktops, JsonObject? args, CancellationToken ct)
    {
        var id = Text(args, "id") == CurrentDesktop ? desktops.CurrentId : Id(args, "id");
        return id is { } found
            ? Done(await desktops.RemoveAsync(found, ct).ConfigureAwait(false))
            : Bad(Remove + " needs { id: string | \"current\" }.");
    }

    public static Task<Outcome<CommandResult>> RenameAsync(IDesktopSet desktops, JsonObject? args)
    {
        if (Id(args, "id") is not { } id || Text(args, "name") is not { } name)
        {
            return Task.FromResult(Bad(Rename + " needs { id: string, name: string }."));
        }

        return Task.FromResult(Done(desktops.Rename(id, name)));
    }

    public static async Task<Outcome<CommandResult>> MoveAsync(IDesktopSet desktops, IWindowModel model, JsonObject? args, CancellationToken ct)
    {
        var text = Text(args, "id");
        if (Text(args, "windowKey") is not { } windowKey || text is null || (text != "new" && !Guid.TryParse(text, out _)))
        {
            return Bad(MoveToDesktop + " needs { windowKey: string, id: string | \"new\" }.");
        }

        var resolved = model.Resolve(windowKey);
        if (!resolved.Ok)
        {
            return Outcome<CommandResult>.Fail(resolved.Error!);
        }

        if (resolved.Value!.Managed is not { } window)
        {
            return Outcome<CommandResult>.Fail(CoreErrorCode.WindowNotMovable, "The window is not one the shell tracks.");
        }

        if (text == "new")
        {
            var created = await desktops.MoveToNewAsync(window.Key, ct).ConfigureAwait(false);
            return created.Ok
                ? Outcome<CommandResult>.Success(new CommandResult(new JsonObject { ["id"] = created.Value.ToString("D") }))
                : Outcome<CommandResult>.Fail(created.Error!);
        }

        var id = Guid.Parse(text);
        var moved = await desktops.MoveAsync(window.Key, id, ct).ConfigureAwait(false);
        return moved.Ok
            ? Outcome<CommandResult>.Success(new CommandResult(new JsonObject { ["id"] = id.ToString("D") }))
            : Outcome<CommandResult>.Fail(moved.Error!);
    }

    public static async Task<Outcome<CommandResult>> PinAsync(IDesktopSet desktops, IWindowModel model, JsonObject? args, CancellationToken ct)
    {
        if (Text(args, "windowKey") is not { } windowKey || args?["pinned"] is not JsonValue p || !p.TryGetValue<bool>(out var pinned))
        {
            return Bad(PinToAll + " needs { windowKey: string, pinned: bool }.");
        }

        var resolved = model.Resolve(windowKey);
        if (!resolved.Ok)
        {
            return Outcome<CommandResult>.Fail(resolved.Error!);
        }

        return resolved.Value!.Managed is { } window
            ? Done(await desktops.PinToAllAsync(window.Key, pinned, ct).ConfigureAwait(false))
            : Outcome<CommandResult>.Fail(CoreErrorCode.WindowNotMovable, "The window is not one the shell tracks.");
    }

    /// <summary>Posts a close request to the window. The application decides; the process is never terminated.</summary>
    public static async Task<Outcome<CommandResult>> CloseAsync(IWindowEffects effects, IWindowModel model, JsonObject? args, CancellationToken ct)
    {
        if (Text(args, "windowKey") is not { } windowKey)
        {
            return Bad(Close + " needs { windowKey: string }.");
        }

        var resolved = model.Resolve(windowKey);
        if (!resolved.Ok)
        {
            return Outcome<CommandResult>.Fail(resolved.Error!);
        }

        if (resolved.Value!.Managed is not { } window)
        {
            return Outcome<CommandResult>.Fail(CoreErrorCode.WindowGone, "The window is gone.");
        }

        var closed = await effects.CloseAsync(new PlannedWindow(window.Key, window.Fingerprint), ct).ConfigureAwait(false);
        if (!closed.Ok && closed.Error!.Code == CoreErrorCode.WindowGone)
        {
            model.Observe(new WindowObservation(window.Key.Handle, ObservationKind.RescanMissing));
        }

        return Done(closed);
    }

    private static Outcome<CommandResult> Done(Outcome outcome) =>
        outcome.Ok ? Outcome<CommandResult>.Success(new CommandResult(null)) : Outcome<CommandResult>.Fail(outcome.Error!);

    private static Outcome<CommandResult> Bad(string detail) => Outcome<CommandResult>.Fail(CoreErrorCode.CommandFailed, detail);

    private static string? Text(JsonObject? args, string name) =>
        args?[name] is JsonValue v && v.TryGetValue<string>(out var text) && !string.IsNullOrWhiteSpace(text) ? text : null;

    private static Guid? Id(JsonObject? args, string name) => Text(args, name) is { } text && Guid.TryParse(text, out var id) ? id : null;
}

using System.Runtime.Versioning;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

[assembly: SupportedOSPlatform("windows")]

namespace SubZeroDev.WindowManager.Plugins.Session;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<SessionPlugin>(args);
}

/// <summary>
/// The power menu: a panel button opening the <c>menu</c> popup with lock, sleep, sign out, restart and shut
/// down. Lock and sleep act at once; the other three ask first, since they end the session. Each calls the
/// host's <c>host.session.*</c> command, and a refusal (an application with unsaved work) is shown in the menu.
/// </summary>
public sealed class SessionPlugin : Plugin
{
    public const string PopupId = "menu";

    /// <summary>Each action: its element id, label, host command and whether it asks first.</summary>
    internal static readonly IReadOnlyList<(string Id, string Label, string Command, bool Confirm)> Actions =
    [
        ("lock", "Lock", "host.session.lock", false),
        ("sleep", "Sleep", "host.session.sleep", false),
        ("signOut", "Sign out", "host.session.signOut", true),
        ("restart", "Restart", "host.session.restart", true),
        ("shutdown", "Shut down", "host.session.shutdown", true),
    ];

    private PopupSurface _popup = null!;
    private string? _pending;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        _popup = Context.Popups.Get(PopupId);
        ShowMenu(string.Empty);
        return Task.CompletedTask;
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new PowerInstance(request);

    /// <summary>A closed menu starts afresh, with no question pending.</summary>
    protected override Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct)
    {
        ShowMenu(string.Empty);
        return Task.CompletedTask;
    }

    protected override Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct)
    {
        if (e.Kind != ElementEventKind.Invoked)
        {
            return Task.CompletedTask;
        }

        if (e.ElementId == "cancel")
        {
            ShowMenu(string.Empty);
            return Task.CompletedTask;
        }

        if (e.ElementId == "confirm" && _pending is { } pending)
        {
            return RunAsync(Actions.First(a => a.Id == pending), ct);
        }

        var action = Actions.FirstOrDefault(a => a.Id == e.ElementId);
        if (action.Id is null)
        {
            return Task.CompletedTask;
        }

        if (action.Confirm)
        {
            ShowConfirm(action.Id, action.Label);
            return Task.CompletedTask;
        }

        return RunAsync(action, ct);
    }

    private async Task RunAsync((string Id, string Label, string Command, bool Confirm) action, CancellationToken ct)
    {
        var result = await Context.Commands.InvokeAsync(action.Command, null, ct).ConfigureAwait(false);
        if (result.Error is { } error)
        {
            ShowMenu($"{action.Label} did not happen: {error.Detail}");
            return;
        }

        await _popup.CloseAsync(ct).ConfigureAwait(false);
    }

    private void ShowMenu(string message)
    {
        _pending = null;
        var buttons = Actions.Select(a => View.Button(a.Id, a.Label, View.Event(a.Id), a.Label, new IconRef.Named(a.Id), null, "menu-item"));
        _popup.SetView(View.Stack("power", StackOrientation.Vertical,
            [.. buttons, View.Text("message", Prop<string>.State("message"), "notice")]));
        _popup.SetState("message", message);
    }

    private void ShowConfirm(string id, string label)
    {
        _pending = id;
        _popup.SetView(View.Stack("confirm-" + id, StackOrientation.Vertical,
            View.Text("question", Prop<string>.State("question")),
            View.Stack("answers", StackOrientation.Horizontal,
                View.Button("confirm", label, View.Event("confirm"), label, null, null, "accent"),
                View.Button("cancel", "Cancel", View.Event("cancel"), "Cancel"))));
        _popup.SetState("question", $"{label} now? Programs with unsaved work may ask you to save it.");
    }
}

/// <summary>A panel button that opens the power menu.</summary>
public sealed class PowerInstance(InstanceRequest request) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Button("open", "Power", View.Popup(SessionPlugin.PopupId), null, new IconRef.Named("power"), null, "power"));
        return Task.CompletedTask;
    }
}

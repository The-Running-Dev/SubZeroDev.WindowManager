using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Plugins.QuickSettings;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<QuickSettingsPlugin>(args);
}

/// <summary>One quick setting: its id, whether it is a switch or a 0..1 level, and the words the panel uses.</summary>
internal sealed record Tile(string Id, string Kind, string Label, string Icon);

/// <summary>
/// The panel of quick settings behind Win+A: Wi-Fi, Bluetooth, airplane mode, brightness and the shell's own
/// do-not-disturb. Each tile shows the value read back from its device, never the value that was asked for (invariant
/// I34), so a change that did not stick moves the control back. A device the machine does not have has no tile; a device
/// that refuses a change is shown disabled, with the reason, for a short while and then offered again. The devices come
/// through <see cref="IRadioControl"/> and <see cref="IBrightnessControl"/>; with the test hook set both are the
/// hook's file, so tests never touch the machine's radios or display (I38).
/// </summary>
public sealed class QuickSettingsPlugin : Plugin
{
    /// <summary>The test hook directory; with it set, the devices come from a file.</summary>
    public const string HookVariable = "SUBZERODEV_WM_TEST_HOOK";

    private const string DoNotDisturbFeed = "host.notifications.doNotDisturb";
    private const string DoNotDisturbCommand = "host.notifications.setDoNotDisturb";
    private const string ButtonId = "quicksettings";
    private static readonly TimeSpan RefusedFor = TimeSpan.FromSeconds(10);

    private static readonly Tile[] Tiles =
    [
        new("wifi", "toggle", "Wi-Fi", "wifi"),
        new("bluetooth", "toggle", "Bluetooth", "bluetooth"),
        new("airplane", "toggle", "Airplane mode", "airplane"),
        new("brightness", "level", "Brightness", "brightness"),
        new("doNotDisturb", "toggle", "Do not disturb", "notifications.muted"),
    ];

    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly Dictionary<string, double> _values = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string Reason, DateTimeOffset Until)> _refused = new(StringComparer.Ordinal);

    private IRadioControl _radios = null!;
    private IBrightnessControl _brightness = null!;
    private IDisposable? _dndSubscription;
    private Timer? _expire;
    private bool? _dnd;
    private bool _panelOpen;
    private bool _stopping;
    private bool _nudge;
    private string _stateJson = string.Empty;
    private volatile string? _buttonSurface;
    private int _dirty;
    private int _running;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        if (Environment.GetEnvironmentVariable(HookVariable) is { Length: > 0 } hook)
        {
            var fake = new FakeDevices(hook);
            (_radios, _brightness) = (fake, fake);
        }
        else
        {
            _radios = new RealRadios();
            _brightness = new RealBrightness();
        }

        SetView();
        Context.Commands.Register("quicksettings.open", async (command, token) =>
        {
            var toggle = command.Trigger is CommandTrigger.Chord or CommandTrigger.Keyboard;
            await OpenPanelAsync(null, toggle, token).ConfigureAwait(false);
            return new CommandResult(null);
        });
        Context.Commands.Register("quicksettings.set", async (command, token) =>
        {
            var id = command.Args?["id"] is JsonValue name && name.TryGetValue<string>(out var text) ? text : string.Empty;
            await SetAsync(id, command.Args?["value"], token).ConfigureAwait(false);
            return new CommandResult(null);
        });

        _dndSubscription = Context.Feeds.Subscribe(DoNotDisturbFeed, OnDoNotDisturbAsync);
        _radios.Changed += (_, _) => RequestRefresh();
        _brightness.Changed += (_, _) => RequestRefresh();
        RequestRefresh();
        return Task.CompletedTask;
    }

    protected override async Task OnStopAsync(CancellationToken ct)
    {
        _stopping = true;
        _dndSubscription?.Dispose();
        if (_expire is not null)
        {
            await _expire.DisposeAsync().ConfigureAwait(false);
        }

        _radios?.Dispose();
        if (!ReferenceEquals(_radios, _brightness))
        {
            _brightness?.Dispose();
        }
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new ButtonInstance(request, this);

    protected override Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct)
    {
        if (popup.PopupId == "panel")
        {
            _panelOpen = false;
        }

        return Task.CompletedTask;
    }

    protected override async Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct)
    {
        if (popup.PopupId != "panel")
        {
            return;
        }

        JsonNode? value = e.Kind switch
        {
            ElementEventKind.Toggled when e.Value is JsonValue flag && flag.TryGetValue<bool>(out var on) => on,
            ElementEventKind.ValueChanged when e.Value is JsonValue number && number.TryGetValue<double>(out var level) => level,
            _ => null,
        };
        if (value is null || !Tiles.Any(tile => tile.Id == e.ElementId))
        {
            return;
        }

        try
        {
            await SetAsync(e.ElementId, value, ct).ConfigureAwait(false);
        }
        catch (CommandFailedException)
        {
            // The refusal is on the tile already; the panel needs nothing more.
        }
    }

    internal void SetButton(string? surfaceId) => _buttonSurface = surfaceId;

    internal Task OpenFromButtonAsync(string surfaceId, CancellationToken ct) =>
        OpenPanelAsync(new PopupAnchor(new ElementAnchor(surfaceId, ButtonId), null), toggle: false, ct);

    private void SetView()
    {
        var nodes = new List<ViewNode> { View.Text("title", "Quick settings", "primary") };
        nodes.Add(View.Text("notice", Prop<string>.State("notice"), "compact").WithVisible(Prop<bool>.State("hasNotice")));
        foreach (var tile in Tiles)
        {
            var id = tile.Id;
            var control = tile.Kind == "level"
                ? View.Stack(id + "-level", StackOrientation.Vertical,
                    View.Text(id + "-title", tile.Label),
                    View.Slider(id, tile.Label, Prop<double>.State(id + "Value"), View.Event(id), 0.05))
                : View.Toggle(id, tile.Label, Prop<bool>.State(id + "Value"), View.Event(id), tile.Label, new IconRef.Named(tile.Icon));
            nodes.Add(View.Stack(id + "-tile", StackOrientation.Vertical,
                    control.WithVisible(Prop<bool>.State(id + "Show")),
                    View.Text(id + "-note", Prop<string>.State(id + "Text"), "compact").WithVisible(Prop<bool>.State(id + "Note")),
                    View.Text(id + "-reason", Prop<string>.State(id + "Reason"), "compact").WithVisible(Prop<bool>.State(id + "Note")))
                .WithVisible(Prop<bool>.State(id + "Present")));
        }

        Context.Popups.Get("panel").SetView(View.Stack("panel", StackOrientation.Vertical, [.. nodes]));
    }

    // ---- Refresh ----------------------------------------------------------------------------------------------

    /// <summary>Asks for a read; bursts of changes coalesce into one read after the one running.</summary>
    private void RequestRefresh()
    {
        if (_stopping)
        {
            return;
        }

        Volatile.Write(ref _dirty, 1);
        if (Interlocked.CompareExchange(ref _running, 1, 0) == 0)
        {
            _ = Task.Run(RefreshLoopAsync);
        }
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (!_stopping)
            {
                while (Interlocked.Exchange(ref _dirty, 0) == 1 && !_stopping)
                {
                    await _work.WaitAsync().ConfigureAwait(false);
                    try
                    {
                        await ReadAllAsync(CancellationToken.None).ConfigureAwait(false);
                        PublishAll();
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        Context.Log.LogError("Reading the quick settings failed ({Type}).", e.GetType().Name);
                    }
                    finally
                    {
                        _work.Release();
                    }
                }

                Volatile.Write(ref _running, 0);
                if (Volatile.Read(ref _dirty) == 0 || Interlocked.CompareExchange(ref _running, 1, 0) != 0)
                {
                    return;
                }
            }
        }
        finally
        {
            if (_stopping)
            {
                Volatile.Write(ref _running, 0);
            }
        }
    }

    /// <summary>Reads every device. A device that cannot be read keeps its last value; one that is absent loses its tile.</summary>
    private async Task ReadAllAsync(CancellationToken ct)
    {
        foreach (var id in new[] { "wifi", "bluetooth", "airplane" })
        {
            try
            {
                Take(id, await _radios.ReadAsync(id, ct).ConfigureAwait(false) is { } on ? (on ? 1 : 0) : null);
            }
            catch (Exception e) when (e is DeviceException or InvalidOperationException or IOException)
            {
                Context.Log.LogWarning("Reading {Id} failed ({Type}).", id, e.GetType().Name);
            }
        }

        try
        {
            Take("brightness", await _brightness.ReadAsync(ct).ConfigureAwait(false));
        }
        catch (Exception e) when (e is DeviceException or InvalidOperationException or IOException)
        {
            Context.Log.LogWarning("Reading brightness failed ({Type}).", e.GetType().Name);
        }
    }

    private void Take(string id, double? value)
    {
        if (value is { } present)
        {
            _values[id] = present;
        }
        else
        {
            _values.Remove(id);
        }
    }

    // ---- Publishing -------------------------------------------------------------------------------------------

    private bool Present(string id) => id == "doNotDisturb" ? _dnd is not null : _values.ContainsKey(id);

    private double ValueOf(string id) => id == "doNotDisturb" ? (_dnd == true ? 1 : 0) : _values.GetValueOrDefault(id);

    private (bool Available, string? Reason) Availability(string id)
    {
        if (_refused.TryGetValue(id, out var refusal))
        {
            if (refusal.Until > DateTimeOffset.UtcNow)
            {
                return (false, refusal.Reason);
            }

            _refused.Remove(id);
        }

        return (true, null);
    }

    /// <summary>Publishes the feed and the panel's state; call with the work lock held.</summary>
    private void PublishAll()
    {
        var feed = new JsonArray();
        var panel = Context.Popups.Get("panel");
        _nudge = !_nudge;
        foreach (var tile in Tiles)
        {
            var id = tile.Id;
            var present = Present(id);
            var (available, reason) = Availability(id);
            var value = ValueOf(id);
            if (present)
            {
                var entry = new JsonObject { ["id"] = id, ["kind"] = tile.Kind, ["available"] = available };
                entry["value"] = tile.Kind == "level" ? JsonValue.Create(Math.Round(value, 3)) : JsonValue.Create(value > 0.5);
                if (reason is not null)
                {
                    entry["reason"] = reason;
                }

                feed.Add(entry);
            }

            panel.SetState(id + "Present", present);
            panel.SetState(id + "Show", present && available);
            panel.SetState(id + "Note", present && !available);
            if (tile.Kind == "level")
            {
                // The slider only moves when its value changes, so a value read back that equals the one it already
                // holds is nudged by a millionth, alternating, to make it settle on the real level (I34).
                panel.SetState(id + "Value", Math.Round(value, 3) + (_nudge ? 0.000001 : 0));
            }
            else
            {
                panel.SetState(id + "Value", value > 0.5);
            }

            panel.SetState(id + "Text", tile.Kind == "level"
                ? $"{tile.Label}: {(value * 100).ToString("0", CultureInfo.InvariantCulture)}%"
                : $"{tile.Label}: {(value > 0.5 ? "on" : "off")}");
            panel.SetState(id + "Reason", reason ?? string.Empty);
        }

        panel.SetState("notice", string.Empty);
        panel.SetState("hasNotice", false);

        var json = feed.ToJsonString();
        if (json != _stateJson)
        {
            _stateJson = json;
            Context.Feeds.Publisher("quicksettings.state").Publish(feed);
        }
    }

    // ---- Commands ---------------------------------------------------------------------------------------------

    internal async Task OpenPanelAsync(PopupAnchor? anchor, bool toggle, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (toggle && _panelOpen)
            {
                await Context.Popups.Get("panel").CloseAsync(ct).ConfigureAwait(false);
                return;
            }

            await ReadAllAsync(ct).ConfigureAwait(false);
            PublishAll();
            _panelOpen = true;
            var result = await Context.Popups.Get("panel").OpenAsync(anchor ?? DefaultAnchor(), ct).ConfigureAwait(false);
            if (result.Error is not null)
            {
                _panelOpen = false;
            }
        }
        finally
        {
            _work.Release();
        }
    }

    private PopupAnchor DefaultAnchor() => _buttonSurface is { } surface
        ? new PopupAnchor(new ElementAnchor(surface, ButtonId), null)
        : new PopupAnchor(null, MonitorAnchor.Active);

    /// <summary>
    /// Changes a setting through its device and reads the device back. Failing with <c>DeviceAbsent</c> hides the tile;
    /// failing with <c>DeviceRefused</c> disables it with the reason for a short while. Either way the value shown is the
    /// one the device holds.
    /// </summary>
    internal async Task SetAsync(string id, JsonNode? raw, CancellationToken ct)
    {
        var tile = Tiles.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new CommandFailedException("InvalidArguments", "quicksettings.set needs { id, value } for a known setting.");
        var level = raw is JsonValue number && number.TryGetValue<double>(out var parsed) ? parsed : (double?)null;
        var on = raw is JsonValue flag && flag.TryGetValue<bool>(out var parsedFlag) ? parsedFlag : (bool?)null;
        if (tile.Kind == "level" ? level is null : on is null)
        {
            throw new CommandFailedException("InvalidArguments", tile.Kind == "level" ? "This setting takes a number from 0 to 1." : "This setting takes true or false.");
        }

        if (id == "doNotDisturb")
        {
            // The shell's own switch; the value shown comes back on the host's feed.
            var result = await Context.Commands.InvokeAsync(DoNotDisturbCommand, new JsonObject { ["on"] = on!.Value }, ct).ConfigureAwait(false);
            if (result.Error is not null)
            {
                await RefuseAsync(id, "The shell did not accept the change.", ct).ConfigureAwait(false);
                throw new CommandFailedException(DeviceException.Refused, "The shell did not accept the change.", retryable: true);
            }

            return;
        }

        try
        {
            if (tile.Kind == "level")
            {
                await _brightness.SetAsync(level!.Value, ct).ConfigureAwait(false);
            }
            else
            {
                await _radios.SetAsync(id, on!.Value, ct).ConfigureAwait(false);
            }
        }
        catch (DeviceException e)
        {
            Context.Log.LogWarning("Changing {Id} failed ({Code}).", id, e.Code);
            if (e.Code == DeviceException.Refused)
            {
                await RefuseAsync(id, e.Message, ct).ConfigureAwait(false);
            }
            else
            {
                await ReadBackAsync(ct).ConfigureAwait(false);
            }

            throw new CommandFailedException(e.Code, e.Message, e.Retryable);
        }

        await ReadBackAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Marks a tile disabled with its reason, then reads and publishes the real values.</summary>
    private async Task RefuseAsync(string id, string reason, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _refused[id] = (reason, DateTimeOffset.UtcNow + RefusedFor);
            _expire ??= new Timer(_ => RequestRefresh(), null, Timeout.Infinite, Timeout.Infinite);
            _expire.Change(RefusedFor + TimeSpan.FromMilliseconds(250), Timeout.InfiniteTimeSpan);
            await ReadAllAsync(ct).ConfigureAwait(false);
            PublishAll();
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task ReadBackAsync(CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await ReadAllAsync(ct).ConfigureAwait(false);
            PublishAll();
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task OnDoNotDisturbAsync(FeedUpdate update, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            _dnd = update.Available
                ? update.Snapshot is JsonObject snapshot && snapshot["on"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag
                : null;
            PublishAll();
        }
        finally
        {
            _work.Release();
        }
    }
}

/// <summary>The button in a panel that opens the quick settings.</summary>
public sealed class ButtonInstance(InstanceRequest request, QuickSettingsPlugin plugin) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Button("quicksettings", "Quick settings", View.Event("open"),
                "Quick settings", new IconRef.Named("quicksettings"), null, "indicator")
            .WithTooltip("Quick settings"));
        plugin.SetButton(Surface.SurfaceId);
        return Task.CompletedTask;
    }

    protected override Task OnEventAsync(ElementEvent e, CancellationToken ct) =>
        e.ElementId == "quicksettings" && e.Kind == ElementEventKind.Invoked
            ? plugin.OpenFromButtonAsync(Surface.SurfaceId, ct)
            : Task.CompletedTask;

    public override ValueTask DisposeAsync()
    {
        plugin.SetButton(null);
        return base.DisposeAsync();
    }
}

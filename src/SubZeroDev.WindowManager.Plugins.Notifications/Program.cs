using System.Globalization;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using SubZeroDev.WindowManager.Protocol;
using SubZeroDev.WindowManager.Sdk;

namespace SubZeroDev.WindowManager.Plugins.Notifications;

public static class Program
{
    public static Task<int> Main(string[] args) => PluginRuntime.RunAsync<NotificationsPlugin>(args);
}

/// <summary>What the bell shows.</summary>
/// <param name="Unread">Notifications raised since the centre was last opened.</param>
/// <param name="DoNotDisturb">Whether the shell's do-not-disturb switch is on.</param>
/// <param name="Ready">Whether the history can be read; false sends the bell to the Set up page.</param>
internal sealed record Summary(int Unread, bool DoNotDisturb, bool Ready);

/// <summary>
/// Windows' notifications as the shell's own: pop-ups on a corner of the work area, a centre listing the history, and a
/// bell with the unread count. The history comes through <see cref="INotificationHistory"/> (the real listener needs the
/// package identity and consent Ben gives outside the shell); the pop-ups are one non-activating window whose list this
/// plugin keeps, expiring each entry after <c>popupSeconds</c>. Notification text is held in memory and sent to the host
/// as feed data only, never written to a file or a log (invariant I31).
/// </summary>
public sealed class NotificationsPlugin : Plugin
{
    /// <summary>The test hook directory; with it set, the history comes from a file and actions are recorded.</summary>
    public const string HookVariable = "SUBZERODEV_WM_TEST_HOOK";

    private const string DoNotDisturbFeed = "host.notifications.doNotDisturb";
    private const string DoNotDisturbCommand = "host.notifications.setDoNotDisturb";
    private static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(5);

    private readonly SemaphoreSlim _work = new(1, 1);
    private readonly HashSet<string> _known = new(StringComparer.Ordinal);
    private readonly List<Toast> _toasts = [];

    private INotificationHistory _history = null!;
    private StateStore _store = null!;
    private IDisposable? _dndSubscription;
    private Timer? _retry;
    private HistorySnapshot? _snapshot;
    private string? _readError;
    private DateTimeOffset _lastSeen = DateTimeOffset.MinValue;
    private bool _dnd;
    private bool _restoring;
    private bool _seeded;
    private bool _centreOpen;
    private bool _setupOpen;
    private bool _toastOpen;
    private bool _stopping;
    private string _notice = string.Empty;
    private string _historyJson = string.Empty;
    private string _statusJson = string.Empty;
    private volatile string? _bellSurface;
    private volatile Summary _summary = new(0, false, false);
    private int _popupSeconds = 6;
    private int _maxVisible = 3;
    private bool _popups = true;
    private int _dirty;
    private int _running;

    internal event EventHandler? Changed;

    internal Summary Current => _summary;

    protected override Task OnStartAsync(CancellationToken ct)
    {
        var hook = Environment.GetEnvironmentVariable(HookVariable) is { Length: > 0 } directory ? directory : null;
        _history = hook is null ? new RealHistory() : new FakeHistory(hook);
        _store = new StateStore(Context.DataDirectory);
        var (seen, dnd) = _store.Load();
        _lastSeen = seen ?? DateTimeOffset.MinValue;
        _dnd = dnd;
        _restoring = dnd;

        SetViews();
        Context.Commands.Register("notifications.openCentre", async (command, token) =>
        {
            var toggle = command.Trigger is CommandTrigger.Chord or CommandTrigger.Keyboard;
            await OpenCentreAsync(null, toggle, token).ConfigureAwait(false);
            return new CommandResult(null);
        });
        Context.Commands.Register("notifications.dismiss", async (command, token) =>
        {
            var id = Arg(command, "id") ?? throw new CommandFailedException("InvalidArguments", "notifications.dismiss needs { id }.");
            await ActAsync(() => _history.DismissAsync(id, token)).ConfigureAwait(false);
            return new CommandResult(null);
        });
        Context.Commands.Register("notifications.clear", async (command, token) =>
        {
            var appId = Arg(command, "appId");
            await ActAsync(() => _history.ClearAsync(appId, token)).ConfigureAwait(false);
            return new CommandResult(null);
        });
        Context.Commands.Register("notifications.activate", async (command, token) =>
        {
            var id = Arg(command, "id") ?? throw new CommandFailedException("InvalidArguments", "notifications.activate needs { id }.");
            await ActivateAsync(id, token).ConfigureAwait(false);
            return new CommandResult(null);
        });

        _dndSubscription = Context.Feeds.Subscribe(DoNotDisturbFeed, OnDoNotDisturbAsync);
        _history.Changed += (_, _) => RequestRefresh();
        RequestRefresh();
        if (_restoring)
        {
            _ = Task.Run(RestoreDoNotDisturbAsync);
        }

        return Task.CompletedTask;
    }

    protected override async Task OnStopAsync(CancellationToken ct)
    {
        _stopping = true;
        _dndSubscription?.Dispose();
        if (_retry is not null)
        {
            await _retry.DisposeAsync().ConfigureAwait(false);
        }

        await _work.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            ClearToasts();
        }
        finally
        {
            _work.Release();
        }

        _history?.Dispose();
    }

    protected override ContributionInstance CreateInstance(InstanceRequest request) => new BellInstance(request, this);

    protected override async Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (popup.PopupId)
            {
                case "toast":
                    _toastOpen = false;
                    ClearToasts();
                    break;
                case "centre":
                    _centreOpen = false;
                    _lastSeen = DateTimeOffset.UtcNow;
                    Save();
                    PublishAll();
                    break;
                case "setup":
                    _setupOpen = false;
                    break;
            }
        }
        finally
        {
            _work.Release();
        }
    }

    protected override async Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct)
    {
        switch (popup.PopupId, e.ElementId)
        {
            case ("centre", "dnd") when e.Kind == ElementEventKind.Toggled && e.Value is JsonValue value && value.TryGetValue<bool>(out var on):
                await SetDoNotDisturbAsync(on, ct).ConfigureAwait(false);
                break;
            case ("setup", "recheck") when e.Kind == ElementEventKind.Invoked:
                RequestRefresh();
                break;
        }
    }

    /// <summary>Takes the bell's settings as the plugin's: pop-ups work without a bell placed, on the defaults.</summary>
    internal void ApplyBellSettings(JsonObject settings)
    {
        _popupSeconds = Math.Clamp(Int(settings, "popupSeconds", 6), 2, 30);
        _maxVisible = Math.Clamp(Int(settings, "maxVisible", 3), 1, 5);
        _popups = settings["popups"] is not JsonValue flag || !flag.TryGetValue<bool>(out var on) || on;
    }

    internal void SetBell(string? surfaceId) => _bellSurface = surfaceId;

    internal Task OpenFromBellAsync(string surfaceId, CancellationToken ct) =>
        OpenCentreAsync(new PopupAnchor(new ElementAnchor(surfaceId, "bell"), null), toggle: false, ct);

    private void SetViews()
    {
        var itemArgs = new Func<JsonObject>(() => new JsonObject { ["id"] = new JsonObject { ["$item"] = "id" } });
        Context.Popups.Get("toast").SetView(View.List("toasts", "New notifications", Prop<JsonArray>.State("items"),
            View.Stack("toast", StackOrientation.Vertical,
                View.Text("source", Prop<string>.Item("source"), "compact"),
                View.Button("open", "Open the application", View.Invoke("notifications.activate", itemArgs()), Prop<string>.Item("title")),
                View.Text("body", Prop<string>.Item("body"), "compact")),
            StackOrientation.Vertical));

        Context.Popups.Get("centre").SetView(View.Stack("centre", StackOrientation.Vertical,
            View.Stack("header", StackOrientation.Horizontal,
                View.Text("title", "Notifications", "primary"),
                View.Spacer("fill", flex: true),
                View.Toggle("dnd", "Do not disturb", Prop<bool>.State("doNotDisturb"), View.Event("dnd"), "Do not disturb"),
                View.Button("clear", "Clear all notifications", View.Invoke("notifications.clear"), "Clear all")),
            View.Text("notice", Prop<string>.State("notice"), "compact").WithVisible(Prop<bool>.State("hasNotice")),
            View.Text("empty", "No new notifications", "compact").WithVisible(Prop<bool>.State("isEmpty")),
            View.List("items", "Notifications", Prop<JsonArray>.State("items"),
                View.Stack("item", StackOrientation.Vertical,
                    View.Text("source", Prop<string>.Item("source"), "compact"),
                    View.Button("open", "Open the application", View.Invoke("notifications.activate", itemArgs()), Prop<string>.Item("title")),
                    View.Text("body", Prop<string>.Item("body"), "compact"),
                    View.Button("dismiss", "Dismiss", View.Invoke("notifications.dismiss", itemArgs()), "Dismiss")),
                StackOrientation.Vertical)));

        Context.Popups.Get("setup").SetView(View.Stack("setup", StackOrientation.Vertical,
            View.Text("title", "Set up notifications", "primary"),
            View.Text("step", Prop<string>.State("step")),
            View.Text("help", Prop<string>.State("help"), "compact"),
            View.Button("recheck", "Check again", View.Event("recheck"), "Check again")));
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
                        await RefreshLockedAsync(CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (Exception e) when (e is not OutOfMemoryException)
                    {
                        Context.Log.LogError(e, "Refreshing the notifications failed.");
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

    private async Task RefreshLockedAsync(CancellationToken ct)
    {
        HistorySnapshot snapshot;
        try
        {
            snapshot = await _history.ReadAsync(ct).ConfigureAwait(false);
        }
        catch (HistoryException e)
        {
            // Keep the last published history and say only the code (I31).
            _readError = e.Code;
            Context.Log.LogWarning("Reading the notification history failed ({Code}).", e.Code);
            _retry ??= new Timer(_ => RequestRefresh(), null, Timeout.Infinite, Timeout.Infinite);
            _retry.Change(RetryAfter, Timeout.InfiniteTimeSpan);
            PublishAll();
            return;
        }

        _readError = null;
        _snapshot = snapshot;
        DetectNew(snapshot);
        if (_centreOpen)
        {
            _lastSeen = DateTimeOffset.UtcNow;
        }

        PublishAll();
        await SyncToastsAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Finds notifications that arrived since the last read. The first successful read only seeds the known set, so the
    /// history that was already there does not pop up; a notification that left the history leaves the pop-ups too.
    /// </summary>
    private void DetectNew(HistorySnapshot snapshot)
    {
        if (!snapshot.CanRead)
        {
            _known.Clear();
            _seeded = false;
            ClearToasts();
            return;
        }

        var ids = new HashSet<string>(snapshot.Items.Select(item => item.Id), StringComparer.Ordinal);
        if (!_seeded)
        {
            _known.UnionWith(ids);
            _seeded = true;
            return;
        }

        var fresh = snapshot.Items.Where(item => !_known.Contains(item.Id)).OrderBy(item => item.At).ToList();
        _known.IntersectWith(ids);
        _known.UnionWith(ids);
        for (var index = _toasts.Count - 1; index >= 0; index--)
        {
            if (!ids.Contains(_toasts[index].Item.Id))
            {
                _toasts[index].Dispose();
                _toasts.RemoveAt(index);
            }
        }

        if (fresh.Count == 0 || !_popups || _dnd || _centreOpen || !snapshot.SwitchedOn)
        {
            return;
        }

        foreach (var item in fresh)
        {
            var id = item.Id;
            _toasts.Insert(0, new Toast(item, new Timer(_ => _ = ExpireAsync(id), null, TimeSpan.FromSeconds(_popupSeconds), Timeout.InfiniteTimeSpan)));
        }

        while (_toasts.Count > _maxVisible)
        {
            _toasts[^1].Dispose();
            _toasts.RemoveAt(_toasts.Count - 1);
        }
    }

    private async Task ExpireAsync(string id)
    {
        if (_stopping)
        {
            return;
        }

        await _work.WaitAsync().ConfigureAwait(false);
        try
        {
            var index = _toasts.FindIndex(toast => toast.Item.Id == id);
            if (index >= 0)
            {
                _toasts[index].Dispose();
                _toasts.RemoveAt(index);
                await SyncToastsAsync(CancellationToken.None).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Context.Log.LogError(e, "Expiring a pop-up failed.");
        }
        finally
        {
            _work.Release();
        }
    }

    private void ClearToasts()
    {
        foreach (var toast in _toasts)
        {
            toast.Dispose();
        }

        _toasts.Clear();
    }

    /// <summary>Pushes the pop-up list to the toast window and opens or closes the window as the list fills and empties.</summary>
    private async Task SyncToastsAsync(CancellationToken ct)
    {
        var popup = Context.Popups.Get("toast");
        var items = new JsonArray();
        foreach (var toast in _toasts)
        {
            items.Add(Row(toast.Item));
        }

        popup.SetState("items", items);
        if (_toasts.Count > 0 && !_toastOpen)
        {
            _toastOpen = true;
            var result = await popup.OpenAsync(new PopupAnchor(null, MonitorAnchor.Primary), ct).ConfigureAwait(false);
            if (result.Error is not null)
            {
                _toastOpen = false;
            }
        }
        else if (_toasts.Count == 0 && _toastOpen)
        {
            _toastOpen = false;
            await popup.CloseAsync(ct).ConfigureAwait(false);
        }
    }

    // ---- Publishing -------------------------------------------------------------------------------------------

    private string? Problem() => SetupError() ?? (_snapshot is null ? _readError : null);

    private string? SetupError()
    {
        if (_snapshot is not { } snapshot)
        {
            return null;
        }

        if (snapshot.Identity != "ok")
        {
            return ErrorNames.IdentityMissing;
        }

        if (snapshot.Access != "allowed")
        {
            return ErrorNames.AccessDenied;
        }

        return snapshot.SwitchedOn ? null : ErrorNames.NotificationsOff;
    }

    private void PublishAll()
    {
        var items = _snapshot?.Items ?? [];
        var history = new JsonArray();
        var rows = new JsonArray();
        var unread = 0;
        foreach (var item in items)
        {
            var read = item.At <= _lastSeen;
            unread += read ? 0 : 1;
            history.Add(new JsonObject
            {
                ["id"] = item.Id,
                ["appId"] = item.AppId,
                ["appName"] = item.AppName,
                ["title"] = item.Title,
                ["body"] = item.Body,
                ["at"] = item.At.ToString("O", CultureInfo.InvariantCulture),
                ["read"] = read,
                ["silent"] = item.Silent,
            });
            rows.Add(Row(item));
        }

        var problem = Problem();
        _summary = new Summary(unread, _dnd, problem is null);

        var historyJson = history.ToJsonString();
        if (historyJson != _historyJson)
        {
            _historyJson = historyJson;
            Context.Feeds.Publisher("notifications.history").Publish(history);
        }

        if (_snapshot is { } snapshot)
        {
            var status = new JsonObject
            {
                ["identity"] = snapshot.Identity,
                ["access"] = snapshot.Access,
                ["switchedOn"] = snapshot.SwitchedOn,
                ["unread"] = unread,
                ["doNotDisturb"] = _dnd,
            };
            var reported = problem ?? _readError;
            if (reported is not null)
            {
                status["error"] = reported;
            }

            var statusJson = status.ToJsonString();
            if (statusJson != _statusJson)
            {
                _statusJson = statusJson;
                Context.Feeds.Publisher("notifications.status").Publish(status);
            }
        }

        var centre = Context.Popups.Get("centre");
        centre.SetState("items", rows);
        centre.SetState("isEmpty", rows.Count == 0);
        centre.SetState("doNotDisturb", _dnd);
        centre.SetState("notice", _notice);
        centre.SetState("hasNotice", _notice.Length > 0);

        var (step, help) = Words(problem);
        var setup = Context.Popups.Get("setup");
        setup.SetState("step", step);
        setup.SetState("help", help);

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private static (string Step, string Help) Words(string? problem) => problem switch
    {
        ErrorNames.IdentityMissing => ("The notification helper is not registered.",
            "Register the plugin's package with its register script and trust its certificate (see docs/replacement-mode.md), then check again."),
        ErrorNames.AccessDenied => ("Notification access is not allowed.",
            "Open Settings > Privacy & security > Notifications and allow SubZeroDev Notifications to read notifications, then check again."),
        ErrorNames.NotificationsOff => ("Notifications are switched off for your account.",
            "Turn on Settings > System > Notifications, then check again."),
        ErrorNames.HistoryReadFailed => ("Windows did not answer.", "The history is read again automatically; you can also check now."),
        _ => ("Notifications are ready.", string.Empty),
    };

    private static JsonObject Row(NotificationItem item) => new()
    {
        ["key"] = item.Id,
        ["id"] = item.Id,
        ["source"] = $"{item.AppName}  {item.At.ToLocalTime().ToString("t", CultureInfo.CurrentCulture)}",
        ["appName"] = item.AppName,
        ["title"] = item.Title,
        ["body"] = item.Body,
    };

    // ---- Commands ---------------------------------------------------------------------------------------------

    internal async Task OpenCentreAsync(PopupAnchor? anchor, bool toggle, CancellationToken ct)
    {
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_snapshot is null && _readError is null)
            {
                await RefreshLockedAsync(ct).ConfigureAwait(false);
            }

            var centre = Problem() is null;
            var id = centre ? "centre" : "setup";
            if (toggle && (centre ? _centreOpen : _setupOpen))
            {
                await Context.Popups.Get(id).CloseAsync(ct).ConfigureAwait(false);
                return;
            }

            if (centre)
            {
                _centreOpen = true;
                _lastSeen = DateTimeOffset.UtcNow;
                _notice = string.Empty;
                Save();
                ClearToasts();
            }
            else
            {
                _setupOpen = true;
            }

            PublishAll();
            await SyncToastsAsync(ct).ConfigureAwait(false);
            var result = await Context.Popups.Get(id).OpenAsync(anchor ?? DefaultAnchor(), ct).ConfigureAwait(false);
            if (result.Error is not null)
            {
                _centreOpen = false;
                _setupOpen = false;
            }
        }
        finally
        {
            _work.Release();
        }
    }

    private PopupAnchor DefaultAnchor() => _bellSurface is { } surface
        ? new PopupAnchor(new ElementAnchor(surface, "bell"), null)
        : new PopupAnchor(null, MonitorAnchor.Active);

    /// <summary>Runs a change to the history, failing with the contract's code when the history cannot be used.</summary>
    private async Task ActAsync(Func<Task> action)
    {
        if (Problem() is { } problem && _snapshot is not null)
        {
            throw new CommandFailedException(problem, "Notifications are not set up.");
        }

        try
        {
            await action().ConfigureAwait(false);
        }
        catch (HistoryException e)
        {
            throw new CommandFailedException(e.Code, e.Message, e.Retryable);
        }

        RequestRefresh();
    }

    private async Task ActivateAsync(string id, CancellationToken ct)
    {
        if (Problem() is { } problem && _snapshot is not null)
        {
            throw new CommandFailedException(problem, "Notifications are not set up.");
        }

        try
        {
            await _history.ActivateAsync(id, ct).ConfigureAwait(false);
        }
        catch (HistoryException e)
        {
            await _work.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                _notice = "That application could not be started.";
                PublishAll();
            }
            finally
            {
                _work.Release();
            }

            throw new CommandFailedException(e.Code, e.Message, e.Retryable);
        }

        if (_centreOpen)
        {
            await Context.Popups.Get("centre").CloseAsync(ct).ConfigureAwait(false);
        }
    }

    // ---- Do not disturb ---------------------------------------------------------------------------------------

    private async Task SetDoNotDisturbAsync(bool on, CancellationToken ct)
    {
        var result = await Context.Commands.InvokeAsync(DoNotDisturbCommand, new JsonObject { ["on"] = on }, ct).ConfigureAwait(false);
        if (result.Error is not null)
        {
            Context.Log.LogWarning("Setting do-not-disturb failed.");
        }
    }

    /// <summary>The host's switch is in memory only; a persisted "on" is handed back to it after a restart.</summary>
    private async Task RestoreDoNotDisturbAsync()
    {
        var result = await Context.Commands.InvokeAsync(DoNotDisturbCommand, new JsonObject { ["on"] = true }, CancellationToken.None).ConfigureAwait(false);
        await _work.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_restoring)
            {
                _restoring = false;
                if (result.Error is not null)
                {
                    _dnd = false;
                    PublishAll();
                }
            }
        }
        finally
        {
            _work.Release();
        }
    }

    private async Task OnDoNotDisturbAsync(FeedUpdate update, CancellationToken ct)
    {
        if (!update.Available)
        {
            return;
        }

        var on = update.Snapshot is JsonObject snapshot && snapshot["on"] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        await _work.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_restoring)
            {
                // The host starts with the switch off; the restore is on its way.
                if (!on)
                {
                    return;
                }

                _restoring = false;
            }

            if (on == _dnd)
            {
                return;
            }

            _dnd = on;
            Save();
            if (on)
            {
                ClearToasts();
            }

            PublishAll();
            await SyncToastsAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            _work.Release();
        }
    }

    // ---- Helpers ----------------------------------------------------------------------------------------------

    private void Save()
    {
        try
        {
            _store.Save(_lastSeen, _dnd);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            Context.Log.LogWarning("Saving the notification state failed ({Type}).", e.GetType().Name);
        }
    }

    private static string? Arg(CommandContext command, string name) =>
        command.Args?[name] is JsonValue value && value.TryGetValue<string>(out var text) && text.Length > 0 ? text : null;

    private static int Int(JsonObject settings, string name, int fallback) =>
        settings[name] is JsonValue value && value.TryGetValue<int>(out var number) ? number : fallback;

    private sealed class Toast(NotificationItem item, Timer timer) : IDisposable
    {
        public NotificationItem Item { get; } = item;

        public void Dispose() => timer.Dispose();
    }
}

/// <summary>The bell in a panel: a button with the unread count that opens the centre, or the Set up page.</summary>
public sealed class BellInstance(InstanceRequest request, NotificationsPlugin plugin) : ContributionInstance(request)
{
    protected override Task OnCreatedAsync(CancellationToken ct)
    {
        Surface.SetView(View.Button("bell", "Notifications", View.Event("open"),
                Prop<string>.State("label"), Prop<IconRef>.State("icon"), null, "indicator")
            .WithTooltip(Prop<string>.State("tooltip")));
        plugin.ApplyBellSettings(Settings);
        plugin.SetBell(Surface.SurfaceId);
        plugin.Changed += OnChanged;
        Show();
        return Task.CompletedTask;
    }

    protected override Task OnEventAsync(ElementEvent e, CancellationToken ct) =>
        e.ElementId == "bell" && e.Kind == ElementEventKind.Invoked
            ? plugin.OpenFromBellAsync(Surface.SurfaceId, ct)
            : Task.CompletedTask;

    protected override Task OnSettingsChangedAsync(JsonObject settings, CancellationToken ct)
    {
        plugin.ApplyBellSettings(settings);
        return Task.CompletedTask;
    }

    public override ValueTask DisposeAsync()
    {
        plugin.Changed -= OnChanged;
        plugin.SetBell(null);
        return base.DisposeAsync();
    }

    private void OnChanged(object? sender, EventArgs e) => Show();

    private void Show()
    {
        var summary = plugin.Current;
        var label = summary.Unread > 0 ? $"Notifications ({summary.Unread})" : "Notifications";
        var icon = summary.Unread > 0 ? "notifications.unread" : summary.DoNotDisturb ? "notifications.muted" : "notifications";
        var tooltip = !summary.Ready ? "Notifications need setting up"
            : summary.Unread > 0 ? $"{summary.Unread} unread notification{(summary.Unread == 1 ? string.Empty : "s")}"
            : "No new notifications";
        if (summary.Ready && summary.DoNotDisturb)
        {
            tooltip += ", do not disturb is on";
        }

        Surface.SetState("label", label);
        Surface.SetState("icon", new JsonObject { ["named"] = icon });
        Surface.SetState("tooltip", tooltip);
    }
}

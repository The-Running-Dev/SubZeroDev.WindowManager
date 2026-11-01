using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.Core;

public enum PlaceholderReason { Starting, Disabled, Incompatible, Invalid, Faulted, Unavailable, NotSupportedInMode, Missing }

/// <summary>One placed contribution instance and where it is shown.</summary>
public sealed record InstancePlacement(ContributionInstance Instance, SurfaceContext Context);

/// <summary>
/// What a surface should show for one instance: a placeholder when <see cref="Reason"/> is set,
/// otherwise <see cref="View"/> bound to <see cref="State"/>. Both are snapshots and never change.
/// </summary>
public sealed record InstanceSnapshot(Guid InstanceId, string PluginId, PlaceholderReason? Reason, string? Detail,
    JsonNode? View, long ViewRevision, IReadOnlyDictionary<string, JsonNode?> State);

/// <summary>What a popup should show: <see cref="View"/> bound to <see cref="State"/>, or nothing yet. Never changes.</summary>
public sealed record PopupSnapshot(string PluginId, string PopupId, JsonNode? View, long ViewRevision,
    IReadOnlyDictionary<string, JsonNode?> State);

public interface IContributionHost
{
    /// <summary>Replaces the set of placed instances: new ones are created in their workers, removed ones destroyed.</summary>
    void SetPlacements(IReadOnlyList<InstancePlacement> placements);

    InstanceSnapshot? Describe(Guid instanceId);

    IReadOnlyList<InstanceSnapshot> All { get; }

    /// <summary>Raised off the UI thread, one at a time, whenever an instance's snapshot changes.</summary>
    event EventHandler<InstanceSnapshot> InstanceChanged;

    /// <summary>
    /// Sends <c>element.event</c> to the instance's worker, after <c>instance.create</c> and in queue
    /// order. User input grants the worker foreground rights first (I18). An instance without a running
    /// worker gets <see cref="CoreErrorCode.PluginUnavailable"/>; the event is not sent later (I19).
    /// </summary>
    Task<Outcome> SendEventAsync(Guid instanceId, string elementId, ElementEventKind kind, JsonNode? value, InvocationSource source,
        string? itemKey = null);

    /// <summary>The latest view of a popup surface (<c>popup:&lt;id&gt;</c>); null before its worker set one.</summary>
    PopupSnapshot? DescribePopup(string pluginId, string popupId);

    /// <summary>Whether the plugin's manifest declares the popup under <c>popups</c>.</summary>
    bool DeclaresPopup(string pluginId, string popupId);

    /// <summary>Raised off the UI thread, one at a time, whenever a popup's snapshot changes.</summary>
    event EventHandler<PopupSnapshot> PopupChanged;

    /// <summary>Raised off the UI thread with the plugin id when a worker stops or faults; its popups close.</summary>
    event EventHandler<string> PopupOwnerStopped;

    /// <summary>Sends <c>element.event</c> for an element in a popup, like <see cref="SendEventAsync"/>.</summary>
    Task<Outcome> SendPopupEventAsync(string pluginId, string popupId, string elementId, ElementEventKind kind, JsonNode? value,
        InvocationSource source, string? itemKey = null);

    /// <summary>Grants the worker foreground rights (I18), then sends <c>popup.opened</c>.</summary>
    Task<Outcome> NotifyPopupOpenedAsync(string pluginId, string popupId, PopupTrigger trigger);

    Task<Outcome> NotifyPopupClosedAsync(string pluginId, string popupId, PopupCloseReason reason);
}

/// <summary>
/// Connects placed instances to their plugin workers. Every change — a new layout, a worker
/// message, a status or catalog change — runs on one queue, so updates for one surface are applied
/// in channel order (I12) and never concurrently. A worker may only update surfaces of its own
/// instances; views are checked before they replace the shown one (I11). State patches mark the
/// instance changed and one publish follows the queued work, so a burst renders latest-wins.
/// When a worker faults its instances show Faulted, then Unavailable once its restart budget is
/// spent; each restart replays <c>instance.create</c>.
/// </summary>
public sealed class ContributionHost : IContributionHost, IAsyncDisposable
{
    public static readonly TimeSpan CreateTimeout = TimeSpan.FromSeconds(5);

    private readonly IPluginSupervisor _supervisor;
    private readonly IPackageCatalog _catalog;
    private readonly IPluginLogs _pluginLogs;
    private readonly IHostLog _log;
    private readonly IForegroundService? _foreground;
    private readonly Channel<Func<Task>> _work = Channel.CreateUnbounded<Func<Task>>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Dictionary<Guid, Entry> _instances = [];
    private readonly HashSet<IWorkerChannel> _attached = [];
    private readonly Dictionary<string, IWorkerChannel> _latest = new(StringComparer.Ordinal);
    private readonly Lock _snapshotGate = new();
    private readonly Dictionary<Guid, InstanceSnapshot> _snapshots = [];
    private readonly Dictionary<(string Plugin, string Popup), PopupEntry> _popups = [];
    private readonly Dictionary<(string Plugin, string Popup), PopupSnapshot> _popupSnapshots = [];
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _loop;

    public ContributionHost(IPluginSupervisor supervisor, IPackageCatalog catalog, IPluginLogs pluginLogs, IHostLog log,
        IForegroundService? foreground = null)
    {
        _foreground = foreground;
        _supervisor = supervisor;
        _catalog = catalog;
        _pluginLogs = pluginLogs;
        _log = log;
        _supervisor.StatusChanged += (_, status) => Enqueue(() => OnStatusChanged(status));

        // Popup views a worker sets while it starts arrive before it is Running.
        _supervisor.ChannelOpened += (_, channel) => Attach(channel);
        _catalog.Changed += (_, change) => Enqueue(() => OnCatalogChanged(change));
        _loop = Task.Run(RunAsync);
    }

    public event EventHandler<InstanceSnapshot>? InstanceChanged;

    public event EventHandler<PopupSnapshot>? PopupChanged;

    public event EventHandler<string>? PopupOwnerStopped;

    public bool DeclaresPopup(string pluginId, string popupId) =>
        _catalog.GetManifest(pluginId) is { } manifest && manifest.Popups.Contains(popupId);

    public PopupSnapshot? DescribePopup(string pluginId, string popupId)
    {
        lock (_snapshotGate)
        {
            return _popupSnapshots.GetValueOrDefault((pluginId, popupId));
        }
    }

    public Task<Outcome> SendPopupEventAsync(string pluginId, string popupId, string elementId, ElementEventKind kind, JsonNode? value,
        InvocationSource source, string? itemKey = null) =>
        SendToWorkerAsync(pluginId, source is InvocationSource.Pointer or InvocationSource.Keyboard or InvocationSource.Hotkey,
            Methods.ElementEvent, new ElementEventParams(PopupSurfaceId(popupId), elementId, kind, itemKey, value?.DeepClone(), source));

    public Task<Outcome> NotifyPopupOpenedAsync(string pluginId, string popupId, PopupTrigger trigger) =>
        SendToWorkerAsync(pluginId, true, Methods.PopupOpened, new PopupOpenedParams(popupId, trigger));

    public Task<Outcome> NotifyPopupClosedAsync(string pluginId, string popupId, PopupCloseReason reason) =>
        SendToWorkerAsync(pluginId, false, Methods.PopupClosed, new PopupClosedParams(popupId, reason));

    public IReadOnlyList<InstanceSnapshot> All
    {
        get
        {
            lock (_snapshotGate)
            {
                return [.. _snapshots.Values];
            }
        }
    }

    public InstanceSnapshot? Describe(Guid instanceId)
    {
        lock (_snapshotGate)
        {
            return _snapshots.GetValueOrDefault(instanceId);
        }
    }

    public void SetPlacements(IReadOnlyList<InstancePlacement> placements) => Enqueue(() => Reconcile(placements));

    public Task<Outcome> SendEventAsync(Guid instanceId, string elementId, ElementEventKind kind, JsonNode? value, InvocationSource source,
        string? itemKey = null)
    {
        var done = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            if (!_instances.TryGetValue(instanceId, out var entry)
                || entry is not { Created: true, Channel: { } channel }
                || _supervisor.GetChannel(entry.PluginId) != channel)
            {
                var plugin = entry?.PluginId ?? "the plugin";
                done.TrySetResult(Outcome.Fail(CoreErrorCode.PluginUnavailable, $"{plugin} is not running.", retryable: true));
                return Task.CompletedTask;
            }

            if (source is InvocationSource.Pointer or InvocationSource.Keyboard or InvocationSource.Hotkey)
            {
                _foreground?.AllowForeground(channel.ProcessId);
            }

            done.TrySetResult(channel.Notify(Methods.ElementEvent,
                new ElementEventParams(SurfaceIdOf(instanceId), elementId, kind, itemKey, value?.DeepClone(), source)));
            return Task.CompletedTask;
        });
        return done.Task;
    }

    /// <summary>Completes once every change queued so far has been applied. For tests and shutdown.</summary>
    public Task FlushAsync()
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            done.TrySetResult();
            return Task.CompletedTask;
        });
        return done.Task;
    }

    public async ValueTask DisposeAsync()
    {
        _work.Writer.TryComplete();
        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await _loop.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private static string SurfaceIdOf(Guid instanceId) => instanceId.ToString("D");

    private static string PopupSurfaceId(string popupId) => "popup:" + popupId;

    /// <summary>Sends a notification to a running worker in queue order, granting foreground first when asked.</summary>
    private Task<Outcome> SendToWorkerAsync<TParams>(string pluginId, bool grantForeground, string method, TParams parameters)
    {
        var done = new TaskCompletionSource<Outcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        Enqueue(() =>
        {
            if (_supervisor.GetChannel(pluginId) is not { } channel)
            {
                done.TrySetResult(Outcome.Fail(CoreErrorCode.PluginUnavailable, $"{pluginId} is not running.", retryable: true));
                return Task.CompletedTask;
            }

            if (grantForeground)
            {
                _foreground?.AllowForeground(channel.ProcessId);
            }

            done.TrySetResult(channel.Notify(method, parameters));
            return Task.CompletedTask;
        });
        return done.Task;
    }

    private void Enqueue(Func<Task> step) => _work.Writer.TryWrite(step);

    private async Task RunAsync()
    {
        await foreach (var step in _work.Reader.ReadAllAsync(_stop.Token).ConfigureAwait(false))
        {
            try
            {
                await step().ConfigureAwait(false);
            }
            catch (Exception e) when (e is not OperationCanceledException)
            {
                _log.Write(LogLevel.Error, "contributions", $"Unexpected failure: {e}");
            }
        }
    }

    private Task Reconcile(IReadOnlyList<InstancePlacement> placements)
    {
        var wanted = placements.GroupBy(placement => placement.Instance.Id).ToDictionary(group => group.Key, group => group.First());
        foreach (var (id, entry) in _instances.ToList())
        {
            if (!wanted.TryGetValue(id, out var placement) || !SameInstance(placement.Instance, entry.Placement.Instance))
            {
                Destroy(entry);
                _instances.Remove(id);
                Forget(id);
            }
        }

        foreach (var (id, placement) in wanted)
        {
            if (_instances.ContainsKey(id))
            {
                continue;
            }

            var entry = new Entry(placement);
            _instances[id] = entry;
            Publish(entry, PlaceholderReason.Starting, null);
        }

        foreach (var pluginId in _instances.Values.Select(entry => entry.PluginId).Distinct(StringComparer.Ordinal).ToList())
        {
            Start(pluginId);
        }

        StopUnused();
        return Task.CompletedTask;
    }

    private static bool SameInstance(ContributionInstance a, ContributionInstance b) =>
        a.Plugin == b.Plugin && a.Contribution == b.Contribution && a.SettingsVersion == b.SettingsVersion
        && JsonNode.DeepEquals(a.Settings, b.Settings);

    /// <summary>Starts the worker if needed and creates its instances that are not created yet.</summary>
    private void Start(string pluginId)
    {
        var entries = Waiting(pluginId);
        if (entries.Count == 0)
        {
            return;
        }

        var manifest = _catalog.GetManifest(pluginId);
        if (manifest is null)
        {
            var record = _catalog.All(PackageKind.Plugin).FirstOrDefault(package => package.Id == pluginId);
            foreach (var entry in entries)
            {
                Publish(entry, record is null ? PlaceholderReason.Missing : PlaceholderReason.Invalid,
                    record is null ? $"No plugin '{pluginId}' is installed." : string.Join(" ", record.Problems.Select(p => p.Detail)));
            }

            return;
        }

        foreach (var entry in entries.Where(entry => manifest.Contributions.All(c => c.Id != entry.Placement.Instance.Contribution)))
        {
            Publish(entry, PlaceholderReason.Missing, $"{pluginId} has no contribution '{entry.Placement.Instance.Contribution}'.");
        }

        if (entries.All(entry => entry.Reason == PlaceholderReason.Missing))
        {
            return;
        }

        _ = Task.Run(async () =>
        {
            Outcome outcome;
            try
            {
                outcome = await _supervisor.EnsureRunningAsync(pluginId, _stop.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                return;
            }
            catch (Exception e)
            {
                _log.Write(LogLevel.Error, "plugins", $"Starting {pluginId} failed: {e}");
                outcome = Outcome.Fail(CoreErrorCode.StartFailed, e.Message, retryable: true);
            }

            Enqueue(() => OnStarted(pluginId, outcome));
        });
    }

    private List<Entry> Waiting(string pluginId) =>
        [.. _instances.Values.Where(entry => entry.PluginId == pluginId && !entry.Created)];

    private Task OnStarted(string pluginId, Outcome outcome)
    {
        if (!outcome.Ok)
        {
            var reason = outcome.Error!.Code switch
            {
                CoreErrorCode.NotFound => PlaceholderReason.Missing,
                CoreErrorCode.PackageInvalid => PlaceholderReason.Invalid,
                CoreErrorCode.ModeUnsupported => PlaceholderReason.NotSupportedInMode,
                CoreErrorCode.HostApiMismatch => PlaceholderReason.Incompatible,
                _ when _supervisor.GetStatus(pluginId).State == PluginState.Unavailable => PlaceholderReason.Unavailable,
                _ => PlaceholderReason.Faulted,
            };
            foreach (var entry in Waiting(pluginId).Where(entry => entry.Reason != PlaceholderReason.Missing))
            {
                Publish(entry, reason, outcome.Error.Detail);
            }

            return Task.CompletedTask;
        }

        CreateWaiting(pluginId);
        return Task.CompletedTask;
    }

    /// <summary>Creates the plugin's instances that are not created yet in its running worker.</summary>
    private void CreateWaiting(string pluginId)
    {
        if (_supervisor.GetChannel(pluginId) is not { } channel)
        {
            return;
        }

        Attach(channel);
        foreach (var entry in Waiting(pluginId).Where(entry => entry.Reason != PlaceholderReason.Missing))
        {
            Create(channel, entry);
        }
    }

    private void Attach(IWorkerChannel channel)
    {
        lock (_attached)
        {
            if (!_attached.Add(channel))
            {
                return;
            }

            _latest[channel.PluginId] = channel;
        }

        channel.NotificationReceived += (_, notification) => Enqueue(() => OnNotification(channel, notification));
    }

    private void Create(IWorkerChannel channel, Entry entry)
    {
        entry.Created = true;
        entry.Channel = channel;
        var instance = entry.Placement.Instance;
        var request = new InstanceCreateParams(SurfaceIdOf(instance.Id), instance.Contribution,
            (JsonObject)instance.Settings.DeepClone(), instance.SettingsVersion, entry.Placement.Context);
        _ = Task.Run(async () =>
        {
            var created = await channel.RequestAsync<InstanceCreateParams, JsonElement?>(Methods.InstanceCreate, request,
                CreateTimeout, _stop.Token).ConfigureAwait(false);
            if (!created.Ok)
            {
                Enqueue(() =>
                {
                    if (_instances.TryGetValue(instance.Id, out var current) && ReferenceEquals(current, entry))
                    {
                        _pluginLogs.Write(entry.PluginId, "host", $"instance.create for {instance.Id} failed: {created.Error!.Detail}");
                        Publish(entry, PlaceholderReason.Faulted, created.Error!.Detail);
                    }

                    return Task.CompletedTask;
                });
            }
        });
    }

    private void Destroy(Entry entry)
    {
        if (entry is { Created: true, Channel: { } channel })
        {
            channel.Notify(Methods.InstanceDestroy, new InstanceDestroyParams(SurfaceIdOf(entry.Placement.Instance.Id)));
        }
    }

    /// <summary>A worker with no placed instance left is stopped, and a pending restart cancelled.</summary>
    private void StopUnused()
    {
        var used = _instances.Values.Select(entry => entry.PluginId).ToHashSet(StringComparer.Ordinal);
        foreach (var status in _supervisor.All.Where(status =>
            status.State is PluginState.Running or PluginState.Faulted && !used.Contains(status.PluginId)))
        {
            _ = _supervisor.StopAsync(status.PluginId, CancellationToken.None);
        }
    }

    private Task OnStatusChanged(PluginStatus status)
    {
        var entries = _instances.Values.Where(entry => entry.PluginId == status.PluginId).ToList();
        switch (status.State)
        {
            case PluginState.Running:
                // A restart after a fault replays instance.create for every instance.
                CreateWaiting(status.PluginId);
                return Task.CompletedTask;
            case PluginState.Starting:
                foreach (var entry in entries.Where(entry => entry.Reason == PlaceholderReason.Unavailable))
                {
                    Publish(entry, PlaceholderReason.Starting, null);
                }

                return Task.CompletedTask;
        }

        lock (_attached)
        {
            // A replacement worker may already be attached; only its predecessors go.
            _attached.RemoveWhere(channel => channel.PluginId == status.PluginId
                && !ReferenceEquals(channel, _latest.GetValueOrDefault(status.PluginId)));
        }

        ForgetPopups(status.PluginId);
        foreach (var entry in entries.Where(entry => entry.Created))
        {
            entry.Created = false;
            entry.Channel = null;
            entry.Model = new SurfaceModel(SurfaceIdOf(entry.Placement.Instance.Id));
            if (status.State is PluginState.Stopped or PluginState.Stopping)
            {
                Publish(entry, PlaceholderReason.Starting, null);
            }
        }

        if (status.State is PluginState.Faulted or PluginState.Unavailable)
        {
            var reason = status.State == PluginState.Faulted ? PlaceholderReason.Faulted : PlaceholderReason.Unavailable;
            var detail = status.Problems.FirstOrDefault()?.Detail ?? status.LastFault?.ToString();
            foreach (var entry in entries.Where(entry => entry.Reason != PlaceholderReason.Missing))
            {
                Publish(entry, reason, detail);
            }
        }

        return Task.CompletedTask;
    }

    private async Task OnCatalogChanged(CatalogChangedEventArgs change)
    {
        if (change.Kind != PackageKind.Plugin || _instances.Values.All(entry => entry.PluginId != change.Id))
        {
            return;
        }

        if (change.Change != CatalogChange.Added)
        {
            await _supervisor.StopAsync(change.Id, _stop.Token).ConfigureAwait(false);
        }

        ForgetPopups(change.Id);

        foreach (var entry in _instances.Values.Where(entry => entry.PluginId == change.Id))
        {
            // The new worker numbers its views from 1 again.
            entry.Created = false;
            entry.Channel = null;
            entry.Model = new SurfaceModel(SurfaceIdOf(entry.Placement.Instance.Id));
            Publish(entry, PlaceholderReason.Starting, null);
        }

        Start(change.Id);
    }

    private Task OnNotification(IWorkerChannel channel, WorkerNotification notification)
    {
        var pluginId = channel.PluginId;
        if (notification.Method is not (Methods.ViewSet or Methods.StatePatch))
        {
            return Task.CompletedTask;
        }

        var surfaceId = notification.Params.ValueKind == JsonValueKind.Object
            && notification.Params.TryGetProperty("surfaceId", out var surfaceElement)
            && surfaceElement.ValueKind == JsonValueKind.String
                ? surfaceElement.GetString()
                : null;
        if (surfaceId?.StartsWith("popup:", StringComparison.Ordinal) == true)
        {
            OnPopupNotification(channel, notification, surfaceId["popup:".Length..]);
            return Task.CompletedTask;
        }

        var entry = Guid.TryParse(surfaceId, out var instanceId) && _instances.TryGetValue(instanceId, out var found)
            && found.PluginId == pluginId && ReferenceEquals(found.Channel, channel)
                ? found
                : null;
        if (entry is null)
        {
            _pluginLogs.Write(pluginId, "host", $"{notification.Method} for surface '{surfaceId}' ignored: it is not one of this plugin's instances.");
            return Task.CompletedTask;
        }

        var update = Apply(entry.Model, notification);
        if (update.Result != SurfaceUpdateResult.Applied)
        {
            LogRejected(pluginId, notification.Method, surfaceId, update);
        }
        else if (notification.Method == Methods.StatePatch)
        {
            PublishLater(entry);
        }
        else
        {
            Publish(entry, entry.Model.View is null ? PlaceholderReason.Starting : null, null);
        }

        return Task.CompletedTask;
    }

    private static SurfaceUpdate Apply(SurfaceModel model, WorkerNotification notification)
    {
        if (notification.Method == Methods.ViewSet)
        {
            return model.ApplyView(notification.Params);
        }

        try
        {
            return notification.Params.Deserialize<StatePatchParams>(ProtocolJson.Options) is { } patch
                ? model.ApplyPatch(patch)
                : new SurfaceUpdate(SurfaceUpdateResult.Rejected, "Empty state.patch.");
        }
        catch (JsonException e)
        {
            return new SurfaceUpdate(SurfaceUpdateResult.Rejected, e.Message);
        }
    }

    private void LogRejected(string pluginId, string method, string? surfaceId, SurfaceUpdate update)
    {
        switch (update.Result)
        {
            case SurfaceUpdateResult.Rejected when method == Methods.ViewSet:
                _pluginLogs.Write(pluginId, "host", $"{WireErrorCodes.InvalidView} InvalidView: view.set for {surfaceId} rejected; the previous view stays. {update.Detail}");
                break;
            case SurfaceUpdateResult.Rejected:
                _pluginLogs.Write(pluginId, "host", $"{WireErrorCodes.InvalidParams} state.patch for {surfaceId} rejected: {update.Detail}");
                break;
            case SurfaceUpdateResult.Stale:
                _pluginLogs.Write(pluginId, "host", $"{method} for {surfaceId} dropped: {update.Detail}");
                break;
        }
    }

    /// <summary>A popup's view is kept per plugin and popup id; a new worker starts it afresh.</summary>
    private void OnPopupNotification(IWorkerChannel channel, WorkerNotification notification, string popupId)
    {
        var pluginId = channel.PluginId;
        if (_catalog.GetManifest(pluginId) is not { } manifest || !manifest.Popups.Contains(popupId))
        {
            _pluginLogs.Write(pluginId, "host", $"{WireErrorCodes.Undeclared} {notification.Method} for popup '{popupId}' ignored: it is not declared under 'popups'.");
            return;
        }

        var key = (pluginId, popupId);
        if (!_popups.TryGetValue(key, out var entry) || !ReferenceEquals(entry.Channel, channel))
        {
            entry = new PopupEntry(channel, new SurfaceModel(PopupSurfaceId(popupId)));
            _popups[key] = entry;
        }

        var update = Apply(entry.Model, notification);
        if (update.Result != SurfaceUpdateResult.Applied)
        {
            LogRejected(pluginId, notification.Method, PopupSurfaceId(popupId), update);
            return;
        }

        var snapshot = new PopupSnapshot(pluginId, popupId, entry.Model.View, entry.Model.ViewRevision,
            new Dictionary<string, JsonNode?>(entry.Model.State, StringComparer.Ordinal));
        lock (_snapshotGate)
        {
            _popupSnapshots[key] = snapshot;
        }

        PopupChanged?.Invoke(this, snapshot);
    }

    private void ForgetPopups(string pluginId)
    {
        foreach (var key in _popups.Keys.Where(key => key.Plugin == pluginId).ToList())
        {
            _popups.Remove(key);
        }

        lock (_snapshotGate)
        {
            foreach (var key in _popupSnapshots.Keys.Where(key => key.Plugin == pluginId).ToList())
            {
                _popupSnapshots.Remove(key);
            }
        }

        PopupOwnerStopped?.Invoke(this, pluginId);
    }

    /// <summary>Publishes the instance once the work queued so far has run, so a burst of patches renders once.</summary>
    private void PublishLater(Entry entry)
    {
        if (entry.PublishPending)
        {
            return;
        }

        entry.PublishPending = true;
        Enqueue(() =>
        {
            if (entry.PublishPending && _instances.TryGetValue(entry.Placement.Instance.Id, out var current) && ReferenceEquals(current, entry))
            {
                Publish(entry, entry.Model.View is null ? PlaceholderReason.Starting : null, null);
            }

            return Task.CompletedTask;
        });
    }

    private void Publish(Entry entry, PlaceholderReason? reason, string? detail)
    {
        entry.PublishPending = false;
        entry.Reason = reason;
        var instance = entry.Placement.Instance;
        var snapshot = new InstanceSnapshot(instance.Id, instance.Plugin, reason, detail,
            reason is null ? entry.Model.View : null, entry.Model.ViewRevision,
            new Dictionary<string, JsonNode?>(entry.Model.State, StringComparer.Ordinal));
        lock (_snapshotGate)
        {
            _snapshots[instance.Id] = snapshot;
        }

        InstanceChanged?.Invoke(this, snapshot);
    }

    private void Forget(Guid instanceId)
    {
        lock (_snapshotGate)
        {
            _snapshots.Remove(instanceId);
        }
    }

    private sealed record PopupEntry(IWorkerChannel Channel, SurfaceModel Model);

    private sealed class Entry(InstancePlacement placement)
    {
        public InstancePlacement Placement { get; } = placement;

        public string PluginId => Placement.Instance.Plugin;

        public SurfaceModel Model { get; set; } = new(placement.Instance.Id.ToString("D"));

        public PlaceholderReason? Reason { get; set; }

        public bool Created { get; set; }

        public bool PublishPending { get; set; }

        public IWorkerChannel? Channel { get; set; }
    }
}

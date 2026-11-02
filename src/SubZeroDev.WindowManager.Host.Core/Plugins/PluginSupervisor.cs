using System.Diagnostics;
using System.Globalization;
using System.IO.Pipes;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SubZeroDev.WindowManager.Host.Core.Diagnostics;
using SubZeroDev.WindowManager.Protocol;
using LogLevel = SubZeroDev.WindowManager.Host.Core.Diagnostics.LogLevel;

namespace SubZeroDev.WindowManager.Host.Core;

/// <summary>
/// Starts and stops plugin workers. Each plugin has its own gate, so at most one worker per id
/// exists (I4). A worker starts only when its manifest is valid and accepts this host's API and
/// mode (I16). It is launched through <see cref="IWorkerLauncher"/> (I3), must complete the
/// handshake within 10 s (I5) and answer <c>initialize</c> within 5 s. The worker's <c>log</c>
/// notifications and stderr go to its plugin log.
/// A running worker is pinged every 5 s and faulted after 10 s without a reply. A faulted worker's
/// process is killed and awaited before anything else starts (I4), then it is restarted after its
/// backoff until the restart budget is spent (I17); it is then Unavailable until <see cref="RetryAsync"/>.
/// A disabled plugin never starts; <see cref="DisableAsync"/> and <see cref="EnableAsync"/> persist the switch.
/// </summary>
public sealed class PluginSupervisor : IPluginSupervisor, IAsyncDisposable
{
    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(3);

    /// <summary>How long a killed process may take to signal before the supervisor moves on.</summary>
    private static readonly TimeSpan ExitWait = TimeSpan.FromSeconds(5);
    private const int MaxFaultHistory = 20;

    private readonly IPackageCatalog _catalog;
    private readonly IWorkerLauncher _launcher;
    private readonly IPluginLogs _pluginLogs;
    private readonly IHostLog _log;
    private readonly Locations _locations;
    private readonly HostMode _mode;
    private readonly string _hostVersion;
    private readonly RestartPolicy _policy;
    private readonly SupervisorTimings _timings;
    private readonly Func<DateTimeOffset> _clock;
    private readonly CancellationTokenSource _disposing = new();
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);

    public PluginSupervisor(IPackageCatalog catalog, IWorkerLauncher launcher, IPluginLogs pluginLogs, IHostLog log,
        Locations locations, HostMode mode, string hostVersion, RestartPolicy policy, SupervisorTimings? timings = null,
        Func<DateTimeOffset>? clock = null)
    {
        _catalog = catalog;
        _launcher = launcher;
        _pluginLogs = pluginLogs;
        _log = log;
        _locations = locations;
        _mode = mode;
        _hostVersion = hostVersion;
        _policy = policy;
        _timings = timings ?? SupervisorTimings.Default;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    public event EventHandler<PluginStatus>? StatusChanged;

    public event EventHandler<IWorkerChannel>? ChannelOpened;

    /// <summary>Answers requests from a worker, given its plugin id. Set once at startup by the command router's owner.</summary>
    public Func<string, RpcMessage, Task<RpcMessage>>? WorkerRequests { get; set; }

    /// <summary>
    /// Receives a worker's notifications other than <c>log</c>, given its plugin id, in channel order. Set
    /// once at startup by the feed broker's owner. Runs from <c>initialize</c> on, before the plugin is Running.
    /// </summary>
    public Action<string, WorkerNotification>? WorkerNotifications { get; set; }

    /// <summary>
    /// Where plugin-level settings are loaded, and migrated through the worker, before <c>initialize</c>. Set once
    /// at startup; without it workers start with empty settings.
    /// </summary>
    public PluginSettingsStore? PluginSettings { get; set; }

    /// <summary>Which plugins are enabled. Set once at startup; without it every plugin counts as enabled.</summary>
    public PluginEnablement? Enablement { get; set; }

    public IReadOnlyList<PluginStatus> All
    {
        get
        {
            lock (_gate)
            {
                return _workers.Values.Select(worker => worker.Status).ToList();
            }
        }
    }

    public PluginStatus GetStatus(string pluginId)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(pluginId, out var worker) ? worker.Status : Discovered(pluginId);
        }
    }

    public IReadOnlyList<PluginFault> FaultsOf(string pluginId)
    {
        Worker? worker;
        lock (_gate)
        {
            _workers.TryGetValue(pluginId, out worker);
        }

        if (worker is null)
        {
            return [];
        }

        lock (worker.History)
        {
            return [.. worker.History];
        }
    }

    public PluginStatus Describe(string pluginId)
    {
        var status = GetStatus(pluginId);
        if (status.State is not (PluginState.Discovered or PluginState.Stopped)
            || Blocked(pluginId, _catalog.GetManifest(pluginId)) is not { } blocked)
        {
            return status;
        }

        return status with { State = blocked.State, Problems = blocked.Problems };
    }

    public IWorkerChannel? GetChannel(string pluginId)
    {
        lock (_gate)
        {
            return _workers.TryGetValue(pluginId, out var worker) && worker.Status.State == PluginState.Running ? worker.Channel : null;
        }
    }

    public async Task<Outcome> EnsureRunningAsync(string pluginId, CancellationToken ct)
    {
        var worker = WorkerFor(pluginId);
        await worker.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            switch (worker.Status.State)
            {
                case PluginState.Running:
                    return Outcome.Success;
                case PluginState.Unavailable:
                    return Outcome.Fail(CoreErrorCode.InvalidState, $"{pluginId} spent its restart budget; it starts again only on Retry.");
                case PluginState.Faulted when worker.PendingRestart is not null:
                    return Outcome.Fail(CoreErrorCode.StartFailed, $"{pluginId} faulted and restarts after its backoff.", retryable: true);
            }

            return await StartAsync(worker, ct).ConfigureAwait(false);
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    public async Task StopAsync(string pluginId, CancellationToken ct)
    {
        Worker? worker;
        lock (_gate)
        {
            _workers.TryGetValue(pluginId, out worker);
        }

        if (worker is null)
        {
            return;
        }

        await worker.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(worker).ConfigureAwait(false);
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    public async Task<Outcome> RetryAsync(string pluginId, CancellationToken ct)
    {
        var worker = WorkerFor(pluginId);
        await worker.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (worker.Status.State is not (PluginState.Faulted or PluginState.Unavailable))
            {
                return Outcome.Fail(CoreErrorCode.InvalidState,
                    $"{pluginId} is {worker.Status.State}; only a faulted or unavailable plugin can be retried.");
            }

            CancelRestart(worker);
            worker.Budget.Reset();
            _pluginLogs.Write(pluginId, "host", "Retry requested; the restart budget is reset.");
            return await StartAsync(worker, ct).ConfigureAwait(false);
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    public async Task<Outcome> DisableAsync(string pluginId, CancellationToken ct)
    {
        var saved = await PersistAsync(pluginId, enabled: false, ct).ConfigureAwait(false);
        if (!saved.Ok)
        {
            return saved;
        }

        var worker = WorkerFor(pluginId);
        await worker.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await StopCoreAsync(worker).ConfigureAwait(false);
            if (worker.Status.State is not (PluginState.Invalid or PluginState.Incompatible))
            {
                SetStatus(worker, PluginState.Disabled);
            }

            _pluginLogs.Write(pluginId, "host", "Disabled.");
            _log.Write(LogLevel.Info, "plugins", $"{pluginId} disabled.");
            return Outcome.Success;
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    public async Task<Outcome> EnableAsync(string pluginId, CancellationToken ct)
    {
        var saved = await PersistAsync(pluginId, enabled: true, ct).ConfigureAwait(false);
        if (!saved.Ok)
        {
            return saved;
        }

        var worker = WorkerFor(pluginId);
        await worker.Gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (worker.Status.State == PluginState.Disabled)
            {
                worker.Budget.Reset();
                SetStatus(worker, PluginState.Discovered);
            }

            _pluginLogs.Write(pluginId, "host", "Enabled.");
            _log.Write(LogLevel.Info, "plugins", $"{pluginId} enabled.");
            return Outcome.Success;
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    public Task StopAllAsync(CancellationToken ct)
    {
        List<string> ids;
        lock (_gate)
        {
            ids = [.. _workers.Keys];
        }

        return Task.WhenAll(ids.Select(id => StopAsync(id, ct)));
    }

    public async ValueTask DisposeAsync()
    {
        await _disposing.CancelAsync().ConfigureAwait(false);
        await StopAllAsync(CancellationToken.None).ConfigureAwait(false);
    }

    internal static string NewPipeName() =>
        $"SubZeroDev.WindowManager.{Environment.ProcessId}.{Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant()}";

    internal static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static PluginStatus Discovered(string pluginId) =>
        new(pluginId, PluginState.Discovered, null, null, [], []);

    private Worker WorkerFor(string pluginId)
    {
        lock (_gate)
        {
            if (!_workers.TryGetValue(pluginId, out var worker))
            {
                _workers[pluginId] = worker = new Worker(Discovered(pluginId), new RestartBudget(_policy, _timings.Backoffs));
            }

            return worker;
        }
    }

    private void SetStatus(Worker worker, PluginState state, int? processId = null, FaultReason? fault = null,
        IReadOnlyList<CoreError>? problems = null)
    {
        PluginStatus status;
        var faults = worker.Budget.Within(_clock());
        lock (_gate)
        {
            status = worker.Status = worker.Status with
            {
                State = state,
                ProcessId = processId,
                LastFault = fault ?? worker.Status.LastFault,
                Problems = problems ?? [],
                RecentFaults = faults,
            };
        }

        StatusChanged?.Invoke(this, status);
    }

    private Task<Outcome> PersistAsync(string pluginId, bool enabled, CancellationToken ct)
    {
        if (_catalog.All(PackageKind.Plugin).All(package => package.Id != pluginId))
        {
            return Task.FromResult(Outcome.Fail(CoreErrorCode.NotFound, $"No plugin package '{pluginId}'."));
        }

        return Enablement is { } enablement
            ? enablement.SetAsync(pluginId, enabled, ct)
            : Task.FromResult(Outcome.Fail(CoreErrorCode.InvalidState, "The enabled switch is not stored by this host."));
    }

    /// <summary>
    /// Why the plugin may not start (I16): the status it then has, its problems and the error to return; null when it may.
    /// </summary>
    private (PluginState State, IReadOnlyList<CoreError> Problems, CoreError Error)? Blocked(string pluginId, PluginManifest? manifest)
    {
        if (manifest is null)
        {
            var record = _catalog.All(PackageKind.Plugin).FirstOrDefault(package => package.Id == pluginId);
            return record is null
                ? (PluginState.Discovered, [], new CoreError(CoreErrorCode.NotFound, $"No plugin package '{pluginId}'.", false))
                : (PluginState.Invalid, record.Problems,
                    new CoreError(CoreErrorCode.PackageInvalid, string.Join(" ", record.Problems.Select(problem => problem.Detail)), false));
        }

        if (!manifest.HostApi.IsSatisfiedBy(ProtocolInfo.HostApiVersion))
        {
            var problem = new CoreError(CoreErrorCode.HostApiMismatch,
                $"{pluginId} needs host API {manifest.HostApi}; this host provides {ProtocolInfo.HostApiVersion}.", false);
            return (PluginState.Incompatible, [problem], problem);
        }

        if (!manifest.Supports(_mode))
        {
            var problem = new CoreError(CoreErrorCode.ModeUnsupported, $"{pluginId} does not support {_mode} mode.", false);
            return (PluginState.Incompatible, [problem], problem);
        }

        if (_mode == HostMode.Replacement && manifest.RequiresExplorer)
        {
            var problem = new CoreError(CoreErrorCode.ModeUnsupported, $"{pluginId} needs Explorer, which does not run in replacement mode.", false);
            return (PluginState.Incompatible, [problem], problem);
        }

        if (Enablement?.IsEnabled(pluginId) == false)
        {
            return (PluginState.Disabled, [], new CoreError(CoreErrorCode.PluginDisabled, $"{pluginId} is disabled.", false));
        }

        return null;
    }

    private async Task<Outcome> StartAsync(Worker worker, CancellationToken ct)
    {
        var pluginId = worker.Status.PluginId;
        var manifest = _catalog.GetManifest(pluginId);
        if (Blocked(pluginId, manifest) is { } blocked)
        {
            SetStatus(worker, blocked.State, problems: blocked.Problems);
            return new Outcome(blocked.Error);
        }

        if (_disposing.IsCancellationRequested)
        {
            return Outcome.Fail(CoreErrorCode.InvalidState, $"{pluginId} cannot start; the supervisor is shut down.");
        }

        SetStatus(worker, PluginState.Starting);
        using var starting = CancellationTokenSource.CreateLinkedTokenSource(ct, _disposing.Token);
        try
        {
            return await LaunchAsync(worker, manifest!, starting.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (starting.IsCancellationRequested)
        {
            _pluginLogs.Write(pluginId, "host", "Start cancelled.");
            SetStatus(worker, PluginState.Stopped);
            ct.ThrowIfCancellationRequested();
            return Outcome.Fail(CoreErrorCode.InvalidState, $"{pluginId} did not start; the supervisor is shutting down.");
        }
        catch (Exception e)
        {
            return Fault(worker, FaultReason.StartFailed, e.Message);
        }
    }

    /// <summary>
    /// Launches the worker, completes the handshake and <c>initialize</c>, and starts watching it.
    /// Whatever fails or is cancelled part-way kills the process and closes the pipe before it propagates.
    /// </summary>
    private async Task<Outcome> LaunchAsync(Worker worker, PluginManifest manifest, CancellationToken ct)
    {
        var pluginId = worker.Status.PluginId;
        var root = _catalog.All(PackageKind.Plugin).First(package => package.Id == pluginId).RootPath;
        var dataDirectory = Path.Combine(_locations.DataRoot, "plugin-data", pluginId);
        Directory.CreateDirectory(dataDirectory);
        var pipeName = NewPipeName();
        var token = NewToken();
        var mode = _mode == HostMode.Coexistence ? "coexistence" : "replacement";
        List<string> arguments =
        [
            .. manifest.Entry.Args,
            "--wm-pipe", pipeName, "--wm-token", token, "--wm-data", dataDirectory, "--wm-mode", mode,
        ];

        var pipe = new NamedPipeServerStream(pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte,
            PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
        IWorkerProcess? process = null;
        WorkerChannel? channel = null;
        try
        {
            var started = await _launcher.StartAsync(
                new WorkerStartInfo(Path.GetFullPath(Path.Combine(root, manifest.Entry.Path)), arguments, root), ct).ConfigureAwait(false);
            if (!started.Ok)
            {
                await pipe.DisposeAsync().ConfigureAwait(false);
                return Fault(worker, FaultReason.StartFailed, started.Error!.Detail);
            }

            process = started.Value!;
            (channel, var outcome) = await ConnectAsync(worker, manifest, pipe, process, token, dataDirectory, ct).ConfigureAwait(false);
            if (channel is null)
            {
                return outcome;
            }
        }
        catch
        {
            if (channel is not null)
            {
                await channel.DisposeAsync().ConfigureAwait(false);
            }

            await pipe.DisposeAsync().ConfigureAwait(false);
            if (process is not null)
            {
                await KillAsync(process).ConfigureAwait(false);
            }

            throw;
        }

        worker.Process = process;
        worker.Channel = channel;
        worker.Stopping = false;
        SetStatus(worker, PluginState.Running, process.ProcessId);
        _log.Write(LogLevel.Info, "plugins", $"{pluginId} running as process {process.ProcessId}.");
        _ = WatchAsync(worker, process, channel);
        return Outcome.Success;
    }

    /// <summary>
    /// Waits for the worker to connect, then completes the handshake and <c>initialize</c>. Returns the open
    /// channel, or no channel and the outcome once the process has been dealt with.
    /// </summary>
    private async Task<(WorkerChannel? Channel, Outcome Outcome)> ConnectAsync(Worker worker, PluginManifest manifest,
        NamedPipeServerStream pipe, IWorkerProcess process, string token, string dataDirectory, CancellationToken ct)
    {
        var pluginId = worker.Status.PluginId;
        _ = PumpStandardErrorAsync(pluginId, process.StandardError);
        _pluginLogs.Write(pluginId, "host", $"Started process {process.ProcessId}.");

        // The handshake deadline covers connecting and hello; the process ending fails it at once.
        using var handshake = CancellationTokenSource.CreateLinkedTokenSource(ct);
        handshake.CancelAfter(WorkerChannel.HandshakeTimeout);
        _ = process.Exited.ContinueWith(_ =>
        {
            try
            {
                handshake.Cancel();
            }
            catch (ObjectDisposedException)
            {
                // The handshake already finished.
            }
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        try
        {
            await pipe.WaitForConnectionAsync(handshake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return (null, await AbortAsync(worker, process, process.Exited.IsCompleted ? FaultReason.Exited : FaultReason.HandshakeTimeout,
                process.Exited.IsCompleted ? $"Exited with code {process.Exited.Result} before connecting." : "Did not connect within 10 s."));
        }

        WorkerChannel? channel;
        HandshakeFailure? failure;
        try
        {
            (channel, failure) = await WorkerChannel.AcceptAsync(pipe, pluginId, process.ProcessId, token,
                WorkerChannel.HandshakeTimeout, handshake.Token, _timings).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return (null, await AbortAsync(worker, process, process.Exited.IsCompleted ? FaultReason.Exited : FaultReason.HandshakeTimeout,
                process.Exited.IsCompleted ? $"Exited with code {process.Exited.Result} during the handshake." : "No hello answered within 10 s."));
        }

        if (channel is null)
        {
            if (failure!.Incompatible)
            {
                _pluginLogs.Write(pluginId, "host", $"{WireErrorCodes.IncompatibleVersion} {failure.Detail}");
                await KillAsync(process).ConfigureAwait(false);
                var problem = new CoreError(CoreErrorCode.HostApiMismatch, failure.Detail, false);
                SetStatus(worker, PluginState.Incompatible, problems: [problem]);
                return (null, new Outcome(problem));
            }

            return (null, await AbortAsync(worker, process, failure.Reason, $"{WireErrorCodes.Unauthorised} {failure.Detail}"));
        }

        channel.NotificationReceived += (_, notification) => OnNotification(pluginId, notification);
        channel.RequestHandler = (_, request) => WorkerRequests is { } serve
            ? serve(pluginId, request)
            : Task.FromResult(RpcMessage.Failure(request.Id, WireErrorCodes.MethodNotFound, $"'{request.Method}' is not served by this host yet."));
        channel.Diagnostic += (_, text) => _pluginLogs.Write(pluginId, "host", text);
        ChannelOpened?.Invoke(this, channel);
        channel.Start();
        var settings = PluginSettings is { } store
            ? await store.LoadAsync(manifest, (fromVersion, stored, cancel) => channel.RequestAsync<SettingsMigrateParams, SettingsMigrateResult>(
                Methods.SettingsMigrate, new SettingsMigrateParams(Scope.Plugin, null, fromVersion, stored), InitializeTimeout, cancel), ct)
                .ConfigureAwait(false)
            : new JsonObject();
        var initialize = await channel.RequestAsync<InitializeParams, JsonElement?>(Methods.Initialize,
            new InitializeParams(ProtocolInfo.ProtocolVersion, _hostVersion, _mode, settings, manifest.SettingsVersion, dataDirectory,
                CultureInfo.CurrentUICulture.Name),
            InitializeTimeout, ct).ConfigureAwait(false);
        if (!initialize.Ok)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return (null, await AbortAsync(worker, process, FaultReason.ProtocolViolation, $"initialize failed: {initialize.Error!.Detail}"));
        }

        return (channel, Outcome.Success);
    }

    private async Task WatchAsync(Worker worker, IWorkerProcess process, WorkerChannel channel)
    {
        using var beat = new CancellationTokenSource();
        var heartbeat = HeartbeatAsync(channel, beat.Token);
        var ended = await Task.WhenAny(process.Exited, channel.Closed, heartbeat).ConfigureAwait(false);
        await beat.CancelAsync().ConfigureAwait(false);
        await worker.Gate.WaitAsync().ConfigureAwait(false);
        var (reason, detail) = (FaultReason.Exited, "The worker ended.");
        try
        {
            if (worker.Stopping || !ReferenceEquals(worker.Channel, channel))
            {
                return;
            }

            (reason, detail) = ended == process.Exited ? (FaultReason.Exited, $"Exited with code {process.Exited.Result}.")
                : ended == heartbeat ? (FaultReason.HeartbeatTimeout, heartbeat.Result ?? "No ping reply.")
                : (channel.Closed.Result.Reason, channel.Closed.Result.Detail);
            worker.Channel = null;
            worker.Process = null;
            await channel.DisposeAsync().ConfigureAwait(false);
            await AbortAsync(worker, process, reason, detail).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            _log.Write(LogLevel.Error, "plugins", $"Watching {worker.Status.PluginId} failed: {e}");
            if (worker.Status.State == PluginState.Running && worker.Channel is null)
            {
                // The channel is gone, so the worker must not stay Running whatever failed on the way.
                Fault(worker, reason, detail);
            }
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    /// <summary>
    /// Pings the worker every <see cref="SupervisorTimings.PingInterval"/>; completes with the fault
    /// detail once a ping is unanswered and no reply has arrived for <see cref="SupervisorTimings.PingTimeout"/>,
    /// or with null when cancelled. A check more than half the timeout late means the host itself stalled,
    /// so the silence is measured again from then rather than blamed on the worker.
    /// </summary>
    private async Task<string?> HeartbeatAsync(WorkerChannel channel, CancellationToken ct)
    {
        var check = TimeSpan.FromMilliseconds(Math.Min(1000, _timings.PingTimeout.TotalMilliseconds / 10));
        var stall = _timings.PingTimeout.TotalMilliseconds / 2;
        var lastReply = Environment.TickCount64;
        var lastPing = lastReply;
        var lastCheck = lastReply;
        long unanswered = 0;
        Task? ping = null;
        try
        {
            while (true)
            {
                await Task.Delay(check, ct).ConfigureAwait(false);
                var now = Environment.TickCount64;
                if (now - lastCheck > stall)
                {
                    _log.Write(LogLevel.Warning, "plugins", $"The heartbeat check ran {now - lastCheck} ms after the last; the ping timeout restarts.");
                    Interlocked.Exchange(ref lastReply, now);
                    Interlocked.Exchange(ref unanswered, 0);
                    lastPing = now - (long)_timings.PingInterval.TotalMilliseconds;
                }

                lastCheck = now;
                var silent = TimeSpan.FromMilliseconds(now - Interlocked.Read(ref lastReply));
                if (Interlocked.Read(ref unanswered) != 0 && silent >= _timings.PingTimeout)
                {
                    return $"No ping reply for {silent.TotalSeconds:0.0} s.";
                }

                if (ping is not { IsCompleted: false } && now - lastPing >= _timings.PingInterval.TotalMilliseconds)
                {
                    lastPing = now;
                    Interlocked.CompareExchange(ref unanswered, now, 0);
                    ping = channel.RequestAsync<object?, JsonElement?>(Methods.Ping, null, _timings.PingTimeout, ct)
                        .ContinueWith(reply =>
                        {
                            if (reply.IsCompletedSuccessfully && reply.Result.Ok)
                            {
                                Interlocked.Exchange(ref lastReply, Environment.TickCount64);
                                Interlocked.Exchange(ref unanswered, 0);
                            }
                        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        catch (OperationCanceledException)
        {
            return null;
        }
    }

    private async Task StopCoreAsync(Worker worker)
    {
        if (CancelRestart(worker) && worker.Status.State == PluginState.Faulted)
        {
            _pluginLogs.Write(worker.Status.PluginId, "host", "Stopped; the pending restart is cancelled.");
            SetStatus(worker, PluginState.Stopped);
            return;
        }

        if (worker is not { Channel: { } channel, Process: { } process })
        {
            return;
        }

        var pluginId = worker.Status.PluginId;
        worker.Stopping = true;
        SetStatus(worker, PluginState.Stopping, process.ProcessId);
        var clock = Stopwatch.StartNew();
        _ = channel.RequestAsync<ShutdownParams, JsonElement?>(Methods.Shutdown,
            new ShutdownParams((int)ShutdownDeadline.TotalMilliseconds), ShutdownDeadline, CancellationToken.None);
        var exited = await Task.WhenAny(process.Exited, Task.Delay(ShutdownDeadline)).ConfigureAwait(false) == process.Exited;
        if (exited)
        {
            _pluginLogs.Write(pluginId, "host", $"Exited with code {process.Exited.Result} after {clock.ElapsedMilliseconds} ms.");
        }
        else
        {
            _pluginLogs.Write(pluginId, "host", $"Killed after {(int)ShutdownDeadline.TotalMilliseconds} ms.");
            _log.Write(LogLevel.Warning, "plugins", $"{pluginId} did not exit within {(int)ShutdownDeadline.TotalMilliseconds} ms and was killed.");
        }

        worker.Channel = null;
        worker.Process = null;
        await channel.DisposeAsync().ConfigureAwait(false);
        await KillAsync(process).ConfigureAwait(false);
        SetStatus(worker, PluginState.Stopped);
    }

    /// <summary>
    /// Records the fault against the restart budget. Within budget the worker is Faulted and restarts
    /// after its backoff; past it, the worker is Unavailable until <see cref="RetryAsync"/> (I17).
    /// Called with the worker's gate held.
    /// </summary>
    private Outcome Fault(Worker worker, FaultReason reason, string detail)
    {
        var pluginId = worker.Status.PluginId;
        _pluginLogs.Write(pluginId, "host", $"Faulted ({reason}): {detail}");
        _log.Write(LogLevel.Warning, "plugins", $"{pluginId} faulted ({reason}): {detail}");
        var now = _clock();
        lock (worker.History)
        {
            worker.History.Add(new PluginFault(now, reason, detail));
            if (worker.History.Count > MaxFaultHistory)
            {
                worker.History.RemoveAt(0);
            }
        }

        var backoff = worker.Budget.RecordFault(now);
        if (backoff is null)
        {
            var spent = $"{_policy.MaxRestarts + 1} faults within {_policy.WindowSeconds} s; the last was {reason}: {detail}";
            _pluginLogs.Write(pluginId, "host", $"Unavailable: {spent}");
            _log.Write(LogLevel.Error, "plugins", $"{pluginId} is unavailable: {spent}");
            SetStatus(worker, PluginState.Unavailable, fault: reason, problems: [new CoreError(CoreErrorCode.StartFailed, spent, true)]);
            return Outcome.Fail(CoreErrorCode.StartFailed, spent, retryable: true);
        }

        var restart = CancellationTokenSource.CreateLinkedTokenSource(_disposing.Token);
        worker.PendingRestart = restart;
        _pluginLogs.Write(pluginId, "host", $"Restarting in {backoff.Value.TotalSeconds:0.###} s.");
        SetStatus(worker, PluginState.Faulted, fault: reason, problems: [new CoreError(CoreErrorCode.StartFailed, detail, true)]);
        _ = RestartAfterAsync(worker, backoff.Value, restart);
        return Outcome.Fail(CoreErrorCode.StartFailed, detail, retryable: true);
    }

    private async Task RestartAfterAsync(Worker worker, TimeSpan backoff, CancellationTokenSource restart)
    {
        try
        {
            await Task.Delay(backoff, restart.Token).ConfigureAwait(false);
            await worker.Gate.WaitAsync(restart.Token).ConfigureAwait(false);
        }
        catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException)
        {
            return;
        }

        try
        {
            if (!ReferenceEquals(worker.PendingRestart, restart))
            {
                return;
            }

            worker.PendingRestart = null;
            restart.Dispose();
            if (worker.Status.State == PluginState.Faulted)
            {
                await StartAsync(worker, _disposing.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (_disposing.IsCancellationRequested)
        {
        }
        catch (Exception e)
        {
            _log.Write(LogLevel.Error, "plugins", $"Restarting {worker.Status.PluginId} failed: {e}");
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    /// <summary>Cancels a scheduled restart; returns whether one was pending. Called with the worker's gate held.</summary>
    private static bool CancelRestart(Worker worker)
    {
        if (worker.PendingRestart is not { } restart)
        {
            return false;
        }

        worker.PendingRestart = null;
        restart.Cancel();
        restart.Dispose();
        return true;
    }

    private async Task<Outcome> AbortAsync(Worker worker, IWorkerProcess process, FaultReason reason, string detail)
    {
        await KillAsync(process).ConfigureAwait(false);
        return Fault(worker, reason, detail);
    }

    /// <summary>Kills the process and waits for its handle to signal, so no restart overlaps it (I4).</summary>
    private async Task KillAsync(IWorkerProcess process)
    {
        process.Kill();
        if (await Task.WhenAny(process.Exited, Task.Delay(ExitWait)).ConfigureAwait(false) != process.Exited)
        {
            _log.Write(LogLevel.Error, "plugins", $"Process {process.ProcessId} did not end within {ExitWait.TotalSeconds:0} s of being killed.");
        }

        process.Dispose();
    }

    private void OnNotification(string pluginId, WorkerNotification notification)
    {
        if (notification.Method != Methods.Log)
        {
            WorkerNotifications?.Invoke(pluginId, notification);
            return;
        }

        try
        {
            if (notification.Params.Deserialize<LogParams>(ProtocolJson.Options) is { } entry)
            {
                var text = entry.Exception is null ? entry.Message : $"{entry.Message} {entry.Exception}";
                _pluginLogs.Write(pluginId, entry.Level.ToString(), text);
            }
        }
        catch (JsonException e)
        {
            _pluginLogs.Write(pluginId, "host", $"{WireErrorCodes.InvalidParams} Unreadable log message: {e.Message}");
        }
    }

    private async Task PumpStandardErrorAsync(string pluginId, Stream stream)
    {
        try
        {
            using var reader = new StreamReader(stream);
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                _pluginLogs.Write(pluginId, "stderr", line);
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException)
        {
        }
    }

    private sealed class Worker(PluginStatus status, RestartBudget budget)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public PluginStatus Status { get; set; } = status;

        public RestartBudget Budget { get; } = budget;

        /// <summary>The last <see cref="MaxFaultHistory"/> faults, oldest first. Lock it to read or write.</summary>
        public List<PluginFault> History { get; } = [];

        public CancellationTokenSource? PendingRestart { get; set; }

        public IWorkerProcess? Process { get; set; }

        public WorkerChannel? Channel { get; set; }

        public bool Stopping { get; set; }
    }
}

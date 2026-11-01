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
/// </summary>
public sealed class PluginSupervisor : IPluginSupervisor, IAsyncDisposable
{
    public static readonly TimeSpan InitializeTimeout = TimeSpan.FromSeconds(5);
    public static readonly TimeSpan ShutdownDeadline = TimeSpan.FromSeconds(3);

    private readonly IPackageCatalog _catalog;
    private readonly IWorkerLauncher _launcher;
    private readonly IPluginLogs _pluginLogs;
    private readonly IHostLog _log;
    private readonly Locations _locations;
    private readonly HostMode _mode;
    private readonly string _hostVersion;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Worker> _workers = new(StringComparer.Ordinal);

    public PluginSupervisor(IPackageCatalog catalog, IWorkerLauncher launcher, IPluginLogs pluginLogs, IHostLog log,
        Locations locations, HostMode mode, string hostVersion)
    {
        _catalog = catalog;
        _launcher = launcher;
        _pluginLogs = pluginLogs;
        _log = log;
        _locations = locations;
        _mode = mode;
        _hostVersion = hostVersion;
    }

    public event EventHandler<PluginStatus>? StatusChanged;

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
            if (worker.Status.State == PluginState.Running)
            {
                return Outcome.Success;
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

    public Task StopAllAsync(CancellationToken ct)
    {
        List<string> ids;
        lock (_gate)
        {
            ids = [.. _workers.Keys];
        }

        return Task.WhenAll(ids.Select(id => StopAsync(id, ct)));
    }

    public ValueTask DisposeAsync() => new(StopAllAsync(CancellationToken.None));

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
                _workers[pluginId] = worker = new Worker(Discovered(pluginId));
            }

            return worker;
        }
    }

    private void SetStatus(Worker worker, PluginState state, int? processId = null, FaultReason? fault = null,
        IReadOnlyList<CoreError>? problems = null)
    {
        PluginStatus status;
        lock (_gate)
        {
            var faults = fault is null ? worker.Status.RecentFaults : [.. worker.Status.RecentFaults, DateTimeOffset.Now];
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

    private async Task<Outcome> StartAsync(Worker worker, CancellationToken ct)
    {
        var pluginId = worker.Status.PluginId;
        var manifest = _catalog.GetManifest(pluginId);
        if (manifest is null)
        {
            var record = _catalog.All(PackageKind.Plugin).FirstOrDefault(package => package.Id == pluginId);
            if (record is null)
            {
                SetStatus(worker, PluginState.Discovered);
                return Outcome.Fail(CoreErrorCode.NotFound, $"No plugin package '{pluginId}'.");
            }

            SetStatus(worker, PluginState.Invalid, problems: record.Problems);
            return Outcome.Fail(CoreErrorCode.PackageInvalid, string.Join(" ", record.Problems.Select(problem => problem.Detail)));
        }

        if (!manifest.HostApi.IsSatisfiedBy(ProtocolInfo.HostApiVersion))
        {
            var problem = new CoreError(CoreErrorCode.HostApiMismatch,
                $"{pluginId} needs host API {manifest.HostApi}; this host provides {ProtocolInfo.HostApiVersion}.", false);
            SetStatus(worker, PluginState.Incompatible, problems: [problem]);
            return new Outcome(problem);
        }

        if (!manifest.Supports(_mode))
        {
            var problem = new CoreError(CoreErrorCode.ModeUnsupported, $"{pluginId} does not support {_mode} mode.", false);
            SetStatus(worker, PluginState.Incompatible, problems: [problem]);
            return new Outcome(problem);
        }

        SetStatus(worker, PluginState.Starting);
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
        var started = await _launcher.StartAsync(
            new WorkerStartInfo(Path.GetFullPath(Path.Combine(root, manifest.Entry.Path)), arguments, root), ct).ConfigureAwait(false);
        if (!started.Ok)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return Fault(worker, FaultReason.StartFailed, started.Error!.Detail);
        }

        var process = started.Value!;
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
            return Abort(worker, process, process.Exited.IsCompleted ? FaultReason.Exited : FaultReason.HandshakeTimeout,
                process.Exited.IsCompleted ? $"Exited with code {process.Exited.Result} before connecting." : "Did not connect within 10 s.");
        }

        WorkerChannel? channel;
        HandshakeFailure? failure;
        try
        {
            (channel, failure) = await WorkerChannel.AcceptAsync(pipe, pluginId, process.ProcessId, token,
                WorkerChannel.HandshakeTimeout, handshake.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            await pipe.DisposeAsync().ConfigureAwait(false);
            return Abort(worker, process, process.Exited.IsCompleted ? FaultReason.Exited : FaultReason.HandshakeTimeout,
                process.Exited.IsCompleted ? $"Exited with code {process.Exited.Result} during the handshake." : "No hello answered within 10 s.");
        }

        if (channel is null)
        {
            if (failure!.Incompatible)
            {
                _pluginLogs.Write(pluginId, "host", $"{WireErrorCodes.IncompatibleVersion} {failure.Detail}");
                process.Kill();
                process.Dispose();
                var problem = new CoreError(CoreErrorCode.HostApiMismatch, failure.Detail, false);
                SetStatus(worker, PluginState.Incompatible, problems: [problem]);
                return new Outcome(problem);
            }

            return Abort(worker, process, failure.Reason, $"{WireErrorCodes.Unauthorised} {failure.Detail}");
        }

        channel.NotificationReceived += (_, notification) => OnNotification(pluginId, notification);
        channel.Start();
        var initialize = await channel.RequestAsync<InitializeParams, JsonElement?>(Methods.Initialize,
            new InitializeParams(ProtocolInfo.ProtocolVersion, _hostVersion, _mode, new JsonObject(), 0, dataDirectory,
                CultureInfo.CurrentUICulture.Name),
            InitializeTimeout, ct).ConfigureAwait(false);
        if (!initialize.Ok)
        {
            await channel.DisposeAsync().ConfigureAwait(false);
            return Abort(worker, process, FaultReason.ProtocolViolation, $"initialize failed: {initialize.Error!.Detail}");
        }

        worker.Process = process;
        worker.Channel = channel;
        worker.Stopping = false;
        SetStatus(worker, PluginState.Running, process.ProcessId);
        _log.Write(LogLevel.Info, "plugins", $"{pluginId} running as process {process.ProcessId}.");
        _ = WatchAsync(worker, process, channel);
        return Outcome.Success;
    }

    private async Task WatchAsync(Worker worker, IWorkerProcess process, WorkerChannel channel)
    {
        var ended = await Task.WhenAny(process.Exited, channel.Closed).ConfigureAwait(false);
        await worker.Gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (worker.Stopping || !ReferenceEquals(worker.Channel, channel))
            {
                return;
            }

            var (reason, detail) = ended == process.Exited
                ? (FaultReason.Exited, $"Exited with code {process.Exited.Result}.")
                : (channel.Closed.Result.Reason, channel.Closed.Result.Detail);
            worker.Channel = null;
            worker.Process = null;
            await channel.DisposeAsync().ConfigureAwait(false);
            Abort(worker, process, reason, detail);
        }
        finally
        {
            worker.Gate.Release();
        }
    }

    private async Task StopCoreAsync(Worker worker)
    {
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
            process.Kill();
            _pluginLogs.Write(pluginId, "host", $"Killed after {(int)ShutdownDeadline.TotalMilliseconds} ms.");
            _log.Write(LogLevel.Warning, "plugins", $"{pluginId} did not exit within {(int)ShutdownDeadline.TotalMilliseconds} ms and was killed.");
        }

        worker.Channel = null;
        worker.Process = null;
        await channel.DisposeAsync().ConfigureAwait(false);
        process.Dispose();
        SetStatus(worker, PluginState.Stopped);
    }

    private Outcome Fault(Worker worker, FaultReason reason, string detail)
    {
        var pluginId = worker.Status.PluginId;
        _pluginLogs.Write(pluginId, "host", $"Faulted ({reason}): {detail}");
        _log.Write(LogLevel.Warning, "plugins", $"{pluginId} faulted ({reason}): {detail}");
        SetStatus(worker, PluginState.Faulted, fault: reason,
            problems: [new CoreError(CoreErrorCode.StartFailed, detail, true)]);
        return Outcome.Fail(CoreErrorCode.StartFailed, detail, retryable: true);
    }

    private Outcome Abort(Worker worker, IWorkerProcess process, FaultReason reason, string detail)
    {
        process.Kill();
        process.Dispose();
        return Fault(worker, reason, detail);
    }

    private void OnNotification(string pluginId, WorkerNotification notification)
    {
        if (notification.Method != Methods.Log)
        {
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

    private sealed class Worker(PluginStatus status)
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public PluginStatus Status { get; set; } = status;

        public IWorkerProcess? Process { get; set; }

        public WorkerChannel? Channel { get; set; }

        public bool Stopping { get; set; }
    }
}

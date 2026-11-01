using System.Text.Json;
using SubZeroDev.WindowManager.Protocol;

namespace SubZeroDev.WindowManager.Host.Core;

// ---- packages
public enum PackageKind { Plugin, Theme, Palette, AssetPack }

public enum PackageSource { BuiltIn, User }

public sealed record PackageRecord(PackageKind Kind, string Id, SemVersion Version, PackageSource Source,
    string RootPath, bool OverridesBuiltIn, PackageHealth Health, IReadOnlyList<CoreError> Problems);

public enum PackageHealth { Valid, Invalid }

public interface IPackageCatalog
{
    IReadOnlyList<PackageRecord> All(PackageKind kind);

    PluginManifest? GetManifest(string pluginId);

    Task<Outcome<PackageRecord>> InstallAsync(PackageKind kind, string zipOrFolderPath, CancellationToken ct);

    Task<Outcome> RemoveAsync(PackageKind kind, string id, CancellationToken ct);   // user packages only

    event EventHandler<CatalogChangedEventArgs> Changed;
}

public sealed record CatalogChangedEventArgs(PackageKind Kind, string Id, CatalogChange Change);

public enum CatalogChange { Added, Updated, Removed }

// ---- plugins
public enum PluginState { Discovered, Invalid, Incompatible, Disabled, Starting, Running, Stopping, Stopped, Faulted, Unavailable }

public sealed record PluginStatus(string PluginId, PluginState State, int? ProcessId,
    FaultReason? LastFault, IReadOnlyList<CoreError> Problems, IReadOnlyList<DateTimeOffset> RecentFaults);

public enum FaultReason { Exited, PipeBroken, HeartbeatTimeout, HandshakeTimeout, ProtocolViolation, Flood, StartFailed }

/// <summary>
/// The S2 part of the contract's <c>IPluginSupervisor</c>: start on demand and stop. Enable, disable,
/// retry and debug-attach arrive with their slices.
/// </summary>
public interface IPluginSupervisor
{
    PluginStatus GetStatus(string pluginId);

    IReadOnlyList<PluginStatus> All { get; }

    /// <summary>Starts the plugin's worker unless it is already starting or running (I4, I16).</summary>
    Task<Outcome> EnsureRunningAsync(string pluginId, CancellationToken ct);

    /// <summary>Shuts the worker down, killing it if it has not exited within 3 s.</summary>
    Task StopAsync(string pluginId, CancellationToken ct);

    Task StopAllAsync(CancellationToken ct);

    IWorkerChannel? GetChannel(string pluginId);                         // null unless Running

    event EventHandler<PluginStatus> StatusChanged;
}

public interface IWorkerChannel
{
    string PluginId { get; }

    int ProcessId { get; }

    Task<Outcome<TResult>> RequestAsync<TParams, TResult>(string method, TParams args, TimeSpan timeout, CancellationToken ct);

    Outcome Notify<TParams>(string method, TParams args);   // non-blocking enqueue; fails if closed

    event EventHandler<WorkerNotification> NotificationReceived;   // in channel order (I12)
}

public sealed record WorkerNotification(string Method, JsonElement Params);

public interface IWorkerLauncher
{
    /// <summary>Creates the process suspended, puts it in the host's kill-on-close job, then resumes it (I3).</summary>
    Task<Outcome<IWorkerProcess>> StartAsync(WorkerStartInfo info, CancellationToken ct);

    /// <summary>The ids of the processes in the host's job, for diagnostics and tests.</summary>
    IReadOnlyList<int> JobProcessIds();
}

public sealed record WorkerStartInfo(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory);

public interface IWorkerProcess : IDisposable
{
    int ProcessId { get; }

    /// <summary>The worker's stderr; stdin and stdout are not connected.</summary>
    Stream StandardError { get; }

    /// <summary>Completes with the exit code when the process ends.</summary>
    Task<int> Exited { get; }

    void Kill();
}

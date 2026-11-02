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

/// <summary>A fault the supervisor recorded: when, why and what it said.</summary>
public sealed record PluginFault(DateTimeOffset At, FaultReason Reason, string Detail);

/// <summary>
/// The contract's <c>IPluginSupervisor</c>: start on demand, stop, restart after a fault, retry, enable
/// and disable. Debug-attach arrives with its slice.
/// </summary>
public interface IPluginSupervisor
{
    PluginStatus GetStatus(string pluginId);

    IReadOnlyList<PluginStatus> All { get; }

    /// <summary>Starts the plugin's worker unless it is already starting or running (I4, I16).</summary>
    Task<Outcome> EnsureRunningAsync(string pluginId, CancellationToken ct);

    /// <summary>Shuts the worker down, killing it if it has not exited within 3 s, and cancels a pending restart.</summary>
    Task StopAsync(string pluginId, CancellationToken ct);

    /// <summary>Unavailable or Faulted → Starting, with the restart budget reset; otherwise InvalidState.</summary>
    Task<Outcome> RetryAsync(string pluginId, CancellationToken ct);

    /// <summary>Persists <c>enabled: true</c>; a Disabled plugin becomes Discovered, so it starts when an instance needs it.</summary>
    Task<Outcome> EnableAsync(string pluginId, CancellationToken ct) =>
        Task.FromResult(Outcome.Fail(CoreErrorCode.InvalidState, "This supervisor cannot enable plugins."));

    /// <summary>Persists <c>enabled: false</c>, then stops the worker as <see cref="StopAsync"/> does; the plugin is Disabled.</summary>
    Task<Outcome> DisableAsync(string pluginId, CancellationToken ct) =>
        Task.FromResult(Outcome.Fail(CoreErrorCode.InvalidState, "This supervisor cannot disable plugins."));

    /// <summary>
    /// The status, except that a plugin that has not started (or has stopped) shows why it would not start:
    /// Invalid, Incompatible or Disabled, with its problems.
    /// </summary>
    PluginStatus Describe(string pluginId) => GetStatus(pluginId);

    /// <summary>The plugin's most recent faults since the host started, oldest first, with their reasons.</summary>
    IReadOnlyList<PluginFault> FaultsOf(string pluginId) => [];

    Task StopAllAsync(CancellationToken ct);

    IWorkerChannel? GetChannel(string pluginId);                         // null unless Running

    event EventHandler<PluginStatus> StatusChanged;

    /// <summary>A worker's channel, after the handshake and before it reads its first message.</summary>
    event EventHandler<IWorkerChannel> ChannelOpened { add { } remove { } }
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

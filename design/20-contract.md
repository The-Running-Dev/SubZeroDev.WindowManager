# Contract — SubZeroDev.WindowManager

Written from [10-design.md](10-design.md). `/next` builds against this. When the code departs from it, the pull request says so; this file is not kept in sync.

Root namespace: `SubZeroDev.WindowManager`. Target: .NET 10, C# latest. All declarations below are signatures only.

## Invariants

Each is written so it can become an assertion or an architecture test. The owner maintains it.

| # | Invariant | Owner |
|---|---|---|
| I1 | The host process never loads an assembly from a plugin package, and no plugin package code runs in the host process. | Host.Core (catalog, supervisor) and an architecture test |
| I2 | Plugin projects reference `Sdk` (and through it `Protocol`) and nothing else from this repository. | Build check |
| I3 | Every worker process is assigned to the host's job object, which has kill-on-close set, before the worker's first instruction runs (created suspended, assigned, then resumed). No worker outlives the host. | Host.Platform (`IWorkerLauncher`) |
| I4 | For each plugin id, at most one worker process exists at any time. A restart's new process is created only after the previous process handle has signalled. | Host.Core (`IPluginSupervisor`) |
| I5 | No channel processes any message other than `hello` until `hello` has presented the launch's one-time token. A connection whose first message is not a valid `hello` within 10 s is closed. | Host.Core (`WorkerChannel`) |
| I6 | Every reversible desktop change is appended to the recovery journal and flushed to disk **before** it is applied, and its entry is removed only **after** it has been reverted. | Host.Core (journal writer) and Host.Platform (app bars) |
| I7 | When the host exits normally, the journal holds zero entries and `cleanExit` is `true`. When the guardian finishes recovering, the journal holds zero entries. | Host.App, Guardian |
| I8 | At most one host and at most one guardian run per user session (`Local\` named mutexes). | Host.App, Guardian |
| I9 | The host UI thread never awaits pipe IO, file IO, process start, or icon extraction. Each such call happens on a background task, and only its result is posted to the dispatcher. | All host modules. Checked by a debug-build dispatcher watchdog that logs any UI-thread stall over 50 ms. |
| I10 | Every placed contribution instance shows either the latest valid view from its plugin's **current** worker or a host placeholder. A view from a previous worker process is cleared when that process faults. | Host.Surfaces |
| I11 | A view tree accepted by the host has unique element ids, only known element kinds, no literal colour, font, brush or pixel size, and bindings that name only state keys or item fields. An invalid tree is rejected whole, and the previous view stays. | Host.Surfaces (validator in Protocol) |
| I12 | For one surface, state patches are applied in send order. A patch whose `revision` is lower than the surface's current view revision is dropped. | Host.Core (channel) and Host.Surfaces |
| I13 | An appearance change is all or nothing. After `IThemeEngine.ApplyAsync` returns, every surface uses the new dictionary (success) or every surface still uses the previous one (failure). | Host.Theming |
| I14 | Every configuration write is atomic: write a temp file in the same directory, flush it, then replace. The host never writes a document whose last load failed validation or whose `schemaVersion` is newer than it supports. | Host.Core (`IConfigStore`) |
| I15 | A command id resolves to at most one provider at any moment. | Host.Core (`ICommandRouter`) |
| I16 | A plugin reaches `Starting` only when all of these hold: its manifest is valid; the host API version satisfies `hostApi`; the current mode is in `modes`; every non-optional dependency is present, enabled, compatible and `Running`; and it is enabled. | Host.Core (`IPluginSupervisor`) |
| I17 | Automatic restarts of one plugin within any `restartPolicy.windowSeconds` window never exceed `restartPolicy.maxRestarts`. The same bound applies to the guardian relaunching the host. | Host.Core, Guardian |
| I18 | Before the host sends a worker a message caused by user input (`element.event`, `command.invoke` from a click, key or hotkey, or `popup.opened`), it grants that worker's process foreground rights. | Host.Surfaces through `IForegroundService` |
| I19 | Command invocations are never retried automatically. | Host.Core (`ICommandRouter`) |
| I20 | Within a worker, the SDK invokes plugin callbacks one at a time, in message arrival order. | Sdk |
| I21 | Pipes are created with a current-user-only ACL and a name containing a fresh 128-bit random value per launch. | Host.Platform |

## Public surface

### Process command lines

```text
SubZeroDev.WindowManager.exe                       # guardian; normal launch
SubZeroDev.WindowManager.exe --attach <hostPid>    # guardian relaunched by a live host
SubZeroDev.WindowManager.exe --recover-only        # revert a stale journal, then exit
SubZeroDev.WindowManager.exe --first-run           # register shortcut (+ Run key if startAtSignIn), then normal launch
SubZeroDev.WindowManager.exe --uninstall           # remove shortcut and Run key, then exit
SubZeroDev.WindowManager.Host.exe --guardian-pid <pid> --guardian-pipe <name>
<plugin entry> [manifest args] --wm-pipe <name> --wm-token <base64url 32 bytes> --wm-data <dir> --wm-mode coexistence|replacement
wm-pack <buildOutputDir> --manifest <plugin.json> --out <package.zip>
```

Exit codes. Guardian: `0` normal, `2` another guardian running, `3` the restart budget was spent. Host: `0` normal, `2` another host running, `10` fatal startup failure (logged). `wm-pack`: `0` ok, `1` invalid manifest, `2` entry executable missing from the output, `3` IO.

### Wire protocol (Protocol)

Protocol version **1**. The host API version is **1.0.0**, the SemVer the manifest `hostApi` range is checked against. Transport: one duplex named pipe per worker, carrying JSON-RPC 2.0 messages framed by a 4-byte little-endian length prefix, UTF-8, at most 4 MiB per message. JSON uses camelCase, ignores unknown properties, and omits nulls.

```csharp
namespace SubZeroDev.WindowManager.Protocol;

public static class ProtocolInfo
{
    public const int ProtocolVersion = 1;
    public static readonly SemVersion HostApiVersion; // 1.0.0
    public const int MaxMessageBytes = 4 * 1024 * 1024;
}

public static class Methods
{
    // worker -> host
    public const string Hello = "hello";                       // request
    public const string ViewSet = "view.set";                  // notification
    public const string StatePatch = "state.patch";            // notification
    public const string PopupOpen = "popup.open";              // request
    public const string PopupClose = "popup.close";            // request
    public const string CommandInvoke = "command.invoke";      // request (both directions)
    public const string FeedPublish = "feed.publish";          // notification
    public const string SettingsSave = "settings.save";        // request
    public const string Log = "log";                           // notification
    // host -> worker
    public const string Initialize = "initialize";             // request
    public const string SettingsMigrate = "settings.migrate";  // request
    public const string InstanceCreate = "instance.create";    // request
    public const string InstanceSettingsChanged = "instance.settingsChanged"; // notification
    public const string InstanceDestroy = "instance.destroy";  // request
    public const string PluginSettingsChanged = "plugin.settingsChanged";     // notification
    public const string ElementEvent = "element.event";        // notification
    public const string PopupOpened = "popup.opened";          // notification
    public const string PopupClosed = "popup.closed";          // notification
    public const string FeedUpdate = "feed.update";            // notification
    public const string Ping = "ping";                         // request
    public const string Shutdown = "shutdown";                 // request
}

public sealed record HelloParams(string Token, string PluginId, int ProtocolVersion, string SdkVersion);
public sealed record HelloResult(bool Accepted);

public sealed record InitializeParams(
    int ProtocolVersion, string HostVersion, HostMode Mode,
    JsonObject PluginSettings, int PluginSettingsVersion,
    string DataDirectory, string Culture);
public sealed record InitializeResult(); // worker replies once its handlers are registered

public enum HostMode { Coexistence, Replacement }

public sealed record SettingsMigrateParams(Scope Scope, string? InstanceId, int FromVersion, JsonObject Settings);
public sealed record SettingsMigrateResult(int Version, JsonObject Settings);
public enum Scope { Plugin, Instance }

public sealed record InstanceCreateParams(
    string InstanceId, string ContributionId, JsonObject Settings, int SettingsVersion,
    SurfaceContext Context);
public sealed record SurfaceContext(string MonitorId, double Scale, Orientation PanelAxis, PanelEdge? Edge);
public enum Orientation { Horizontal, Vertical }
public enum PanelEdge { Top, Bottom, Left, Right }

public sealed record InstanceSettingsChangedParams(string InstanceId, JsonObject Settings);
public sealed record InstanceDestroyParams(string InstanceId);
public sealed record PluginSettingsChangedParams(JsonObject Settings);

// A surface is an instance's main view (SurfaceId = instance id) or a plugin popup (SurfaceId = "popup:" + popup id).
public sealed record ViewSetParams(string SurfaceId, long Revision, ViewNode Root);
public sealed record StatePatchParams(string SurfaceId, long Revision,
    IReadOnlyDictionary<string, JsonNode?>? Set, IReadOnlyList<string>? Remove);

public sealed record PopupOpenParams(string PopupId, PopupAnchor Anchor);
public sealed record PopupAnchor(ElementAnchor? Element, MonitorAnchor? Monitor); // exactly one is set
public sealed record ElementAnchor(string SurfaceId, string ElementId);
public enum MonitorAnchor { Active, Pointer, Primary }
public sealed record PopupCloseParams(string PopupId);
public sealed record PopupOpenedParams(string PopupId, PopupTrigger Trigger);
public sealed record PopupClosedParams(string PopupId, PopupCloseReason Reason);
public enum PopupTrigger { Element, Command }
public enum PopupCloseReason { LightDismiss, Escape, Requested, OwnerFaulted }

public sealed record CommandInvokeParams(string CommandId, JsonObject? Args, InvocationSource Source);
public enum InvocationSource { Pointer, Keyboard, Hotkey, Plugin, Host }
public sealed record CommandResult(JsonNode? Value);

public sealed record ElementEventParams(string SurfaceId, string ElementId, ElementEventKind Kind,
    string? ItemKey, JsonNode? Value, InvocationSource Source);
public enum ElementEventKind { Invoked, Toggled, TextChanged, TextSubmitted, SelectionChanged, ItemInvoked }

public sealed record FeedPublishParams(string FeedId, JsonNode Snapshot);
public sealed record FeedUpdateParams(string FeedId, bool Available, JsonNode? Snapshot);

public sealed record SettingsSaveParams(Scope Scope, string? InstanceId, JsonObject Settings);
public sealed record LogParams(LogLevel Level, string Message, string? Exception);
public enum LogLevel { Trace, Debug, Information, Warning, Error }

public sealed record ShutdownParams(int DeadlineMilliseconds);
```

**Callers may rely on:**
- Per channel, messages arrive in send order.
- Requests always get exactly one response or a channel close.
- `instance.create` for an instance comes after `initialize` and before any `element.event` for that instance.
- `shutdown` is the last request a healthy worker receives.

**Callers must never:**
- Send `view.set` or `state.patch` for a surface they do not own.
- Send a revision lower than one they already sent for that surface.
- Write to standard output expecting the host to read it. Standard error is captured into the plugin log.

### View model (Protocol)

```csharp
public sealed record ViewNode(
    string Id, ElementKind Kind,
    IReadOnlyList<string>? Classes = null,
    IReadOnlyDictionary<string, JsonNode?>? Props = null,   // literal or binding
    IReadOnlyList<ViewNode>? Children = null,              // Stack only
    ViewNode? ItemTemplate = null,                         // List only
    ElementAction? Action = null);

public enum ElementKind { Text, Icon, Image, Button, Toggle, Stack, List, Progress, TextInput, Separator, Spacer }

public sealed record ElementAction(InvokeAction? Invoke, string? Event, string? Popup); // exactly one is set
public sealed record InvokeAction(string Command, JsonObject? Args); // Args values may be bindings

// Binding encodings inside Props and Args:
//   { "$state": "<key>" }   value of a state key of the owning surface
//   { "$item": "<field>" }  field of the current List item (inside ItemTemplate only)
// IconRef encodings:
//   { "named": "<icon id>" } | { "app": { "exe"?: "<path>", "aumid"?: "<id>" } } | { "window": <hwnd as number> }
//   | { "file": "<package-relative path>" }

public static class ViewValidator
{
    public static ValidationResult Validate(ViewNode root);   // enforces I11
}
public sealed record ValidationResult(bool IsValid, IReadOnlyList<ValidationIssue> Issues);
public sealed record ValidationIssue(string ElementId, string Code, string Detail);
```

Props per kind. Every kind also takes the common props `automationName: string` (required on Button, Toggle, TextInput and List), `tooltip: string`, `visible: bool`.

| Kind | Props |
|---|---|
| Text | `text: string`, `wrap: bool = false` |
| Icon | `icon: IconRef`, `size: "small"\|"medium"\|"large" = "medium"` |
| Image | `source: { "file": path } \| { "named": id }`, `fit: "contain"\|"cover" = "contain"` |
| Button | `label?: string`, `icon?: IconRef`, `active: bool = false`, `attention: bool = false`, `badge?: string` |
| Toggle | `label?: string`, `icon?: IconRef`, `checked: bool` |
| Stack | `orientation: "horizontal"\|"vertical"\|"panel" = "panel"` (`panel` follows the panel axis) |
| List | `items: array of objects, each with a unique string "key"`, `orientation: as Stack`, `selection: "none"\|"single" = "none"`, `selectedKey?: string` |
| Progress | `value: number 0..1 or null` (null means indeterminate) |
| TextInput | `value: string`, `placeholder?: string`, `navigates?: <List element id>` (Up, Down and Enter drive that list) |
| Separator | none |
| Spacer | `flex: bool = false` |

### Plugin SDK (Sdk)

```csharp
namespace SubZeroDev.WindowManager.Sdk;

public static class PluginRuntime
{
    // Call from Main. Parses --wm-* arguments, connects, runs until shutdown. Returns the process exit code.
    public static Task<int> RunAsync<TPlugin>(string[] args) where TPlugin : Plugin, new();
}

public abstract class Plugin
{
    protected internal PluginContext Context { get; }
    protected internal virtual Task OnStartAsync(CancellationToken ct);                 // register commands and feeds here
    protected internal virtual Task OnStopAsync(CancellationToken ct);                  // ct fires at the shutdown deadline
    protected internal abstract ContributionInstance CreateInstance(InstanceRequest request);
    protected internal virtual Task<MigratedSettings> MigrateSettingsAsync(SettingsScope scope, int fromVersion,
        JsonObject settings, CancellationToken ct);                                     // default: returns settings unchanged
    protected internal virtual Task OnPluginSettingsChangedAsync(JsonObject settings, CancellationToken ct);
    protected internal virtual Task OnPopupOpenedAsync(PopupSurface popup, PopupTrigger trigger, CancellationToken ct);
    protected internal virtual Task OnPopupClosedAsync(PopupSurface popup, PopupCloseReason reason, CancellationToken ct);
}

public enum SettingsScope { Plugin, Instance }
public sealed record MigratedSettings(int Version, JsonObject Settings);

public sealed class PluginContext
{
    public string PluginId { get; }
    public HostMode Mode { get; }
    public string DataDirectory { get; }
    public JsonObject Settings { get; }
    public ICommands Commands { get; }
    public IFeeds Feeds { get; }
    public IPopups Popups { get; }
    public ILogger Log { get; }   // Microsoft.Extensions.Logging abstraction, forwarded as `log`
    public Task<SdkResult> SaveSettingsAsync(JsonObject settings, CancellationToken ct);
}

public sealed record InstanceRequest(string InstanceId, string ContributionId, JsonObject Settings,
    SurfaceContext Context, Surface Surface);

public abstract class ContributionInstance : IAsyncDisposable
{
    protected ContributionInstance(InstanceRequest request);
    public string InstanceId { get; }
    public string ContributionId { get; }
    protected Surface Surface { get; }
    protected internal virtual Task OnCreatedAsync(CancellationToken ct);   // set the first view here
    protected internal virtual Task OnEventAsync(ElementEvent e, CancellationToken ct);
    protected internal virtual Task OnSettingsChangedAsync(JsonObject settings, CancellationToken ct);
    public virtual ValueTask DisposeAsync();
    public Task<SdkResult> SaveSettingsAsync(JsonObject settings, CancellationToken ct);
}

public class Surface
{
    public string SurfaceId { get; }
    public void SetView(ViewNode root);                     // validates (I11) and throws InvalidViewException; bumps the revision
    public void SetState(string key, JsonNode? value);      // coalesced, sent in order
    public void RemoveState(string key);
}

public sealed class PopupSurface : Surface
{
    public string PopupId { get; }
    public Task<SdkResult> OpenAsync(PopupAnchor anchor, CancellationToken ct);
    public Task<SdkResult> CloseAsync(CancellationToken ct);
}

public interface IPopups { PopupSurface Get(string popupId); }   // the id must be declared in the manifest

public sealed record ElementEvent(string ElementId, ElementEventKind Kind, string? ItemKey, JsonNode? Value,
    InvocationSource Source);

public interface ICommands
{
    // The id must be declared in the manifest. One handler per id.
    void Register(string commandId, Func<CommandContext, CancellationToken, Task<CommandResult>> handler);
    Task<SdkResult<CommandResult>> InvokeAsync(string commandId, JsonObject? args, CancellationToken ct);
}
public sealed record CommandContext(JsonObject? Args, InvocationSource Source);

public interface IFeeds
{
    IFeedPublisher Publisher(string feedId);   // declared under `feeds`
    IDisposable Subscribe(string feedId, Func<FeedUpdate, CancellationToken, Task> onUpdate); // declared under `subscribes`
}
public interface IFeedPublisher { void Publish(JsonNode snapshot); }  // latest-wins
public sealed record FeedUpdate(bool Available, JsonNode? Snapshot);

public static class View   // builder helpers producing Protocol.ViewNode; sugar, not a separate model
{
    public static ViewNode Text(string id, Prop<string> text, params string[] classes);
    public static ViewNode Icon(string id, Prop<IconRef> icon, IconSize size = IconSize.Medium);
    public static ViewNode Button(string id, string automationName, ElementAction action,
        Prop<string>? label = null, Prop<IconRef>? icon = null, Prop<bool>? active = null, params string[] classes);
    public static ViewNode Stack(string id, StackOrientation orientation, params ViewNode[] children);
    public static ViewNode List(string id, string automationName, Prop<JsonArray> items, ViewNode itemTemplate,
        StackOrientation orientation = StackOrientation.Panel, ListSelection selection = ListSelection.None);
    public static ViewNode TextInput(string id, string automationName, Prop<string> value,
        string? placeholder = null, string? navigates = null);
    // Toggle, Image, Progress, Separator and Spacer follow the same pattern.
    public static ElementAction Invoke(string commandId, JsonObject? args = null);
    public static ElementAction Event(string name);
    public static ElementAction Popup(string popupId);
}
public readonly struct Prop<T>   // a literal, or a binding
{
    public static implicit operator Prop<T>(T literal);
    public static Prop<T> State(string key);
    public static Prop<T> Item(string field);
}
public abstract record IconRef
{
    public sealed record Named(string Id) : IconRef;
    public sealed record App(string? Exe, string? Aumid) : IconRef;
    public sealed record Window(long Hwnd) : IconRef;
    public sealed record File(string PackagePath) : IconRef;
}
public enum IconSize { Small, Medium, Large }
public enum StackOrientation { Horizontal, Vertical, Panel }
public enum ListSelection { None, Single }

public readonly record struct SdkResult(SdkError? Error) { public bool Ok { get; } }
public readonly record struct SdkResult<T>(T? Value, SdkError? Error) { public bool Ok { get; } }
public sealed record SdkError(ErrorCode Code, string Detail, bool Retryable);
public sealed class InvalidViewException : Exception { public ValidationResult Result { get; } }
```

A plugin author may rely on these:
- Callbacks are serialised (I20).
- `SetState` and `SetView` are safe from any thread.
- `OnStopAsync` gets the remainder of the shutdown deadline.
- The process is killed when the deadline passes.

A plugin author must never:
- Block a callback indefinitely. The heartbeat is answered by the SDK off the callback context, but a request is still subject to its timeout.
- Write protocol frames themselves.
- Assume an instance outlives a `DisposeAsync`.

### Host built-in commands

Provider `host`. Every plugin can invoke them without declaring a dependency.

| Command | Args | Result | Notes |
|---|---|---|---|
| `host.window.activate` | `{ hwnd: number }` | none | Restores the window if it is minimised, then brings it forward |
| `host.window.minimize` | `{ hwnd: number }` | none | |
| `host.settings.open` | `{ page?: "plugins"\|"layout"\|"appearance", pluginId?: string }` | none | |
| `host.preset.apply` | `{ id: string }` | none | Writes `activePreset`, then flow 4 |
| `host.plugin.debugAttach` | `{ pluginId: string }` | `{ pipe: string, token: string }` | Stops the plugin's worker and waits for one started by a developer with these arguments |
| `host.exit` | none | none | Normal exit (flow 6) |

### First-party plugin contracts

The public names other plugins may depend on:

| Plugin id | Commands | Feeds | Popups | Contributions |
|---|---|---|---|---|
| `subzerodev.clock` | none | none | none | `clock` (panelItem, multiInstance; settings: `format: string`, `showDate: bool`) |
| `subzerodev.launcher` | `launcher.open` (default hotkey `Ctrl+Alt+Space`), `launcher.close`, `applications.launch { exe?: string, aumid?: string, args?: string }` | `applications.index` (array of `{ key, name, exe?, aumid? }`) | `search` | `launcher` (panelItem: a button that opens `search`) |
| `subzerodev.running-windows` | `windows.activate { hwnd }`, `windows.minimize { hwnd }` | `windows.running` (array of `{ key, hwnd, title, exe?, aumid?, monitorId, active, minimized }`) | none | `windows` (panelItem, multiInstance; settings: `scope: "allMonitors"\|"thisMonitor"`) |

### Host internals (Host.Core abstractions)

```csharp
namespace SubZeroDev.WindowManager.Host.Core;

public sealed record CoreError(CoreErrorCode Code, string Detail, bool Retryable);
public readonly record struct Outcome(CoreError? Error) { public bool Ok { get; } }
public readonly record struct Outcome<T>(T? Value, CoreError? Error) { public bool Ok { get; } }

// ---- configuration
public enum DocumentKind { HostSettings, Layout, Preset, PluginSettings }
public sealed record DocumentId(DocumentKind Kind, string Id);   // Id is "" for HostSettings
public sealed record DocumentState(DocumentId Id, JsonObject? Content, string? ContentHash, DocumentHealth Health, CoreError? LastError);
public enum DocumentHealth { Valid, Missing, Invalid, TooNew }

public interface IConfigStore
{
    DocumentState Get(DocumentId id);
    IReadOnlyList<DocumentId> List(DocumentKind kind);
    // edit receives the current content and returns the replacement. Fails with Conflict when the disk hash
    // differs from basedOnHash, and with DocumentReadOnly when Health is Invalid or TooNew.
    Task<Outcome<DocumentState>> WriteAsync(DocumentId id, string? basedOnHash, JsonObject content, CancellationToken ct);
    Task<Outcome> DeleteAsync(DocumentId id, string? basedOnHash, CancellationToken ct);
    event EventHandler<DocumentChangedEventArgs> Changed;   // raised off the UI thread
}
public sealed record DocumentChangedEventArgs(DocumentId Id, DocumentState State, ChangeOrigin Origin);
public enum ChangeOrigin { Host, External }

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

public interface IPluginSupervisor
{
    PluginStatus GetStatus(string pluginId);
    IReadOnlyList<PluginStatus> All { get; }
    Task<Outcome> EnableAsync(string pluginId, CancellationToken ct);    // persists `enabled: true`, then starts
    Task<Outcome> DisableAsync(string pluginId, CancellationToken ct);   // persists `enabled: false`, then stops
    Task<Outcome> RetryAsync(string pluginId, CancellationToken ct);     // Unavailable/Faulted -> Starting; resets the budget
    Task<Outcome<DebugAttachInfo>> DebugAttachAsync(string pluginId, CancellationToken ct);
    IWorkerChannel? GetChannel(string pluginId);                         // null unless Running
    event EventHandler<PluginStatus> StatusChanged;
}
public sealed record DebugAttachInfo(string Pipe, string Token);

public interface IWorkerChannel
{
    string PluginId { get; }
    int ProcessId { get; }
    Task<Outcome<TResult>> RequestAsync<TParams, TResult>(string method, TParams args, TimeSpan timeout, CancellationToken ct);
    Outcome Notify<TParams>(string method, TParams args);   // non-blocking enqueue; fails if closed
    event EventHandler<WorkerNotification> NotificationReceived;   // in channel order (I12)
}
public sealed record WorkerNotification(string Method, JsonElement Params);

// ---- commands and feeds
public sealed record CommandDescriptor(string CommandId, string Title, string Provider, bool Available, string? DefaultHotkey);
public interface ICommandRouter
{
    IReadOnlyList<CommandDescriptor> Commands { get; }
    Task<Outcome<CommandResult>> InvokeAsync(string commandId, JsonObject? args, InvocationSource source,
        string? callerPluginId, CancellationToken ct);
    Task<Outcome> SetProviderAsync(string commandId, string pluginId, CancellationToken ct);
    event EventHandler CommandsChanged;
}
public interface IFeedBroker
{
    FeedUpdateParams? Latest(string feedId);
    event EventHandler<FeedUpdateParams> Updated;
}

// ---- recovery (implemented over the Recovery module)
public interface IRecoveryJournal
{
    Task<Outcome<Guid>> BeginAsync(DesktopChange change, CancellationToken ct);   // durable before return (I6)
    Task<Outcome> EndAsync(Guid changeId, CancellationToken ct);                  // after revert
    Task<Outcome> MarkCleanExitAsync(CancellationToken ct);
}

// ---- platform abstractions, implemented by Host.Platform
public sealed record MonitorInfo(string StableId, nint Handle, RectInt Bounds, RectInt WorkArea, uint Dpi, bool IsPrimary);
public readonly record struct RectInt(int Left, int Top, int Right, int Bottom);
public interface IMonitorService
{
    IReadOnlyList<MonitorInfo> Monitors { get; }
    event EventHandler TopologyChanged;   // debounced 250 ms
}
public interface IAppBarService
{
    Task<Outcome<RectInt>> RegisterAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct); // journals first
    Task<Outcome<RectInt>> UpdateAsync(nint hwnd, string monitorId, PanelEdge edge, int thicknessPx, CancellationToken ct);
    Task<Outcome> RemoveAsync(nint hwnd, CancellationToken ct);   // reverts, then ends the journal entry
    event EventHandler<FullscreenChangedEventArgs> FullscreenChanged;
    event EventHandler ShellRestarted;    // "TaskbarCreated"
}
public sealed record FullscreenChangedEventArgs(string MonitorId, bool IsFullscreen);
public interface IForegroundService
{
    void AllowForeground(int processId);              // I18
    Task<Outcome> ActivateWindowAsync(nint hwnd, CancellationToken ct);
    Task<Outcome> MinimizeWindowAsync(nint hwnd, CancellationToken ct);
}
public interface IHotkeyService
{
    Outcome<HotkeyRegistration> Register(KeyChord chord, Action onPressed);   // fails with HotkeyInUse
}
public sealed record HotkeyRegistration(KeyChord Chord) : IDisposable;
public readonly record struct KeyChord(KeyModifiers Modifiers, uint VirtualKey)
{
    public static Outcome<KeyChord> Parse(string text);   // "Ctrl+Alt+Space": Ctrl|Alt|Shift|Win joined by '+', then a key name
}
[Flags] public enum KeyModifiers { None = 0, Alt = 1, Ctrl = 2, Shift = 4, Win = 8 }
public interface IIconService
{
    Task<Outcome<IconImage>> ExtractAsync(IconRef reference, int sizePx, CancellationToken ct);   // system icon only
}
public sealed record IconImage(int Width, int Height, ReadOnlyMemory<byte> Bgra32);
public interface IWorkerLauncher
{
    Task<Outcome<WorkerProcess>> StartAsync(WorkerStartInfo info, CancellationToken ct);   // I3
}
public sealed record WorkerStartInfo(string ExecutablePath, IReadOnlyList<string> Arguments, string WorkingDirectory);
public sealed record WorkerProcess(int ProcessId, SafeHandle ProcessHandle, Stream StandardError);
```

### Recovery module

```csharp
namespace SubZeroDev.WindowManager.Recovery;

public enum DesktopChangeKind { AppBar, WorkArea }
public sealed record DesktopChange(Guid Id, DesktopChangeKind Kind, string MonitorId, DateTimeOffset AppliedAt, JsonObject Payload);
public sealed record JournalDocument(int SchemaVersion, int HostPid, DateTimeOffset HostStartTime, bool CleanExit,
    IReadOnlyList<DesktopChange> Entries);

public static class Journal
{
    public static string PathForSession(string dataRoot, int sessionId);
    public static Outcome<JournalDocument?> Read(string path);   // null when absent
    public static Outcome Write(string path, JournalDocument doc);   // atomic, flushed
}
public static class Reverter
{
    // Newest first. Returns the entries that could not be reverted; the caller logs them and still clears the journal.
    public static IReadOnlyList<(DesktopChange Change, RecoveryError Error)> RevertAll(JournalDocument doc);
}
public sealed record RecoveryError(RecoveryErrorCode Code, string Detail);
public readonly record struct Outcome(RecoveryError? Error);
public readonly record struct Outcome<T>(T? Value, RecoveryError? Error);
```

### Theming and Surfaces

```csharp
namespace SubZeroDev.WindowManager.Host.Theming;

public sealed record AppearanceSelection(string ThemeId, PaletteSelection Palette, IReadOnlyList<string> AssetPacks);
public sealed record PaletteSelection(string? Single, string? Light, string? Dark);
public sealed record ThemeError(ThemeErrorCode Code, string File, string? Key, string Detail);
public sealed record ApplyResult(bool Applied, IReadOnlyList<ThemeError> Errors);

public interface IThemeEngine
{
    AppearanceSelection Current { get; }
    PanelBackdrop CurrentBackdrop { get; }
    Task<ApplyResult> ApplyAsync(AppearanceSelection selection, CancellationToken ct);   // I13
    Task<ApplyResult> ValidateAsync(string themeId, CancellationToken ct);              // no swap
    Task<Outcome<ImageSourceHandle>> ResolveIconAsync(IconRef reference, int sizePx, CancellationToken ct);
    event EventHandler AppearanceChanged;   // raised on the UI thread after a swap
}
public enum PanelBackdrop { None, Transparent, Mica, MicaAlt, Acrylic }
```

```csharp
namespace SubZeroDev.WindowManager.Host.Surfaces;

public interface ISurfaceManager
{
    Task ApplyLayoutAsync(string layoutId, CancellationToken ct);
    Task<Outcome> OpenPopupAsync(string pluginId, string popupId, PopupAnchor anchor, PopupTrigger trigger, CancellationToken ct);
    Task<Outcome> ClosePopupAsync(string pluginId, string popupId, PopupCloseReason reason, CancellationToken ct);
    void FocusPanel(string panelId);
}
public enum PlaceholderReason { Starting, Disabled, Incompatible, Invalid, Faulted, Unavailable, NotSupportedInMode, Missing }
```

**Template vocabulary (theme API 1).** Each element kind is rendered by a host templated control in the XAML namespace `using:SubZeroDev.WindowManager.Host.Surfaces.Controls`. A theme styles them with `Style` resources whose `ControlTemplate` binds to the listed properties.

| Resource key | Target control | Template properties | Visual state groups |
|---|---|---|---|
| `Wm.Panel` (required), `Wm.Panel.Top\|Bottom\|Left\|Right\|Float\|Desktop` | `WmPanel` | `Edge`, `Behaviour`, `Start`, `Center`, `End` (zone content) | `HiddenStates`: Shown, AutoHidden |
| `Wm.Zone` | `WmZone` | `Items` | none |
| `Wm.Contribution`, `Wm.Contribution.<pluginId>:<contributionId>` | `WmContribution` | `Content` | none |
| `Wm.Popup` | `WmPopup` | `Content` | `OpenStates`: Opening, Open, Closing |
| `Wm.Placeholder` | `WmPlaceholder` | `PluginName`, `Reason`, `Detail`, `RetryCommand` | `ReasonStates`: one state per `PlaceholderReason` |
| `Wm.Notice` | `WmNotice` | `Message` | none |
| `Wm.Element.<Kind>` | `Wm<Kind>`, for example `WmButton` | One dependency property per prop in the kind table, same name in PascalCase; icons arrive as `ImageSource` | `CommonStates`: Normal, PointerOver, Pressed, Disabled · `FocusStates` · Button and Toggle: `ActiveStates`: Inactive, Active · `AttentionStates`: Calm, Attention |
| `Wm.Element.<Kind>.<class>` | same | same | same |

Style resolution for an element with classes `[a, b]`: `Wm.Element.<Kind>.a`, then `Wm.Element.<Kind>.b`, then `Wm.Element.<Kind>`, then the host's built-in style.

Palette resources: for each token `t`, the host adds `Wm.Color.t` (`Color`) and `Wm.Brush.t` (`SolidColorBrush`). Tokens the host always defines (themes may add more): `surface`, `surfaceAlt`, `text`, `textMuted`, `accent`, `accentText`, `border`, `attention`, `warning`, `danger`.

Icon sizes: the theme sets `Wm.IconSize.Small|Medium|Large` (`x:Double`, DIPs). Defaults are 16, 20 and 32.

## Persisted schemas

**Install root** (read-only to the host): `%LOCALAPPDATA%\Programs\SubZeroDev\WindowManager\`
- `SubZeroDev.WindowManager.exe` is the guardian, and `host\` holds the host with its runtime.
- `content\{plugins,themes,palettes,assetpacks,layouts,presets}\` holds the built-in packages and documents.

**Data root**: `%LOCALAPPDATA%\SubZeroDev\WindowManager\`

| Path | Key | Constraints | Existing data |
|---|---|---|---|
| `settings.json` | singleton | `schemaVersion: 1`; `activePreset` names an existing preset, else the built-in default is used and a diagnostic is raised; `restartPolicy.maxRestarts` 0–10 (default 3); `windowSeconds` 30–3600 (default 300) | Absent on first run → defaults written |
| `layouts\<id>.json` | `id` = file stem | Panel `id` unique within the layout; instance `id` unique across all layouts; `thickness` 16–512 DIP; `zones` keys only `start`, `center`, `end` | None; built-in layouts are copied here the first time the user edits one |
| `presets\<id>.json` | `id` = file stem | `palette` is either a string or `{ light, dark }`; `assetPacks` may be empty | Same as layouts |
| `plugin-settings\<pluginId>.json` | plugin id | `{ pluginId, settingsVersion, settings }`; `settings` validates against the manifest `settingsSchema` | Migrated through `settings.migrate`; the prior file is kept once as `.v<N>.bak` |
| `plugins\<id>\plugin.json` (+ package files) | manifest `id` = folder name | See the manifest below | Install replaces the folder atomically |
| `themes\<id>\theme.json` (+ `.xaml`, assets) | `id` = folder name | `themeApi: 1`; `resources` paths stay inside the folder | none |
| `palettes\<id>.json` or `palettes\<id>\palette.json` | `id` | `colors` values match `^#[0-9A-Fa-f]{8}$` | none |
| `assetpacks\<id>\assetpack.json` (+ images) | `id` = folder name | Image paths stay inside the folder; PNG or SVG | none |
| `plugin-data\<pluginId>\` | plugin id | Opaque to the host | Kept when a plugin is removed; deleted only if the user chooses to on removal |
| `recovery\session-<sessionId>.json` | Windows session id | `JournalDocument`; written atomically | Recovered and cleared by the guardian at startup |
| `.lastgood\<relative path>` | mirrors the source document | A copy of the last version of each document that loaded valid | Overwritten on each valid load |
| `logs\host-<yyyyMMdd>.log`, `logs\plugins\<pluginId>-<yyyyMMdd>.log`, `logs\crash\<timestamp>.json` | date | 10 MiB per file, 7 days, 100 MiB in total | Pruned at startup |
| `staging\` | random | Emptied at startup | none |

**Plugin manifest (`plugin.json`, manifestVersion 1):**

```json
{
  "manifestVersion": 1,
  "id": "subzerodev.clock",
  "version": "1.0.0",
  "displayName": "Clock", "publisher": "SubZeroDev", "description": "…", "icon": "icon.png",
  "hostApi": "^1.0",
  "entry": { "path": "SubZeroDev.WindowManager.Plugins.Clock.exe", "args": [] },
  "dependencies": [ { "id": "…", "versionRange": "^1.0", "optional": false } ],
  "capabilities": [ "time.read" ],
  "modes": [ "coexistence", "replacement" ],
  "requiresExplorer": false,
  "contributions": [ { "id": "clock", "kind": "panelItem", "displayName": "Clock", "multiInstance": true, "settingsSchema": {} } ],
  "commands": [ { "id": "…", "title": "…", "argsSchema": {}, "defaultHotkey": "Ctrl+Alt+Space" } ],
  "feeds": [ { "id": "…", "schema": {} } ],
  "subscribes": [ "…" ],
  "popups": [ { "id": "…" } ],
  "settingsSchema": {}, "settingsVersion": 1
}
```

Constraints:
- `id` matches `^[a-z0-9]+(\.[a-z0-9-]+)+$` and is at most 128 characters.
- Command, feed, popup and contribution ids match `^[a-z0-9]+([.-][a-z0-9]+)*$` and are unique within their list.
- No command id starts with `host.`.
- `entry.path` exists inside the package.
- `dependencies` does not contain the plugin's own id.

**Settings schema subset** (used by `settingsSchema` and `argsSchema`):
- The root is `type: object` with `properties`.
- Each property is one of `string` (with optional `enum`, `maxLength`, or `format` of `hotkey` or `path`), `number` or `integer` (with optional `minimum` and `maximum`), `boolean`, or an `array` whose `items` are `type: string`.
- Annotation keywords: `title`, `description`, `default`.
- Any other keyword makes the manifest `Invalid`.

**Migration of existing data:** none exists; this is a new project. Every document type starts at `schemaVersion` 1 (or `manifestVersion` 1). A later version's migration is a pure function `vN → vN+1`, applied in sequence on load and written back only when the document next changes.

## Error semantics

All errors are typed codes with a detail string for logs. None is a bare exception across a module boundary. Exceptions inside a module are caught at its boundary and converted.

**Wire (JSON-RPC `error.code`):**

| Code | Name | Raised when | Retryable | Caller does |
|---|---|---|---|---|
| -32700 / -32600 | Parse / InvalidRequest | Malformed frame or message | no | Host: fault the worker. Worker (SDK): log and close. |
| -32601 | MethodNotFound | Unknown method | no | Log. Host treats a worker sending an unknown method as a protocol violation. |
| -32602 | InvalidParams | Params fail to deserialise or validate | no | Log, and surface the problem to the plugin author's log |
| 1001 | PluginUnavailable | The target plugin is not `Running`, or faulted mid-request | yes, by the user | Show a notice; no automatic retry (I19) |
| 1002 | CommandNotFound | No provider is registered for the id | no | Show a notice |
| 1003 | Timeout | Request exceeded its timeout (command 5 s, `instance.create` 5 s, `settings.migrate` 5 s, ping 10 s) | no | Notice. A ping timeout causes a fault. |
| 1004 | IncompatibleVersion | `hello` protocol version unsupported | no | Host: `Incompatible`, and the worker is killed |
| 1005 | InvalidView | `view.set` fails I11 | no | Host keeps the previous view, logs the issues to the plugin log |
| 1006 | Cancelled | The caller's token was cancelled, or shutdown began | no | Drop |
| 1007 | Undeclared | A command, feed or popup was used without a manifest declaration | no | Log; the SDK raises it locally before sending |
| 1008 | SettingsInvalid | `settings.save` fails the schema | no | Plugin gets `SdkError`; stored settings are unchanged |
| 1009 | CommandFailed | The provider's handler threw or returned an error | depends on the provider (carried in `data.retryable`) | Notice with the provider's detail |
| 1010 | Unauthorised | `hello` token mismatch | no | Connection closed |

**`CoreErrorCode` (Host.Core and Host.Platform):**

| Code | Raised when | Retryable | Caller does |
|---|---|---|---|
| `Conflict` | Config write `basedOnHash` ≠ disk | yes, after reload | Settings UI reloads and shows "changed on disk" |
| `DocumentReadOnly` | Write to an `Invalid` or `TooNew` document | no | Show the document's load error |
| `DocumentInvalid` | Content fails schema on write | no | Show the field errors |
| `IoFailure` | Disk or access failure | yes | Show "save failed"; the user re-saves |
| `PackageInvalid` | Install: manifest or metadata invalid, path escape, missing entry | no | Show the reasons |
| `PackageInUse` | Remove or upgrade of a built-in package, or one another package depends on | no | Explain; offer to disable instead |
| `NotFound` | Unknown plugin, package or document id | no | Caller bug or a stale UI; refresh |
| `InvalidState` | Lifecycle call not valid in the current `PluginState` (for example Retry on `Running`) | no | Refresh the UI |
| `DependencyMissing` / `DependencyCycle` / `HostApiMismatch` / `ModeUnsupported` | Compatibility resolution | no | Shown as `Incompatible` with the reason |
| `StartFailed` | The process could not be created, or job assignment failed | yes (supervisor backoff) | Fault path |
| `HotkeyInUse` | Hotkey registration refused | no | Leave unbound; settings shows the conflict |
| `AppBarRefused` | Shell refused app-bar registration | yes, on "taskbar created" | Fall back to `overlay` |
| `WindowGone` | `host.window.*` on a window that no longer exists | no | Return the error to the caller plugin |
| `ForegroundDenied` | Windows refused activation | no | Fall back to flashing the window's taskbar button; return an error |
| `JournalWriteFailed` | Recovery journal flush failed | yes, once | **Do not apply** the desktop change (I6); the panel falls back to `overlay`, and a diagnostic is raised |

**`ThemeErrorCode`:** `XamlParse`, `MissingRequiredKey`, `WrongTargetType`, `AssetMissing`, `PathEscape`, `ThemeApiMismatch`, `PaletteInvalid`. None is retryable. The previous appearance stays, and the errors are shown.

**`RecoveryErrorCode`:** `JournalUnreadable` (the guardian moves the file aside as `.corrupt`, logs it, and continues; nothing can be reverted), `RevertFailed` (logged in the crash record; the journal is still cleared, because the shell recomputes app-bar space when it next processes app bars, and a stale entry would otherwise retry forever).

**`ErrorCode` (Sdk):** this mirrors the wire codes 1001–1010 plus `NotConnected` (called before `initialize` or after `shutdown`). `InvalidViewException` is the one exception the SDK throws, raised synchronously from `SetView` on an invalid tree. It is a programming error in the plugin.

## Unresolved

- The **Windows App SDK release**: pinned in the foundation slice. It may change the template-property mechanics if runtime-loaded templates turn out to be limited (open question 5 in the design).
- **How transparent, shaped panel windows combine with backdrops.** `PanelBackdrop.Transparent` is declared, but how it is achieved, and whether templates may use non-rectangular hit-testing, waits on the prototype.
- **Exact dependency properties beyond the props table**: anything a theme needs that is not a view prop (for example the panel's monitor scale, or an "is first item" flag) is added after the sample themes are attempted, as theme API 1 additions, before the vocabulary is frozen.
- **Performance budgets** (startup, idle memory and CPU, interaction latency): set after the first working prototype, per the brief.
- **`applications.index` sources**: Start-menu shortcuts and the AUMIDs of installed packaged apps are the intent. The enumeration method for packaged apps without package identity on the host is to be confirmed in the launcher slice.

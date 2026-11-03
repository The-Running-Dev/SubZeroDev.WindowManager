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
| I22 | `--shell on` writes only the per-user `Winlogon\Shell` value, and only after recording the previous value (or its absence) in `shell.json`. `--shell off` restores exactly that state. Registration never writes a machine-wide or policy value. | Guardian |
| I23 | In replacement mode, a session that cannot keep a host running ends with Explorer as the shell: when the relaunch budget is spent, the guardian starts `explorer.exe` before it shows any message. | Guardian |
| I24 | Start-up programs run at most once per sign-in session, however often the host restarts. | Host.Platform (`IStartupRunner`) |

Second release (from [10-design.md](10-design.md) § Second release):

| # | Invariant | Owner |
|---|---|---|
| I25 | The window model has one writer, the host UI thread. Tracker, hook and backchannel threads only post observations; everything else reads immutable snapshots. | Host.Core (`IWindowModel`) |
| I26 | A window is identified by its window key (handle, process id and process start time). A handle reused by another process never inherits a window's desktop, group or badges. | Host.Core (`IWindowModel`) |
| I27 | Only the window-effects thread hides or shows another process's window, and it uses non-blocking calls. | Host.Platform (`IWindowEffects`) |
| I28 | A window is hidden by the shell only after a journal entry listing it was flushed (I6), and the entry stops listing it only after it was shown. | Host.Platform (`IWindowEffects`), Host.Core (journal writer) |
| I29 | At least one desktop exists, and `currentId` names one. | Host.Core (`IDesktopSet`) |
| I30 | `host.desktops` reports a new `current` only after the plan that hides and shows windows has completed. | Host.Core (`IDesktopSet`) |
| I31 | Notification titles and bodies are never written to a file, a log or a crash record by the shell. Logs carry counts, application ids and notification ids only. | `subzerodev.notifications` |
| I32 | The shell changes no trust setting, no notification-access setting and no notification switch. The setup is shown to the user with the commands to copy, and nothing in it is run on the user's behalf. | `subzerodev.notifications` |
| I33 | Only the notifications worker executable carries package identity. The sparse package declares exactly that one application. | Build check (PackTool) and `subzerodev.notifications` |
| I34 | A quick setting shows the value read back from the device after a change, never the value that was requested. | `subzerodev.quick-settings` |
| I35 | A chord is swallowed only when its command has a `Running` provider at key-down. The hook never calls into the UI thread and never waits. | Host.Platform (`WindowsKeyTracker`) |
| I36 | A DWM thumbnail registration lives no longer than the `Thumbnail` element that asked for it, and is released on unload. | Host.Surfaces, Host.Platform (`IThumbnailService`) |
| I37 | Third-party shell-extension code loads only in the `subzerodev.desktop-icons` worker, never in the host. | `subzerodev.desktop-icons` |
| I38 | No second-release test touches the user's real notifications, desktops, windows, devices or settings; every device port has a fake in the test hook. | Host.App (test hook) |

## Public surface

### Process command lines

```text
SubZeroDev.WindowManager.exe                       # guardian; normal launch
SubZeroDev.WindowManager.exe --attach <hostPid>    # guardian relaunched by a live host
SubZeroDev.WindowManager.exe --recover-only        # revert a stale journal, then exit
SubZeroDev.WindowManager.exe --first-run           # register shortcut (+ Run key if startAtSignIn), then normal launch
SubZeroDev.WindowManager.exe --uninstall           # remove shortcut and Run key, unregister the shell, then exit
SubZeroDev.WindowManager.exe --shell on|off|status # register or unregister as the user's shell, or report it, then exit
SubZeroDev.WindowManager.exe --mode replacement [--take-over]   # guardian started by Windows as the shell
SubZeroDev.WindowManager.Host.exe --guardian-pid <pid> --guardian-pipe <name> [--mode coexistence|replacement]
<plugin entry> [manifest args] --wm-pipe <name> --wm-token <base64url 32 bytes> --wm-data <dir> --wm-mode coexistence|replacement
wm-pack <buildOutputDir> --manifest <plugin.json> --out <package.zip>
```

Exit codes. Guardian: `0` normal, `2` another guardian or host running, `3` the restart budget was spent (in replacement mode, after starting Explorer), `4` the host could not be started, `5` shell registration is overridden by a policy or Shell Launcher, `6` `--mode replacement` found Explorer already the shell without `--take-over`. Host: `0` normal, `2` another host running, `10` fatal startup failure (logged). `wm-pack`: `0` ok, `1` invalid manifest, `2` entry executable missing from the output, `3` IO.

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
public enum ElementEventKind { Invoked, Toggled, TextChanged, TextSubmitted, SelectionChanged, ItemInvoked, ValueChanged }

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

public enum ElementKind { Text, Icon, Image, Button, Toggle, Stack, List, Progress, TextInput, Slider, Separator, Spacer }

public sealed record ElementAction(InvokeAction? Invoke, string? Event, string? Popup); // exactly one is set
public sealed record InvokeAction(string Command, JsonObject? Args); // Args values may be bindings

// Binding encodings inside Props and Args:
//   { "$state": "<key>" }   value of a state key of the owning surface
//   { "$item": "<field>" }  field of the current List item (inside ItemTemplate only)
// IconRef encodings:
//   { "named": "<icon id>" } | { "app": { "exe"?: "<path>", "aumid"?: "<id>" } } | { "window": <hwnd as number> }
//   | { "file": "<package-relative path>" } | { "tray": "<host.tray.icons key>" } (replacement mode)

public static class ViewValidator
{
    public static ValidationResult Validate(ViewNode root);   // enforces I11
}
public sealed record ValidationResult(bool IsValid, IReadOnlyList<ValidationIssue> Issues);
public sealed record ValidationIssue(string ElementId, string Code, string Detail);
```

Props per kind. Every kind also takes the common props `automationName: string` (required on Button, Toggle, TextInput, Slider and List), `tooltip: string`, `visible: bool`.

| Kind | Props |
|---|---|
| Text | `text: string`, `wrap: bool = false` |
| Icon | `icon: IconRef`, `size: "small"\|"medium"\|"large" = "medium"` |
| Image | `source: { "file": path } \| { "named": id }`, `fit: "contain"\|"cover" = "contain"` |
| Button | `label?: string`, `icon?: IconRef`, `active: bool = false`, `attention: bool = false`, `badge?: string`, `invokeOn: "click" \| "doubleClick" = "click"` (a double click or Enter acts, a single click only focuses) |
| Toggle | `label?: string`, `icon?: IconRef`, `checked: bool` |
| Stack | `orientation: "horizontal"\|"vertical"\|"panel" = "panel"` (`panel` follows the panel axis) |
| List | `items: array of objects, each with a unique string "key"`, `orientation: as Stack`, `selection: "none"\|"single" = "none"`, `selectedKey?: string` |
| Progress | `value: number 0..1 or null` (null means indeterminate) |
| TextInput | `value: string`, `placeholder?: string`, `navigates?: <List element id>` (Up, Down and Enter drive that list) |
| Slider | `value: number 0..1`, `step: number = 0.05`. Its `event` action fires with `{ value }` when the user releases it or presses an arrow key. |
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
    protected internal virtual Task OnPopupEventAsync(PopupSurface popup, ElementEvent e, CancellationToken ct); // a popup element's `event` action
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
    public static ViewNode WithAttention(this ViewNode button, Prop<bool> attention);
    public static ViewNode Stack(string id, StackOrientation orientation, params ViewNode[] children);
    public static ViewNode List(string id, string automationName, Prop<JsonArray> items, ViewNode itemTemplate,
        StackOrientation orientation = StackOrientation.Panel, ListSelection selection = ListSelection.None,
        Prop<string>? selectedKey = null);
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
    public sealed record Tray(string Key) : IconRef;   // a host.tray.icons entry, replacement mode
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
| `host.desktop.show` | none | none | Minimises every normal window; a second call restores the ones it minimised (Win+D) |
| `host.desktop.minimizeAll` | none | none | Minimises every normal window, with nothing remembered to restore (Win+M) |
| `host.files.open` | `{ path?: string }` | none | Starts `explorer.exe` with the path (default: This PC) as an ordinary application (Win+E) |
| `host.session.lock` | none | none | |
| `host.session.signOut` | none | none | An application may block it: `CommandFailed` with `SessionCommandRefused` |
| `host.session.restart` | none | none | Same |
| `host.session.shutdown` | none | none | Same |
| `host.session.sleep` | none | none | |
| `host.shell.restoreExplorer` | `{ unregister: bool = false }` | none | Replacement mode only: normal exit, then Explorer starts as the shell for this session; with `unregister`, also `--shell off` |
| `host.tray.invoke` | `{ key: string, action: "select"\|"contextMenu"\|"doubleClick", x: number, y: number }` | none | Replacement mode only: grants the icon's owner foreground rights (I18), then sends the click in the form its `Shell_NotifyIcon` version expects |

Host feeds, provider `host`, which any plugin may subscribe to: `host.tray.icons` (replacement mode only), an array of `{ key, uid, ownerPid, tooltip, icon: { "tray": key }, hidden, balloon?: { title, text, at } }`.

### First-party plugin contracts

The public names other plugins may depend on:

| Plugin id | Commands | Feeds | Popups | Contributions |
|---|---|---|---|---|
| `subzerodev.clock` | none | none | none | `clock` (panelItem, multiInstance; settings: `format: string`, `showDate: bool`) |
| `subzerodev.launcher` | `launcher.open { mode?: "search"\|"run" }` (default hotkey `Ctrl+Alt+Space`; `run` takes a command line, as Win+R did), `launcher.close`, `applications.launch { exe?: string, aumid?: string, args?: string }` | `applications.index` (array of `{ key, name, exe?, aumid?, args? }`; `key` is the shortcut path, or `aumid:` and the AUMID) | `search` | `launcher` (panelItem: a button that opens `search`) |
| `subzerodev.running-windows` | `windows.activate { hwnd }`, `windows.minimize { hwnd }` | `windows.running` (array of `{ key, hwnd, title, exe?, aumid?, monitorId, active, minimized }`) | none | `windows` (panelItem, multiInstance; settings: `scope: "allMonitors"\|"thisMonitor"`) |
| `subzerodev.tray` | none | none (subscribes to `host.tray.icons`) | none | `tray` (panelItem: the notification area). `modes: ["replacement"]` |
| `subzerodev.desktop-icons` | `desktop.open { path: string }` (`ShellExecute` with `open`) | none | none | `icons` (gadget on the desktop layer: the user's and the public Desktop folders) |
| `subzerodev.session` | none | none | `menu` | `power` (panelItem: a button opening lock, sign out, restart, shut down and sleep). `modes: ["replacement"]` |
| `subzerodev.indicators` | none | none | `volume`, `network`, `battery` | `volume`, `network`, `battery` (panelItem each; `battery` is hidden on machines without one) |

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
    event EventHandler<IWorkerChannel> ChannelOpened;                    // after the handshake, before the first read
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

// ---- replacement mode only (flow 8); in coexistence mode these are not created
public interface IShellRoleService
{
    Outcome Claim(nint desktopHwnd, nint hookHwnd);   // shell window, shell hook, task-manager window; ShellRoleTaken when another shell holds them
    event EventHandler<ShellWindowEventArgs> WindowEvent;
    void SignalShellReady();                           // ShellDesktopSwitchEvent, then WM_USER to the desktop window
}
public enum ShellWindowEventKind { Created, Destroyed, Activated, Flashing, Redraw }
public sealed record ShellWindowEventArgs(ShellWindowEventKind Kind, nint Hwnd);
public interface ITrayHost
{
    IReadOnlyList<TrayIcon> Icons { get; }
    event EventHandler IconsChanged;
    Task<Outcome> InvokeAsync(string key, TrayAction action, int x, int y, CancellationToken ct);
}
public sealed record TrayIcon(string Key, nint OwnerHwnd, uint Uid, int OwnerPid, string Tooltip, bool Hidden, TrayBalloon? Balloon);
public sealed record TrayBalloon(string Title, string Text, DateTimeOffset At);
public enum TrayAction { Select, ContextMenu, DoubleClick }
public interface IStartupRunner
{
    Task<StartupReport> RunOnceForSessionAsync(CancellationToken ct);   // I24; LiteStep's order, policy respected
}
public sealed record StartupReport(bool Ran, IReadOnlyList<StartupItem> Items);
public sealed record StartupItem(string Source, string Command, CoreError? Error);
public interface IWindowsKeyHook : IDisposable
{
    event EventHandler<KeyChord> Pressed;   // VirtualKey 0 with Modifiers Win: a lone Windows-key press
}
public interface ISessionService
{
    Outcome Lock();
    Outcome SignOut();
    Outcome Restart();
    Outcome Shutdown();
    Outcome Sleep();
}
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
    void FocusPanel(string panelId);   // activates the panel's window, then keyboard focus on its first Button or Toggle
}
// An opened popup puts keyboard focus on its first TextInput, else its first visible, enabled interactive element.
// A later view update moves focus only when it adds the first TextInput or the focused element went away.
public interface IElementActions   // where a Button or Toggle sends what the user did
{
    Task<Outcome<CommandResult>> InvokeCommandAsync(string commandId, JsonObject? args, InvocationSource source);
    Task<Outcome> SendEventAsync(Guid instanceId, string elementId, ElementEventKind kind, JsonNode? value, InvocationSource source);
}
public sealed record ElementNotice(string ElementId, CoreErrorCode Code, string Message);   // shown in Wm.Notice for 4 s; never resent (I19)
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

### Second release (Host.Core, Host.Platform, Host.Surfaces, first-party plugins)

**Protocol and host API 1.1 (additive).** `ProtocolVersion` stays 1; `HostApiVersion` becomes 1.1.0. A 1.0 plugin runs unchanged. A plugin that uses anything below declares `hostApi: "^1.1"`.
- `ElementKind` gains `Thumbnail`: props `window: number` (a window handle from the `host.windows` feed; a binding is allowed) and `size: "small"|"medium"|"large" = "medium"`. The theme sets `Wm.Thumbnail.Width|Height.Small|Medium|Large` (`x:Double`, DIPs; defaults 160×90, 240×135 and 320×180), because I11 allows no literal pixel sizes. A `Thumbnail` whose window is gone, hidden or minimised draws an icon and title card.
- Every element gains the common prop `hoverPopup?: { popup: string, delayMs: number = 400 }`: the host opens the named popup of the same plugin at the element, without taking focus, and closes it when the pointer leaves both.
- The manifest `popups` entries gain `placement: "anchored" | "toast" | "overlay" = "anchored"`. `toast` is a non-activating stack on a work-area corner (the corner is a host setting `toastCorner`, default bottom right). `overlay` is a full-monitor surface that takes keyboard focus and closes on Esc or a click outside.
- `command.invoke` from the host gains an optional `trigger: "pointer" | "keyboard" | "chord"`, so the receiving plugin knows whether to open for keyboard navigation.
- Feed items of `host.windows` may be referenced by `Thumbnail.window` and `IconRef { "window": hwnd }`.

**Host built-in commands (replacement mode only unless stated; `NotInReplacementMode` otherwise).**

| Command | Args | Result | Notes |
|---|---|---|---|
| `host.desktops.switch` | `{ id?: string, direction?: "previous"\|"next" }` | none | Exactly one of the two. `previous` and `next` wrap. Flow 10 |
| `host.desktops.create` | `{ name?: string, switchTo: bool = false }` | `{ id: string }` | |
| `host.desktops.remove` | `{ id: string }` | none | Its windows move to the previous desktop. `LastDesktop` if it is the only one |
| `host.desktops.rename` | `{ id: string, name: string }` | none | |
| `host.window.moveToDesktop` | `{ hwnd: number, id: string \| "new" }` | `{ id: string }` | `WindowGone`, `DesktopNotFound`. Flow 11 |
| `host.window.pinToAllDesktops` | `{ hwnd: number, pinned: bool }` | none | |
| `host.window.close` | `{ hwnd: number }` | none | Posts a close request; never terminates the process |
| `host.notifications.setDoNotDisturb` | `{ on: bool }` | none | The shell's own switch; Windows' setting is not changed |

The `host.window.activate` and `host.window.minimize` commands of the first release also work for a window on another desktop: `activate` switches to its desktop first (flow 10), then activates it.

**Chords** (`settings.json` `windowsKeys`, chord → command id; defaults below; a chord naming a command with no `Running` provider passes through, I35):

| Chord | Command | Provider |
|---|---|---|
| Win+A | `quicksettings.open` | `subzerodev.quick-settings` |
| Win+N | `notifications.openCentre` | `subzerodev.notifications` |
| Win+Tab | `desktops.openOverview` | `subzerodev.desktops` |
| Win+X | `powermenu.open` | `subzerodev.power-menu` |
| Win+Ctrl+Left / Right | `host.desktops.switch { direction }` | host |
| Win+Ctrl+D | `host.desktops.create { switchTo: true }` | host |
| Win+Ctrl+F4 | `host.desktops.remove { id: current }` | host |
| Alt+Tab, Alt+Shift+Tab | `switcher.step { backwards }`, then `switcher.commit` on Alt release, `switcher.cancel` on Esc | `subzerodev.switcher` |

**Host feeds (provider `host`, any plugin may subscribe).**
- `host.windows`: array of `{ key, hwnd, processId, title, exe?, aumid?, groupKey, monitorId, desktopId, pinnedToAll, active, minimized, attention, progress: { state: "none"\|"indeterminate"\|"normal"\|"error"\|"paused", value?: number }, overlay?: { icon: IconRef, description?: string } }`. `key` is the window key (I26) rendered as a string. Every managed window on every desktop appears, including hidden ones.
- `host.desktops`: `{ current: string, desktops: [ { id, name?, windowCount, hasAttention } ] }`.

**First-party plugin contracts (second release).**

| Plugin id | Commands | Feeds | Popups | Contributions |
|---|---|---|---|---|
| `subzerodev.running-windows` (changed) | the first release's two, plus `windows.openJumpList { groupKey, x, y }` | `windows.running` (unchanged shape, current desktop only) | `preview` (placement `anchored`, the hover popup with its `Thumbnail` per window), `jumplist` | `windows` (settings gain `grouping: "always"\|"whenFull"\|"never" = "always"`, `labels: bool`) |
| `subzerodev.notifications` | `notifications.openCentre`, `notifications.dismiss { id }`, `notifications.clear { appId? }`, `notifications.activate { id }` | `notifications.history` (array of `{ id, appId, appName, title, body, at, read, silent }`), `notifications.status` (`{ identity: "ok"\|"none", access: "allowed"\|"denied"\|"unspecified", switchedOn: bool, unread: number, doNotDisturb: bool }`) | `toast` (placement `toast`), `centre` (placement `anchored`), `setup` | `bell` (panelItem: a button with the unread count; settings: `popupSeconds: integer 2–30 = 6`, `maxVisible: integer 1–5 = 3`, `popups: bool = true`). `modes: ["replacement"]` |
| `subzerodev.desktops` | `desktops.openOverview` | none (subscribes to `host.windows`, `host.desktops`) | `overview` (placement `overlay`: one tile per desktop, thumbnails of its windows, drag to re-file, "new desktop") | `desktops` (panelItem: the current desktop's name, click for the overview). `modes: ["replacement"]` |
| `subzerodev.switcher` | `switcher.step { backwards: bool }`, `switcher.commit`, `switcher.cancel` | none | `switcher` (placement `overlay`) | none. `modes: ["replacement"]` |
| `subzerodev.quick-settings` | `quicksettings.open`, `quicksettings.set { id: string, value: bool \| number }` | `quicksettings.state` (array of `{ id, kind: "toggle"\|"level", value, available, reason? }`; ids `wifi`, `bluetooth`, `airplane`, `brightness`, `doNotDisturb`) | `panel` | `quicksettings` (panelItem: a button opening `panel`). `modes: ["replacement"]` |
| `subzerodev.power-menu` | `powermenu.open` | none | `menu` (the WinX entries, then a built-in set when the folders are unreadable) | none. `modes: ["replacement"]` |
| `subzerodev.desktop-icons` (changed) | `desktop.open` (unchanged), `desktop.contextMenu { path?: string, x: number, y: number }` | none | `rename` (a TextInput) | `icons` (unchanged contribution) |

**Host internals (second release).** Declarations are signatures only.

```csharp
namespace SubZeroDev.WindowManager.Host.Core;

public readonly record struct WindowKey(nint Handle, int ProcessId, long ProcessStartTicks);

public enum ProgressState { None, Indeterminate, Normal, Error, Paused }
public sealed record WindowProgress(ProgressState State, double? Value);
public sealed record WindowOverlay(IconRef Icon, string? Description);

public sealed record ManagedWindow(
    WindowKey Key, string Title, string? ExePath, string? Aumid, string GroupKey, string MonitorId,
    Guid DesktopId, bool PinnedToAll, bool Active, bool Minimised, bool Attention,
    WindowProgress Progress, WindowOverlay? Overlay);

public sealed record WindowSnapshot(long Revision, IReadOnlyList<ManagedWindow> Windows);

public interface IWindowModel
{
    WindowSnapshot Current { get; }
    event EventHandler<WindowSnapshot> Changed;             // raised on the UI thread, at most once per frame
    void Observe(WindowObservation observation);           // callable from any thread; posts to the UI thread (I25)
    void SetProgress(WindowKey key, WindowProgress progress);
    void SetOverlay(WindowKey key, WindowOverlay? overlay);
}

public abstract record WindowObservation(WindowKey Key);   // Created, Shown, Hidden, Destroyed, TitleChanged, Foreground, Minimised, Restored

public sealed record DesktopInfo(Guid Id, string? Name);
public sealed record SwitchPlan(Guid From, Guid To, IReadOnlyList<WindowKey> Hide, IReadOnlyList<WindowKey> Show);

public interface IDesktopSet
{
    IReadOnlyList<DesktopInfo> Desktops { get; }
    Guid CurrentId { get; }
    event EventHandler Changed;                             // UI thread
    Outcome<Guid> Create(string? name);
    Outcome Remove(Guid id);
    Outcome Rename(Guid id, string name);
    Outcome<SwitchPlan> PlanSwitch(Guid target);           // does not change anything
    Task<Outcome> SwitchAsync(Guid target, CancellationToken ct);       // journal, effects, then CurrentId (I28, I30)
    Task<Outcome> MoveAsync(WindowKey window, Guid target, CancellationToken ct);
    Outcome PinToAll(WindowKey window, bool pinned);
}

namespace SubZeroDev.WindowManager.Host.Platform;

public interface IWindowEffects                             // the effects thread (I27)
{
    // Journals the hidden set first, then applies the plan. Windows that cannot be moved are returned in Stuck.
    Task<Outcome<EffectsResult>> ApplyAsync(SwitchPlan plan, CancellationToken ct);
    Task<Outcome> ShowAllAsync(CancellationToken ct);       // used at exit and by recovery paths
}
public sealed record EffectsResult(IReadOnlyList<WindowKey> Stuck);

public interface IWindowTracker                             // WinEvent hooks, 2 s rescan, shell-hook window
{
    void Start(Action<WindowObservation> sink);
    void Stop();
}

public interface ITaskbarBackchannel                        // the host's Shell_TrayWnd; replacement mode only
{
    void Start(Action<WindowKey, WindowProgress> progress, Action<WindowKey, WindowOverlay?> overlay);
    void BroadcastButtonCreated();                          // TaskbarButtonCreated, once the tray window exists
    void Stop();
}

public interface IThumbnailService                          // I36
{
    Outcome<IThumbnailRegistration> Register(WindowKey source, nint destinationWindow);
}
public interface IThumbnailRegistration : IDisposable
{
    void SetBounds(RectInt destination, bool visible);
    bool SourceAvailable { get; }
}
```

**Plugin-side (workers).** The notifications worker uses an `INotificationHistory` port (`ReadAsync`, `Changed`, `DismissAsync`, `ClearAsync`, `ActivateAsync`), and the quick-settings worker one port per device (`IRadioControl`, `IBrightnessControl`). Each has a real implementation and a fake in the plugin's test project; the plugin never calls Windows from a code path that has no port (I38).

## Persisted schemas

**Install root** (read-only to the host): `%LOCALAPPDATA%\Programs\SubZeroDev\WindowManager\`
- `SubZeroDev.WindowManager.exe` is the guardian, and `host\` holds the host with its runtime.
- `content\{plugins,themes,palettes,assetpacks,layouts,presets}\` holds the built-in packages and documents.

**Data root**: `%LOCALAPPDATA%\SubZeroDev\WindowManager\`

| Path | Key | Constraints | Existing data |
|---|---|---|---|
| `settings.json` | singleton | `schemaVersion: 1`; `activePreset` names an existing preset, else the built-in default is used and a diagnostic is raised; `restartPolicy.maxRestarts` 0–10 (default 3); `windowSeconds` 30–3600 (default 300) | Absent on first run → defaults written |
| `layouts\<id>.json` | `id` = file stem | Panel `id` unique within the layout; instance `id` unique across all layouts; `thickness` 16–512 DIP; `zones` keys only `start`, `center`, `end` | None; built-in layouts are copied here the first time the user edits one. In replacement mode a preset naming the built-in `subzerodev.default` is shown with the built-in `subzerodev.shell` (launcher, running windows, tray, indicators, clock, power), unless `layouts\subzerodev.default.json` exists here |
| `presets\<id>.json` | `id` = file stem | `palette` is either a string or `{ light, dark }`; `assetPacks` may be empty; `wallpaper` (optional) is, from `schemaVersion` 2, `{ default?: W, monitors?: { <monitorId>: W } }` with `W` = `{ image: <asset-pack image id>, fit: "fill"\|"fit"\|"stretch"\|"center" }` (version 1 was `W` itself) | Same as layouts; the v1 → v2 migration wraps the old `W` as `default`. A preset of either version loads. |
| `plugin-settings\<pluginId>.json` | plugin id | `{ pluginId, settingsVersion, settings }`; `settings` validates against the manifest `settingsSchema` | Migrated through `settings.migrate`; the prior file is kept once as `.v<N>.bak` |
| `plugins\<id>\plugin.json` (+ package files) | manifest `id` = folder name | See the manifest below | Install replaces the folder atomically |
| `themes\<id>\theme.json` (+ `.xaml`, assets) | `id` = folder name | `themeApi: 1`; `resources` paths stay inside the folder | none |
| `palettes\<id>.json` or `palettes\<id>\palette.json` | `id` | `colors` values match `^#[0-9A-Fa-f]{8}$` | none |
| `assetpacks\<id>\assetpack.json` (+ images) | `id` = folder name | Image paths stay inside the folder; PNG or SVG | none |
| `plugin-data\<pluginId>\` | plugin id | Opaque to the host | Kept when a plugin is removed; deleted only if the user chooses to on removal |
| `shell.json` | singleton | `{ schemaVersion: 1, registered, previous: { present, value? }, registeredAt? }`; written only by the guardian (I22) | Absent until `--shell on` |
| `recovery\session-<sessionId>.json` | Windows session id | `JournalDocument`; written atomically | Recovered and cleared by the guardian at startup |
| `.lastgood\<relative path>` | mirrors the source document | A copy of the last version of each document that loaded valid | Overwritten on each valid load |
| `logs\host-<yyyyMMdd>.log`, `logs\plugins\<pluginId>-<yyyyMMdd>.log`, `logs\crash\<timestamp>.json` | date | 10 MiB per file, 7 days, 100 MiB in total | Pruned at startup |
| `staging\` | random | Emptied at startup | none |
| `desktops.json` | singleton | `{ schemaVersion: 1, desktops: [ { id: guid, name?: string } ], lastCurrent: guid }`; at least one desktop (I29); `lastCurrent` names one, else the first is used; window membership is never persisted | Absent → one desktop "Desktop 1". Invalid → the same, with the file untouched and a diagnostic |

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

**Second-release additions:**
- **Recovery journal** (`recovery\session-<sessionId>.json`): a third entry kind, `hiddenWindows`: `{ kind: "hiddenWindows", windows: [ { hwnd, processId, processStartTicks } ] }`. There is at most one such entry; it is replaced whole on each switch (I28). The guardian shows each listed window whose process id and start time still match, then clears the entry. It does not change `schemaVersion`: the journal's entry kinds are already open-ended, and an older guardian ignores a kind it does not know (a mixed install cannot happen, because the guardian and host ship together).
- **`settings.json`**: optional `windowsKeys` (object: chord → command id; chords match `^(Win|Ctrl|Alt|Shift)(\+(Win|Ctrl|Alt|Shift))*\+[A-Za-z0-9]+$` or `Alt+Tab`/`Alt+Shift+Tab`; an unknown chord, or a command id that does not resolve, is ignored with a diagnostic) and `toastCorner` (`"topLeft"|"topRight"|"bottomLeft"|"bottomRight"`, default `bottomRight`). `schemaVersion` stays 1 because both keys are optional.
- **`plugin-data\subzerodev.notifications\state.json`**: `{ lastSeen: string (ISO 8601 instant), doNotDisturb: bool }`. No notification content (I31).
- **Plugin manifest**: popups entries accept `placement` (`anchored`, `toast`, `overlay`). `manifestVersion` stays 1. An unknown `placement` makes the manifest `Invalid`.
- **Sparse package** (install root, `content\plugins\subzerodev.notifications\package\`): the package manifest, the logo, the signed package file and the public `.cer`. The manifest declares one application, with `userNotificationListener`, `runFullTrust`, and the COM activator for toast activation (I33). Registered per user by the script shipped beside it; unregistered by `--uninstall`.
- **`presets` v1 → v2** is described in the presets row above. Built-in presets are shipped at v2.

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
| `ShellRoleTaken` | Another process holds the shell window | no | Guardian: exit 6 unless `--take-over`. Host: run without the shell roles and log it |
| `ShellOverridden` | `--shell on` found a policy or Shell Launcher overriding the per-user value | no | Report it; change nothing |
| `SessionCommandRefused` | Lock, sign out, restart, shut down or sleep was refused | no | Notice at the element |
| `NotInReplacementMode` | A replacement-only command in coexistence mode | no | Return the error to the caller |
| `JournalWriteFailed` | Recovery journal flush failed | yes, once | **Do not apply** the desktop change (I6, I28); the panel falls back to `overlay`; a desktop switch does not happen, and a diagnostic is raised |
| `DesktopNotFound` | A desktop command names an id that does not exist | no | Return the error to the caller; a stale UI refreshes |
| `LastDesktop` | `host.desktops.remove` on the only desktop | no | Notice at the element |
| `WindowNotMovable` | The effects thread could not hide a window (hung, elevated, or refused) | no | Logged once per window; the window stays visible on every desktop and is marked stuck in the model |
| `SwitchSuperseded` | A switch was replaced by a newer request before it started | no | Drop; the newer request reports the outcome |
| `ThumbnailUnavailable` | A thumbnail could not be registered (source gone, hidden, minimised, or refused) | no | The element draws its icon and title card |

**Notifications and quick settings (inside the plugins; reported over the wire as 1009 `CommandFailed` with `data.code` set to the name below):**

| Code | Plugin | Raised when | Retryable | Caller does |
|---|---|---|---|---|
| `IdentityMissing` | notifications | The worker has no package identity (not registered, certificate not trusted, or started without it) | no, until the setup is done | The plugin shows the Set up page |
| `AccessDenied` | notifications | Notification access is `Denied` or `Unspecified` | no, until the user allows it | Set up page |
| `NotificationsOff` | notifications | The account's notifications switch is off (an empty history while it is) | no, until the user switches it on | Set up page |
| `HistoryReadFailed` | notifications | A read of the history failed | yes (next event or the 60 s resync) | Keep the last published history; log the count only (I31) |
| `ActivationFailed` | notifications | The source application could not be started | no | Notice in the centre |
| `DeviceAbsent` | quick-settings | No such device on this machine (no radio, no built-in panel, a monitor without DDC/CI) | no | The tile is hidden |
| `DeviceRefused` | quick-settings | The device exists and refused the change | yes | The tile is disabled with `reason`; the real value is shown (I34) |

**`ThemeErrorCode`:** `XamlParse`, `MissingRequiredKey`, `WrongTargetType`, `AssetMissing`, `PathEscape`, `ThemeApiMismatch`, `PaletteInvalid`. None is retryable. The previous appearance stays, and the errors are shown.

**`RecoveryErrorCode`:** `JournalUnreadable` (the guardian moves the file aside as `.corrupt`, logs it, and continues; nothing can be reverted), `RevertFailed` (logged in the crash record; the journal is still cleared, because the shell recomputes app-bar space when it next processes app bars, and a stale entry would otherwise retry forever).

**`ErrorCode` (Sdk):** this mirrors the wire codes 1001–1010 plus `NotConnected` (called before `initialize` or after `shutdown`). `InvalidViewException` is the one exception the SDK throws, raised synchronously from `SetView` on an invalid tree. It is a programming error in the plugin.

## Unresolved

- **Whether templates may use non-rectangular hit-testing.** A translucent panel over an acrylic backdrop works (decision "S1: Pin Windows App SDK 1.8.260317003"); hit-testing that follows a template's shape is not settled.
- **Exact dependency properties beyond the props table**: anything a theme needs that is not a view prop (for example the panel's monitor scale, or an "is first item" flag) is added after the sample themes are attempted, as theme API 1 additions, before the vocabulary is frozen.
- **The per-user `Winlogon\Shell` value on build 26200** (design open question 6): to be confirmed on a real sign-in (S17.7); the fallback is the per-user Custom User Interface value.
- **Explorer-hosted features without Explorer** (design open question 7): the expected state of each is recorded in `docs/replacement-mode.md` (S23); a week of daily use confirms it (S23.1).

Second release (the design's open questions 1–12 are the full list; these are the ones that leave a signature undetermined):

- **How the effects thread hides a window.** `IWindowEffects` fixes the port. Whether hide-and-show alone is enough, or cloaking from a shell-role process is possible (design question 8), decides if `Thumbnail` can show another desktop's windows live.
- **The progress and overlay message format** received on the host's `Shell_TrayWnd` (design question 9). `ITaskbarBackchannel` fixes the port; the decoding is set by the taskbar slice.
- **The jump-list reading API** (design question 10): which source gives destinations without Explorer. `windows.openJumpList` and the `jumplist` popup are fixed; the feed behind them is not.
- **The quick-settings device ports** (design question 11): `IRadioControl` and `IBrightnessControl` are named, not declared.
- **Whether notification actions beyond "open the application" exist** (design question 1). `notifications.activate { id }` is the only action until then.
- **The WinX folder reading** for `subzerodev.power-menu`: the entry shape (`{ title, target, args?, icon? }`) is the intended one, the folder enumeration is the slice's.

Settled since the design: the Windows App SDK release is 1.8.260317003 (decision "S1: Pin Windows App SDK 1.8.260317003"), and the performance budgets are in `docs/budgets.md` (decisions "S16: performance budgets" and "S23: replacement mode qualified for daily use").

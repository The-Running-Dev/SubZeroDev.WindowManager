# Design — SubZeroDev.WindowManager

Built from [00-brief.md](00-brief.md). Approach approved 2026-11-01: **declarative contributions rendered by the host, every plugin in its own worker process, and a separate guardian process that undoes desktop changes if the host dies.**

Premises confirmed before designing:
1. Every plugin, the first-party clock, launcher and running-windows plugins included, runs in its own process. The host never loads plugin code. Custom in-process WinUI UI is deferred past the first release.
2. Each panel's layout chooses whether it reserves screen space like the taskbar, floats over windows, auto-hides, or sits on the desktop beneath windows.
3. Themes, palettes, layouts, asset packs and presets are data, with no executable code. They are hand-editable files that the settings UI also writes.
4. Plugins are trusted. Process isolation guards against crashes and hangs, not malice. There is no sandbox, and capability declarations are labels. Install is per-user and needs no administrator rights.

## The shape in one paragraph

Three kinds of process. The **guardian** is the small entry point the user launches. It starts the **host** and watches it. The **host** is the WinUI 3 application that owns every window on screen: panels, gadgets, popups, the settings window. It also owns configuration, the theme, and the plugin lifecycle. Each enabled plugin runs as a **worker**: a headless process built against the plugin SDK, connected to the host over its own named pipe. A worker never draws. It sends the host a *view*, a tree of standard building blocks (text, icon, button, list and the rest) whose values bind to *state* the worker keeps updating. The host draws each block with a template from the active theme, so changing theme, palette or icon pack restyles every plugin without the plugin taking part. Before the host changes the desktop (reserving screen space), it writes the change to a recovery journal. If the host dies, the guardian reads the journal, undoes the changes, and relaunches it.

## Data model

Identity conventions: plugins, themes, palettes, asset packs, layouts and presets have a **package id**. It is reverse-DNS, lower case, `[a-z0-9.-]`, at most 128 characters, and unique within its kind. Contribution instances, panels and recovery entries have a **GUID**. Versions are SemVer 2.0.

### Persisted: package content (read-only to the host, written by the author)

**Plugin manifest.** One per plugin package, at the package root.
- `id`: package id. `version`: SemVer. `displayName`, `publisher`, `description`: string. `icon`: relative path, optional.
- `hostApi`: SemVer range of the host API the plugin was built against (for example `^1.0`).
- `entry`: the worker executable's relative path, plus optional fixed arguments.
- `dependencies`: list of `{ id, versionRange }`. Only needed when a plugin consumes another plugin's commands or feeds *by contract*. A soft dependency (`optional: true`) does not block start.
- `capabilities`: list of free-text labels such as `windows.enumerate` or `process.launch`. Shown to the user, never enforced.
- `modes`: subset of `coexistence`, `replacement`. `requiresExplorer`: bool.
- `contributions`: list of `{ id, kind, displayName, settingsSchema?, multiInstance }`. `kind` is `panelItem` or `gadget`.
- `commands`: list of `{ id, title, argsSchema?, defaultHotkey? }`.
- `feeds`: list of `{ id, schema? }`, the shared data providers this plugin publishes.
- `popups`: list of `{ id }`, the popup views this plugin may open (a launcher's search popup). A popup belongs to the plugin, not to an instance, so a hotkey can open it with no panel item placed.
- `subscribes`: list of feed ids this plugin consumes.
- `settingsSchema`: plugin-level settings (JSON Schema subset, below). `settingsVersion`: int, the version of the plugin's settings shape.

**Theme.** A theme package holds a metadata file and one or more XAML resource dictionaries.
- `id`, `version`, `displayName`, `author`. `themeApi`: int, the version of the host's template vocabulary the theme targets.
- `tokens`: the palette tokens the theme uses, each with a default colour. This lets a theme stand alone.
- `defaultPalette`, `defaultAssetPack`: ids, optional. Bundled ones may sit inside the theme package.
- `panelBackdrop`: `none` | `transparent` | `mica` | `micaAlt` | `acrylic`. The material behind panel windows.
- `resources`: the dictionaries, in merge order. They define templates and styles under host-defined keys (see the template vocabulary in [20-contract.md](20-contract.md)). Visual states and transitions are expressed in the XAML itself.

**Palette.** `id`, `version`, `displayName`, `themeApi`, and `colors`: map of token → `#AARRGGBB`. A token the active theme declares but the palette lacks falls back to the theme's default.

**Asset pack.** `id`, `version`, `displayName`.
- `icons`: map of named icon id (such as `action.search` or `app.launcher`) → image path.
- `appIcons`: list of `{ match: { exe? | aumid? }, image }`, overrides for application icons.
- `wallpapers`: list of image paths. Carried but unused in the first release.

Images are PNG or SVG. Paths are relative to the package; a path that escapes the package root is rejected.

### Persisted: user configuration (the host reads and writes, the user may hand-edit)

**Host settings.** One per user.
- `schemaVersion`: int. `activePreset`: preset id.
- `plugins`: map of plugin id → `{ enabled: bool }`. A plugin absent from the map is enabled when built in, disabled when user-installed.
- `commandProviders`: map of command id → plugin id. Records the user's choice when two plugins declare the same command.
- `hotkeys`: map of command id → key chord or `null` (unbound). Overrides the manifest defaults.
- `startAtSignIn`: bool. `restartPolicy`: `{ maxRestarts: int, windowSeconds: int }`, which applies to both workers and the host.

**Preset.** `schemaVersion`, `id`, `displayName`, `layout`: layout id, `theme`: theme id, `palette`: palette id or `{ light, dark }` following the Windows app mode, `assetPacks`: ordered list of asset pack ids (first match wins).

**Layout.** `schemaVersion`, `id`, `displayName`, `panels`: list of Panel.

**Panel.** One panel is one host window per matching monitor.
- `id`: GUID.
- `monitor`: `primary` | `all` | `{ device: <stable monitor id> }`.
- `placement`: `{ edge: top|bottom|left|right, length?: DIP or "fill", align?: start|center|end }` or `{ float: { x, y, width, height } }` in DIPs relative to the monitor's work area.
- `thickness`: DIPs.
- `behaviour`: `reserve` | `overlay` | `autoHide` | `desktop`. `reserve` holds screen space the way the taskbar does; `overlay` floats above windows; `autoHide` collapses to a sliver at its edge until pointed at; `desktop` sits beneath all windows (gadgets).
- `zones`: `{ start, center, end }`. Each zone is an ordered list of ContributionInstance.

**ContributionInstance.** `id`: GUID, `plugin`: plugin id, `contribution`: contribution id, `settings`: JSON object validated against the contribution's `settingsSchema`, `settingsVersion`: int.

**Plugin settings.** One per plugin that has settings. `pluginId`, `settingsVersion`, `settings`: JSON object.

**Plugin data directory.** One private folder per plugin. The host creates it and passes its path to the worker. It holds whatever the plugin keeps, such as the launcher's recent-items list. The host never reads it.

**Recovery journal.** One per user session.
- `hostPid`, `hostStartTime`, `cleanExit`: bool.
- `entries`: list of DesktopChange `{ id: GUID, kind, monitor, appliedAt, payload }`. `kind` is `appBar` (payload: window handle, edge, rectangle) or `workArea` (payload: the original work area rectangle). The second exists for replacement mode and is not applied in the first release.

**Diagnostics.** A rolling host log, one rolling log per plugin (worker log calls plus captured stderr), and a crash record for each guardian recovery. Retention is bounded by size.

Every configuration file has a `schemaVersion`. The host migrates older versions forward when it loads them. It refuses to write a file whose version is newer than it understands.

### In memory only (host)

- **PluginRecord**: parsed manifest, source (`builtIn` | `user`), install path, validation result, and compatibility result. Derived at discovery.
- **PluginRuntime**: plugin id, state (see *Plugin lifecycle* below), process id, channel, a fault history of timestamps inside the restart window, and the last fault reason.
- **InstanceView**: instance id, the current view tree, view revision (int, worker-assigned and increasing), the state map, and display status (`live` | `placeholder(reason)`).
- **CommandTable**: command id → provider (`host` or plugin id), derived from the manifests of enabled, running-or-startable plugins plus `commandProviders`.
- **FeedTable**: feed id → publisher plugin id, latest snapshot, and subscribers.
- **MonitorTopology**: list of `{ stable id, handle, bounds, work area, DPI, primary }`, refreshed when the display configuration changes.
- **ActiveAppearance**: resolved theme, palette and asset-pack chain, plus the built resource dictionary.

### Plugin lifecycle (host-side state per plugin)

`Discovered` → one of `Invalid` (bad manifest), `Incompatible` (hostApi, mode or dependency), `Disabled`, or `Starting`.

`Starting` → `Running` once the handshake succeeds.

`Running` → `Stopping` → `Stopped` on disable, removal or exit.

`Running` or `Starting` → `Faulted` when the process exits, the pipe breaks, a heartbeat or handshake times out, or the worker violates the protocol. `Faulted` → `Starting` after backoff (1 s, 5 s, 30 s), or → `Unavailable` once the restart budget is spent. `Unavailable` → `Starting` only when the user retries.

Instance display status follows the plugin state. `Running` shows the live view, or `placeholder(starting)` until the first view arrives. Every other state shows the matching placeholder.

## Module boundaries

Dependency direction runs left to right. A module may depend only on modules to its left:

```
Protocol ─► Sdk ─► (plugins: Clock, Launcher, RunningWindows, samples)
Protocol ─► PackTool
Recovery ─► Guardian
Protocol, Recovery ─► Host.Core ─► Host.Platform ─► Host.Theming ─► Host.Surfaces ─► Host.Settings ─► Host.App
```

Host.Platform depends on Host.Core only for the abstractions Core defines. Core never references Platform. Each arrow points one way, and no module appears on both sides of another, so the graph is acyclic. Plugins reference **only** Sdk, and Sdk references only Protocol. A build check enforces that no plugin project references a host project.

| Module | Owns | Depends on | Exposes |
|---|---|---|---|
| **Protocol** | The wire contract: message types, the view-tree model, manifest and package metadata models, error codes, JSON serialisation settings, protocol version | nothing | Types only. No behaviour beyond validation helpers. |
| **Sdk** | The worker runtime: pipe connection and handshake, message loop, ordering, instance objects, view builder, state publishing, commands, feeds, settings access, logging, stop deadlines | Protocol | The plugin-author API. Shipped as a NuGet package. |
| **PackTool** | Building a plugin package: validates the manifest against the built output, writes the archive | Protocol | A command-line tool. Shipped with the SDK. |
| **Recovery** | Recovery journal format, durable writes, and *revert* operations for each DesktopChange kind (Win32 calls only, no UI) | nothing beyond the OS | Journal read/write and revert. Used by host and guardian. |
| **Guardian** | Entry point: single-instance check, stale-journal recovery, launching and watching the host, relaunch budget, sign-in registration | Recovery | Command-line entry. Nothing else. |
| **Host.Core** | Config store (load, validate, migrate, atomic write, watch); package discovery and catalog; compatibility and dependency resolution; plugin supervisor (lifecycle state machine, restart budget, heartbeats); worker channels; command router; feed broker; journal writer; diagnostics. Defines the platform abstractions. | Protocol, Recovery | Services consumed by Theming, Surfaces, Settings. Platform interfaces. |
| **Host.Platform** | The Windows integration layer: monitors and stable ids, DPI, app bars, full-screen detection, Explorer restart detection, global hotkeys, foreground rights and window activation, icon extraction, worker process launch inside a job object, single-instance mutex | Host.Core (abstractions) | Implementations of the Core interfaces. |
| **Host.Theming** | Loading themes, palettes and asset packs; validating a theme against the template vocabulary; building the merged resource dictionary; resolving icons through the asset-pack chain; atomic appearance switch | Host.Core | The active appearance and its change event; template and icon resolution. |
| **Host.Surfaces** | Panel, gadget and popup windows; placing them per layout and monitor; app-bar behaviour; z-order and full-screen handling; mapping view nodes to themed controls; bindings; placeholders; keyboard navigation; dispatching user actions | Host.Core, Host.Platform (through Core interfaces), Host.Theming | Apply layout, show and close popups, focus a panel. |
| **Host.Settings** | The settings window: plugin manager (list, state, enable, disable, retry, inspect, install, remove, logs), layout editor, appearance picker, settings forms generated from schemas | Host.Core, Host.Theming | Opening the window, through the `host.settings.open` command. |
| **Host.App** | The composition root: startup order, single UI thread, shutdown order, guardian link | everything above | The host executable. |
| **Clock / Launcher / RunningWindows** | Each plugin's own behaviour | Sdk | Their manifest-declared commands, feeds and contributions. |

Platform primitives several plugins need belong to the host and are exposed as `host.*` commands, never as plugin-to-plugin code. These cover window activation, application icons, and opening popups anchored to host-owned elements.

## Control flow

### 1. Sign-in or launch → a working desktop

1. The guardian starts and takes the per-session guardian mutex. If another guardian holds it, the guardian asks it over its pipe to show the settings window, then exits.
2. The guardian reads the recovery journal. If it is non-empty and its host process is gone, it reverts every entry, clears the journal, and writes a crash record.
3. The guardian starts the host, passing its own process id and pipe name, and waits on the host's process handle.
4. The host takes the host mutex and loads its configuration. A missing file gets defaults. An invalid file is a diagnostic and falls back to the last good copy (see Failure modes). The host then resolves the active preset.
5. The host discovers packages: built-in content in the install folder first, then user content in the data folder. A user package with the same id replaces the built-in one. The host validates each manifest, checks compatibility, and resolves dependencies into a start order. A dependency cycle makes every plugin in the cycle `Incompatible`.
6. The host loads the appearance: theme, palette and asset-pack chain. If the theme fails, it uses the built-in fallback theme.
7. The host reads the monitor topology and creates panel windows. Every instance shows `placeholder(starting)`. For each `reserve` panel, it writes a journal entry, flushes it, then registers the app bar.
8. The host creates a job object and starts the enabled workers in dependency order. A plugin starts as soon as its own dependencies are `Running`, so independent plugins start in parallel. Each worker gets its pipe name, a one-time token, its data directory and the host mode.
9. Each worker connects and presents the token. Then `initialize` runs: the worker gets the protocol version, mode and plugin settings (after any `settings.migrate`). The plugin registers its declared command handlers and feeds, and the host marks it `Running`.
10. For each placed instance, the host sends `instance.create`. The worker answers with `view.set` and `state.patch` messages, and the host replaces the placeholder with the themed view.

### 2. User action → effect

1. The user clicks or presses Enter on an element, types in a text input, or uses a global hotkey.
2. Surfaces resolves the element's action:
   - `invoke(commandId, args)` goes to the command router.
   - `event(name)` goes to the owning worker as `element.event`. Text input changes are debounced at 150 ms.
   - `openPopup(popupId)` opens that plugin popup next to the element, and tells the owning worker it opened.
   A hotkey resolves to its command.
3. Before a user-initiated message goes to a worker, the host grants that worker's process foreground rights. A launched app or an activated window can then come forward.
4. The command router finds the provider. A `host.*` command runs in the host; anything else becomes `command.invoke` to the provider worker, with a timeout (5 s by default). A missing or unavailable provider gives a typed error, and Surfaces shows a short themed notice next to the element.
5. The worker acts (launches an application, asks `host.window.activate`, and so on) and publishes state. The host applies the patches in order and re-renders the affected bindings.

### 3. Worker failure → recoverable unavailable state

1. Supervisor detects one of: process exit, pipe broken, missed heartbeat (a ping every 5 s; failure after 10 s with no reply), handshake timeout (10 s), or a protocol violation (malformed message, unknown method in strict position, invalid view).
2. It terminates the process if it is still alive and marks the plugin `Faulted`. Its pending requests fail with `PluginUnavailable`. Its commands and feeds are marked unavailable, and its feed subscribers receive a `feed.update` with `available: false`.
3. Every instance of the plugin switches to `placeholder(faulted)`: themed, labelled with the plugin name, with a Retry action. The rest of the desktop is untouched.
4. After backoff, the supervisor restarts the worker and replays `instance.create` for each placed instance. After `maxRestarts` faults inside `windowSeconds` (default 3 in 300 s), the plugin becomes `Unavailable`. The placeholder and the plugin manager then show the last fault reason and the log tail, and Retry is manual.

### 4. Appearance change → every surface restyled

1. The user picks a preset, theme, palette or icon pack in settings, or edits one of those files by hand.
2. The config store writes or detects the change. Theming builds and validates the new dictionary off-screen, then resolves icons through the new chain.
3. On success, it swaps the dictionary on the UI thread in a single pass. Surfaces re-templates every element from the retained view trees and state. No worker is contacted. Panel backdrops and window shapes are re-applied.
4. On failure, the previous appearance stays. The settings window and a diagnostic name the file, key and reason.

### 5. Host abrupt failure → desktop restored

1. The guardian's wait on the host process handle returns, and the journal does not say `cleanExit`.
2. The guardian reverts each journal entry, newest first. For an app bar, it removes the registration for the dead window handle and lets the shell recompute the work area. For a work area, it restores the original rectangle. It then clears the journal. Workers have already died with the host's job object.
3. It writes a crash record. If the host restart budget allows, it relaunches the host (flow 1 from step 3). Otherwise it stays idle with the desktop restored and shows one message box naming the log.

### 6. Normal exit

The host sends `shutdown` to every worker with a 3 s deadline, then kills the ones still alive. It closes surfaces and unregisters app bars, removing each journal entry as it reverts. It sets `cleanExit` and exits. The guardian sees `cleanExit` and exits without relaunching.

### 7. Display or shell change

On a monitor topology or DPI change, Surfaces re-matches panels to monitors. A panel whose monitor is gone is hidden; its configuration is kept and it returns when the monitor does. App bars are re-registered for new rectangles; each change goes through the journal. On the shell's "taskbar created" broadcast (Explorer restarted), every app bar is re-registered.

On full-screen detection on a monitor (the shell's full-screen notification for app bars, plus foreground-window bounds equal to monitor bounds for other panels), panels on that monitor drop out of topmost. They return when the full-screen window leaves.

## Distribution

The first release has a single user, Ben, on one machine.

**Desktop.** A per-user, unpackaged install. A release is a zip on the GitHub repository's Releases page. It holds the guardian, the host (self-contained .NET and Windows App SDK, so nothing else needs installing), the built-in plugins, and the sample themes, palettes, asset packs, layouts and presets.
- **First install:** extract to the per-user programs folder and run the guardian once. A first-run flag registers the start-menu shortcut and, when `startAtSignIn` is on, the per-user Run key. No administrator rights are needed.
- **Upgrade:** exit the app, replace the install folder, and start it again. User data lives in a separate per-user data folder and is migrated forward on load.
- **Removal:** the guardian's uninstall flag removes the shortcut and Run key. Deleting the data folder is left to the user.

**Plugin SDK.** A NuGet package containing Sdk and Protocol, plus PackTool as a .NET tool. For the first release both are attached to the same GitHub release, and a plugin developer adds them through a local package folder. A `samples` plugin, built only against that package in a separate project, proves acceptance criterion 1 in CI.

**Plugins.** A plugin package is a zip of the plugin folder (manifest, worker executable, its dependencies and assets). It is installed through Settings → Install, or by extracting it into the user plugins folder. The host picks it up without a rebuild or restart. The default plugins are built and packaged by the same PackTool, and the release places them in the built-in content folder.

**Themes and the rest.** Folders or zips dropped into the matching user content folder, or installed from Settings.

## Failure modes

| Boundary | What fails | Detected by | System does | User sees | State left behind |
|---|---|---|---|---|---|
| Worker process | Crash or exit | Process handle signalled, or pipe EOF | Flow 3 | `Faulted` placeholder, then the live view after restart | None. The instance configuration is unchanged. |
| Worker process | Hang | Heartbeat timeout (10 s) or request timeout | Kill, then flow 3 | Same | None |
| Worker process | Never connects or fails the handshake | 10 s timeout, wrong token, or protocol version outside range | Kill. `Faulted`, or `Incompatible` when the version is out of range. | Placeholder naming the reason | None |
| Worker process | Protocol violation: malformed JSON, invalid view (unknown kind, literal styling, duplicate ids, binding to a missing key), message for an unknown instance | Validation on receipt | Invalid view: ignore it, keep the previous view, log. Malformed framing: treat as a fault. | A diagnostic in the plugin manager | The previous view is kept |
| Worker process | Floods messages | Per-channel queue over limit, or patches over the rate cap | Coalesce state patches latest-wins per key. Drop excess events and log. A sustained flood (10 s) counts as a fault. | Nothing, unless it faults | None |
| Worker process | Ignores shutdown | 3 s deadline | Kill | Nothing | None |
| Command routing | No provider, provider not running, timeout | Router lookup, or request timeout | Typed error back to the caller | A brief themed notice at the element; hotkeys log only | None |
| Command routing | Two plugins declare the same command | Catalog build | The provider is the one named in `commandProviders`; otherwise the lowest plugin id in ordinal order, recorded there | Plugin manager flags the conflict and lets the user choose | `commandProviders` updated |
| Feeds | Publisher faulted | Supervisor | Subscribers get `available: false` and the last snapshot is kept | The consumer decides; standard placeholders do not apply | None |
| Package discovery | Bad manifest, missing entry executable, path outside the package | Validation | `Invalid`; never started | Plugin manager lists it with the reason | None |
| Package discovery | Incompatible hostApi or mode, missing dependency, dependency cycle | Compatibility resolution | `Incompatible`; never started | Reason in the plugin manager; placeholder on placed instances | None |
| Plugin install | Bad package, same id with a running worker, disk full | Extract to staging and validate before the move | Bad package: reject, staging deleted. Upgrade: stop the worker, rename the old folder aside, move the new one in, restart. Revert the rename on any error. | Install result in settings | Old version intact on failure |
| Plugin settings | Stored settings older than the plugin's `settingsVersion` | Version compare | `settings.migrate` to the worker, then a schema check, then write | Nothing | Migrated settings. The prior file is kept as a one-time backup. |
| Plugin settings | Migration fails or the result is invalid | Error or schema check | Start the plugin with defaults in memory; do not overwrite the stored settings | Diagnostic | The stored settings are untouched |
| Config files | Invalid JSON or schema, newer `schemaVersion` | Load-time validation | Use the last good copy in memory (and on startup, the last good backup the store keeps), else defaults. Mark the file read-only to the host until it validates. | Settings banner naming the file and the error. Edits to that document are refused. | The user's file is never overwritten |
| Config files | Hand edit while the UI is editing the same document | Content-hash compare at write | The write fails with `Conflict`, the store reloads, and the UI shows fresh values | "Changed on disk, reloaded" | Disk version wins |
| Config files | Write fails (disk full, access denied) | IO error | Atomic temp-and-replace, so the old file stands. Typed error. | Settings shows that the save failed | Old file |
| Theme | Invalid XAML, a missing required key, a missing asset, `themeApi` out of range | Validation while building | Keep the current appearance. At startup, fall back to the built-in theme. | Settings shows the error | Selection unchanged on disk |
| Theme | An optional template is missing (a variant) | Template resolution | Fall back to the base key, then to the built-in default for that block kind | Nothing | None |
| Palette or icons | A token or icon is missing | Resolution | Theme default token. For icons: next pack, then theme bundle, then host default, then system icon. | Nothing | None |
| Desktop: app bar | Shell refuses registration, or Explorer not running | Return value | Fall back to `overlay` behaviour; retry on "taskbar created" | Panel overlaps windows; diagnostic | Journal entry removed if not applied |
| Desktop: Explorer restarts | App bars lost | "Taskbar created" broadcast | Re-register every app bar | Brief re-layout | Journal updated |
| Desktop: monitors | A monitor is removed or added, or DPI changes | Display-change and DPI messages | Flow 7 | Panels move or hide | Layout untouched |
| Desktop: full screen | A full-screen app or presentation mode | Shell notification, foreground bounds | Panels on that monitor drop out of topmost | Panels go behind | None |
| Desktop: hotkey | Chord already registered elsewhere | Registration result | Leave it unbound for this session | Settings shows the conflict | None |
| Host process | Crash, kill, hang (the guardian pings the host every 5 s; 30 s without a reply counts as a hang and the guardian kills it) | Guardian wait or ping | Flow 5 | Desktop returns to plain Explorer, then the host comes back | Journal cleared; crash record |
| Guardian process | Crash or kill | Host waits on the guardian process | Host relaunches the guardian in attach mode with its own pid | Nothing | None |
| Both | Killed together (process-tree kill), or power loss | The next guardian start finds a non-empty journal | Revert at the next start (flow 1, step 2). App bars and the shell work area do not survive sign-out, so power loss leaves nothing to undo. | Nothing | Journal cleared |
| Pipes | Another user or process connects | Pipe ACL is current user only; one-time token | Reject the connection | Nothing | None |

Retry semantics: worker restarts and host relaunches use the bounded budget above, with backoff. Command invocations are **never** retried automatically, because a command may have side effects such as launching an application. Config writes are not retried; the user re-saves. App-bar registration is retried only on the "taskbar created" broadcast.

## Concurrency and ordering

- **One UI thread** in the host owns every window, the resource dictionaries and every control. XAML resources are tied to a thread, and one thread lets a single theme dictionary serve every surface. Nothing else touches UI objects; work is posted to the UI thread's dispatcher queue.
- **Never block the UI thread on IPC or disk.** Each worker channel reads and writes on background tasks. Config IO, package discovery, theme parsing and icon extraction run off the UI thread, and only the final apply (the dictionary swap, the view update) is posted.
- **Per channel, order is preserved.** The channel delivers inbound messages in send order through a single reader. State patches carry the view revision they belong to; a patch older than the instance's current revision is dropped. Patches for one instance are coalesced per UI frame, latest value per key, and applied in a single pass.
- **Across channels, nothing is ordered.** Two plugins' updates interleave freely. Cross-plugin causality goes through commands, which are request/response.
- **Lifecycle transitions are serialised per plugin.** Each plugin's state machine processes its events one at a time (start, fault, stop, retry, disable, remove), so a fault arriving during a stop is handled after the stop completes. Different plugins transition in parallel, ordered only by dependencies at start.
- **One config writer.** The config store serialises all writes through one queue. A write carries the content hash it was based on, which is how concurrent hand edits are detected. Watcher events are debounced (250 ms), and the store's own writes are recognised by hash and not re-applied.
- **Recovery journal: write-ahead.** A change is appended and flushed before it is applied, and removed after it is reverted. Journal writes are serialised within the host. The guardian reads the journal only when the host is gone, or at startup before it launches the host. The single-instance mutexes guarantee only one host writes.
- **Inside a worker**, the SDK delivers inbound messages to plugin code one at a time, on one logical execution context, in arrival order. A plugin author never sees concurrent callbacks unless they start their own tasks. Plugin code may call `SetState`, `SetView` and `InvokeAsync` from any thread; the SDK serialises outbound messages.
- **Single instance:** a per-session named mutex for the host and another for the guardian. At most one worker per plugin id is enforced by the supervisor's state machine. A restart starts only after the previous process is confirmed dead.

## Alternatives considered

1. **How plugin visuals reach the screen.** *Chosen:* plugins send declarative views; the host renders them with theme templates. *Rejected:* plugin-drawn UI in the plugin's own process, embedded by the host. Input, focus, DPI and accessibility across processes are fragile, and a theme cannot restyle what a plugin draws, which defeats the deep-theming requirement. *Rejected:* WebView2 surfaces with HTML and CSS themes. Expressive, but each surface costs a browser process's memory and startup and feels less native, against idle and startup budgets. *Rejected for the first release:* custom WinUI controls loaded into the host (premise 1). They share the host's crash risk.
2. **Process boundary.** *Chosen:* one worker process per plugin. *Rejected:* in-process loading through assembly load contexts. Those manage dependencies; they do not isolate crashes or hangs, and a plugin crash would take the desktop down. *Rejected:* one shared worker process for all plugins. It saves memory, but one bad plugin takes every plugin with it, which fails criterion 7. *Rejected:* one process per contribution instance. Memory scales with placements for no gain, since instances of one plugin share code and fail together anyway.
3. **Transport.** *Chosen:* JSON-RPC 2.0 over a per-worker named pipe, current-user ACL, plus a one-time token. *Rejected:* standard input and output. Private and simple, but any stray console write by plugin code corrupts the stream, and a developer cannot start a worker under a debugger and attach it to a running host. *Rejected:* gRPC. Code generation and an HTTP/2 stack are heavy for a local link carrying small JSON. *Rejected:* out-of-process COM. It needs registration and is awkward for third-party authors. *Rejected for now:* a binary format such as MessagePack. Faster, but not readable in logs; the serialisation is behind the Protocol module and can change with a protocol version bump.
4. **Theme format.** *Chosen:* XAML resource dictionaries loaded at runtime, keyed by a host-defined template vocabulary. They are data, cannot carry code-behind, and give full control templates, visual states and transitions. *Rejected:* a custom JSON styling language. It means building a second rendering engine, and it would never reach control-template expressiveness. *Rejected:* compiled theme assemblies. They are executable, which contradicts premise 3, and they load code into the host.
5. **Configuration storage.** *Chosen:* JSON files, one per document, written atomically. *Rejected:* SQLite. Not hand-editable or diffable, and hand-editing is premise 3. *Rejected:* the registry. Same problem, and harder to back up or share as a preset.
6. **Abrupt-failure recovery.** *Chosen:* a separate guardian process that is also the entry point, reading a write-ahead journal. *Rejected:* cleanup in the host only. The brief rules it out: a dead process cleans nothing. *Rejected:* a guardian spawned by the host inside its job object. It would die with the host. *Rejected:* restarting Explorer to reset the desktop. Heavy-handed, disrupts the user's windows, and is not available in replacement mode.
7. **Packaging.** *Chosen:* unpackaged, per-user, self-contained. *Rejected:* MSIX. Package identity virtualises file and registry access, which complicates user plugin folders and later shell-replacement launch. It also needs a signing certificate. It can come back as an extra channel later.
8. **Desktop-layer gadgets.** *Chosen:* windows kept at the bottom of the z-order. *Rejected:* re-parenting into Explorer's desktop worker window. Undocumented, breaks across Windows builds, and does not exist without Explorer.
9. **Threads for windows.** *Chosen:* one UI thread for all surfaces. *Rejected:* a thread per panel. Resources are thread-affine, so each thread would need its own copy of the theme, and theme switches would have to coordinate across threads.

Decided here with no live alternative worth a paragraph (logged in [90-decisions.md](90-decisions.md)): .NET 10 (LTS) for everything; a pinned stable Windows App SDK release chosen in the first slice; Windows 11 22H2 (build 22621) minimum; x64 only for the first release.

## Open questions

1. **Explorer's taskbar in coexistence mode.** When a `reserve` panel takes the bottom edge, should the host hide Explorer's taskbar (a reversible change the journal would track), or leave the taskbar alone and leave it to the user to set it to auto-hide? Until answered, the host leaves it alone.
2. **Monitor and DPI test matrix.** Which physical displays, and which scale settings, are available to test on? Criterion 10's matrix is written against what exists. Without an answer, it covers one monitor at 100 % and 150 %, plus a second monitor emulated with a virtual-display driver if one is installed.
3. **SDK publication.** Should the SDK also go to nuget.org? That needs an account and an API key that only Ben can provide. Until answered, the SDK ships on GitHub Releases only.
4. **Arm64.** Is any Arm64 machine in scope? Until answered, x64 only.
5. **Prototype checks that may move the design.** The foundation prototype must confirm three things before the theme vocabulary is frozen:
   - Runtime-loaded XAML dictionaries can carry control templates, visual states and transitions in WinUI 3.
   - Per-pixel transparent, shaped panel windows can be made alongside system backdrops.
   - App-bar registration behaves correctly from a WinUI 3 window.

   A failure in any of them is a design change recorded in [90-decisions.md](90-decisions.md), not a silent workaround.

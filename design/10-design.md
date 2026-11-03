# Design — SubZeroDev.WindowManager

Built from [00-brief.md](00-brief.md). Approach approved 2026-11-01: **declarative contributions rendered by the host, every plugin in its own worker process, and a separate guardian process that undoes desktop changes if the host dies.**

The second release's design is the [Second release](#second-release) part at the end of this file. It extends this design and changes only what it names.

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
- `wallpapers`: list of image paths (PNG, JPEG or BMP). A preset's `wallpaper` names one by path or file name; in replacement mode the desktop draws it.

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
- `entries`: list of DesktopChange `{ id: GUID, kind, monitor, appliedAt, payload }`. `kind` is `appBar` (payload: window handle, edge, rectangle) or `workArea` (payload: the original work area rectangle). The second is used in replacement mode, where the host owns the work area.

**Shell registration.** One per user, written only by the guardian. `registered`: bool; `previous`: the per-user `Winlogon\Shell` value before registration, or its absence; `registeredAt`. It is what `--shell off` restores.

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
| **Guardian** | Entry point: single-instance check, stale-journal recovery, launching and watching the host, relaunch budget, sign-in registration, shell registration (`--shell on|off`) and its policy checks, the replacement-mode fallback to Explorer | Recovery | Command-line entry. Nothing else. |
| **Host.Core** | Config store (load, validate, migrate, atomic write, watch); package discovery and catalog; compatibility and dependency resolution; plugin supervisor (lifecycle state machine, restart budget, heartbeats); worker channels; command router; feed broker; journal writer; diagnostics. Defines the platform abstractions. | Protocol, Recovery | Services consumed by Theming, Surfaces, Settings. Platform interfaces. |
| **Host.Platform** | The Windows integration layer: monitors and stable ids, DPI, app bars, full-screen detection, Explorer restart detection, global hotkeys, foreground rights and window activation, icon extraction, worker process launch inside a job object, single-instance mutex. In replacement mode also: the shell roles (shell window, shell hook, task-manager window), the notification-area and app-bar host (`Shell_TrayWnd`), the work area, start-up programs, the low-level Windows-key hook, and session commands (lock, sign out, restart, shut down, sleep) | Host.Core (abstractions) | Implementations of the Core interfaces. |
| **Host.Theming** | Loading themes, palettes and asset packs; validating a theme against the template vocabulary; building the merged resource dictionary; resolving icons through the asset-pack chain; atomic appearance switch | Host.Core | The active appearance and its change event; template and icon resolution. |
| **Host.Surfaces** | Panel, gadget and popup windows; placing them per layout and monitor; app-bar behaviour; z-order and full-screen handling; mapping view nodes to themed controls; bindings; placeholders; keyboard navigation; dispatching user actions | Host.Core, Host.Platform (through Core interfaces), Host.Theming | Apply layout, show and close popups, focus a panel. |
| **Host.Settings** | The settings window: plugin manager (list, state, enable, disable, retry, inspect, install, remove, logs), layout editor, appearance picker, settings forms generated from schemas | Host.Core, Host.Theming | Opening the window, through the `host.settings.open` command. |
| **Host.App** | The composition root: startup order, single UI thread, shutdown order, guardian link | everything above | The host executable. |
| **Clock / Launcher / RunningWindows** | Each plugin's own behaviour | Sdk | Their manifest-declared commands, feeds and contributions. |
| **Tray / DesktopIcons / Session / Indicators** | The replacement-mode features: notification-area icons (from the host's `host.tray.icons` feed), Desktop-folder icons, the power menu, and volume, network and battery | Sdk | Same |

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

### 8. Replacement mode

The same host, plugins and themes, with the host doing the jobs Explorer did. It follows what LiteStep did when it was the shell.

1. **Registration.** Ben runs `SubZeroDev.WindowManager.exe --shell on`. The guardian reads the per-user `Winlogon\Shell` value and the settings that override it: the Custom User Interface policy and a Shell Launcher configuration. If one overrides it, the guardian names it and changes nothing. Otherwise it records the previous value, or its absence, in the shell registration, then writes its own command line with `--mode replacement`. `--shell off` writes back exactly what was recorded. No step needs elevation.
2. **Sign-in.** Windows starts the guardian instead of Explorer. With Shift held, it starts `explorer.exe` and exits, which is the escape hatch. Started with `--mode replacement` while Explorer already is the shell, it exits, unless `--take-over` asks it to close Explorer the way Explorer's own exit command does, so Windows does not restart it.
3. **Host start.** The guardian starts the host with `--mode replacement` (flow 1 from step 3). Before the first panel shows, the host:
   - takes the shell roles: its desktop window becomes the shell window, a hook window receives shell-hook messages (window created, destroyed, activated, flashing), and it registers as the task-manager window;
   - creates `Shell_TrayWnd` and `TrayNotifyWnd`, so the `Shell_NotifyIcon` and `SHAppBarMessage` calls of other applications reach it over `WM_COPYDATA`, and then broadcasts `TaskbarCreated` so running applications re-add their icons;
   - hides minimised windows (`ARW_HIDE`), and sets the work area itself from its `reserve` panels and other applications' app bars, journalling the original as `workArea`;
   - installs the Windows-key hook on its own thread.

   After the first panel shows, it signals the shell-ready event (`ShellDesktopSwitchEvent`) and sends `WM_USER` to the desktop window, so the sign-in screen gives way.
4. **Plugins** start as in flow 1, with `--wm-mode replacement`. A plugin whose `modes` lacks `replacement`, or with `requiresExplorer: true`, is `Incompatible`.
5. **Start-up programs.** On the session's first host start only, the host runs them in LiteStep's order: HKLM RunOnce, RunOnceEx, HKLM Run, HKCU Run, the common Startup folder, the user Startup folder, then HKCU RunOnce. It skips a source that policy disables. A host relaunch in the same session runs nothing.
6. **File Explorer.** `explorer.exe <path>`, started while the host holds the shell window, opens a folder window and does not take over the desktop. Win+E and the launcher use this.
7. **Failure.** A host crash is flow 5, and the guardian also restores the work area. When the relaunch budget is spent, the guardian starts `explorer.exe` as the shell for the rest of the session before it shows its message. The registration stays, so the next sign-in tries again.
8. **The way back.** `--shell off`, the switch in Settings, and `host.shell.restoreExplorer` (which also ends the host and starts Explorer for this session) all restore the recorded value.

## Distribution

The first release has a single user, Ben, on one machine.

**Desktop.** A per-user, unpackaged install. A release is a zip on the GitHub repository's Releases page. It holds the guardian, the host (self-contained .NET and Windows App SDK, so nothing else needs installing), the built-in plugins, and the sample themes, palettes, asset packs, layouts and presets.
- **First install:** extract to the per-user programs folder and run the guardian once. A first-run flag registers the start-menu shortcut and, when `startAtSignIn` is on, the per-user Run key. No administrator rights are needed.
- **Upgrade:** exit the app, replace the install folder, and start it again. User data lives in a separate per-user data folder and is migrated forward on load.
- **Removal:** the guardian's uninstall flag removes the shortcut and Run key, and unregisters the shell if it is registered. Deleting the data folder is left to the user.
- **Replacement mode:** no separate install. `--shell on` registers the app as Ben's shell, and `--shell off` or Settings unregisters it (flow 8).

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
| Shell registration | A policy or Shell Launcher overrides the per-user value | The guardian reads them before writing | Write nothing; exit 5 naming the override | The message names the setting to change | Registry unchanged |
| Replacement start | The host cannot start, or keeps crashing, after sign-in | Guardian relaunch budget (I17) | Start `explorer.exe` as the shell for the rest of the session (I23) | One message naming the log; a normal Explorer desktop | Registration kept; journal reverted |
| Replacement start | Explorer already is the shell | Another process owns the shell window | Without `--take-over`: exit 6. Host already running: run without the shell roles and log `ShellRoleTaken` | Nothing changes | None |
| Notification area | A malformed or oversized `Shell_NotifyIcon` message, or the owner window dies | Size and version checks on `WM_COPYDATA`; window check before each use | Ignore the message; drop a dead window's icons | The icon disappears | None |
| Notification area | The host restarts | Host start in replacement mode | Broadcast `TaskbarCreated`, so applications re-add their icons | Icons return | None |
| Desktop: app bar (replacement) | Another application's app bar asks for an edge | `ABM_*` through `Shell_TrayWnd` | Apply the rules Explorer applies: propose a non-overlapping rectangle, reserve it, recompute the work area (journalled) | Maximised windows fit the remaining space | Journal updated |
| Session command | Restart, shut down or sign out is blocked by an application, or refused | Return value | Typed error | A notice at the element | None |
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
10. **How the app becomes the shell.** *Chosen:* the per-user `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell` value, written and restored by the guardian. It needs no elevation, affects only Ben's account, and is the route LiteStep's users took. *Rejected:* the machine-wide `HKLM` value. It needs elevation and changes every account. *Rejected:* Shell Launcher. It needs elevation and an Enterprise feature, and it is built for kiosks. *Rejected:* writing the Custom User Interface policy. Policies belong to administrators; the app reports one instead of changing it. *Rejected:* replacing or renaming `explorer.exe`. That breaks File Explorer, servicing and every other account.
11. **Hosting the notification area and app bars.** *Chosen:* in replacement mode the host creates `Shell_TrayWnd` itself and implements the `Shell_NotifyIcon` and `SHAppBarMessage` protocols that applications send it, as LiteStep's tray service did; the icons reach plugins as a host feed. *Rejected:* a plugin owning the tray window. The window must exist before applications start and must survive plugin restarts. *Rejected:* hosting the notification area in coexistence mode. Explorer owns `Shell_TrayWnd` while it runs, and a second one would take icons away from it.
12. **The Windows key.** *Chosen:* a low-level keyboard hook in the host, on its own thread, installed only in replacement mode. *Rejected:* `RegisterHotKey` alone, which cannot express a lone Windows-key press. *Rejected:* a hook in a plugin process. The hook must answer every key press within the system timeout, and a worker restart would drop keys.

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

   *Answered:* all three pass on Windows App SDK 1.8.260317003, unpackaged and self-contained (decision "S1: Pin Windows App SDK 1.8.260317003"). Hit-testing that follows a template's shape is still open.
6. **The per-user shell value on Ben's build.** The design assumes Windows honours the per-user `Winlogon\Shell` value on Windows 11 Enterprise build 26200. S17's prototype check confirms it. If it does not, the fallback is the per-user Custom User Interface value (`HKCU\...\Policies\System\Shell`), which also needs no elevation. Switching to it is a decision entry.
7. **Explorer-hosted features without Explorer.** Toasts, the notification centre, Quick Settings and Task View are hosted by Explorer or its shell-experience processes. S23's qualification records which of them still work with Explorer absent on Ben's build. The brief keeps them out of scope. *Recorded:* the expected state of each is in `docs/replacement-mode.md`, and a week of daily use confirms it (S23.1, decision "S23: replacement mode qualified for daily use").

---

# Second release

Added 2026-11-03 from the second-release part of [00-brief.md](00-brief.md). The first release above stands; this part extends it and changes only what it says it changes. Approach approved 2026-11-03: **one authoritative window model in the host, published as a feed with commands, with the taskbar depth, virtual desktops, Task View and switcher as plugin views of it; notifications independent of the model, read by a packaged worker and drawn by the shell.**

Premises confirmed before designing (Ben, 2026-11-03):
1. The shell draws its own notification pop-ups and centre from the history that a packaged listener reads. It does not rely on Windows' own pop-ups. The run without Explorer is the first notifications slice's gate, and this design does not depend on its result.
2. Package identity is confined to the notifications plugin's worker. The guardian, the host and every other plugin stay unpackaged. The one-time setup (trust the certificate, allow notification access, switch notifications on) is a guided step shown to Ben, never silent. The first release's no-administrator promise still covers shell registration.
3. Virtual desktops are the shell's own sets of windows: hide and show, and moving a window re-files it. Windows' own desktops depend on Explorer and on undocumented interfaces. The cost is that applications asking Windows which desktop they are on do not see ours.
4. Every second-release feature is replacement-mode only, so Win+A, Win+N and Win+Tab never compete with Explorer. The window model and its feed are infrastructure, not a feature, and run in both modes.
5. Custom plugin UI in host windows, a theme editor and automatic updates are out of this release (deferred, not open).
6. The design assumes issue #28's manual checks pass. A failure reopens it. Slices follow Ben's priority order (notifications first), one pull request each.

**Revised 2026-11-03** against [the window-model handoff](2026-11-03-second-release-design-handoff.md), a design input and not an implementation instruction. The revision does not reopen approach A or premises 1–6. It corrects five things in the first draft: (1) the registry no longer has a model-wide `eligible` rule: the host tracks every admitted window and publishes facts, and each view applies its own rule; (2) a window's lifetime identity is a host-assigned generation, separate from its application `groupKey`, and the claim that handle, process id and start time alone survive handle reuse is withdrawn; (3) `windows.running` and the existing window commands have a documented compatibility table and a single-tracker cutover; (4) the slices (S24–S31) follow the brief's order, with the model extraction as the first window slice and the reason it is larger stated; (5) seven acceptance checks, C1–C7, each have a slice owner and an evidence method. The checks are planned requirements, not results. Ben's five questions (open questions 1–5) are unchanged and still open.

## The shape in one paragraph

Two things are added to the host, and four kinds of plugin sit on top. The **window model** takes over window scanning from the running-windows plugin, so the host becomes the only place that discovers windows. It is one in-memory registry of every top-level window the host has admitted, kept for the window's whole life whether or not it is visible, with desktop membership, a lifetime identity, a separate grouping key, and progress and overlay badge state; views decide what to show. It is published as the `host.windows` and `host.desktops` feeds with commands to switch desktops and move windows. Hiding and showing windows is done by a **window-effects thread**, behind the recovery journal, so a dead host cannot leave windows hidden. The **notification path** is separate: a packaged worker reads Windows' notification history, publishes it as a feed, and the notifications plugin draws pop-ups and a centre as host surfaces. On top: running windows (grouping, hover thumbnail, jump lists, badges), virtual desktops with a Task View, quick settings, and a power-user menu, all reached by chords the existing Windows-key hook already owns. The desktop plugin gains the shell's file context menu, built in its own worker so third-party menu extensions never load into the host.

## Data model

**Persisted** (new or changed; the first release's documents are unchanged unless named):

- `desktops.json` (data root): the shell's desktops. `{ schemaVersion: 1, desktops: [ { id: guid, name?: string } ], lastCurrent: guid }`. Only the list and names persist. Which window sits on which desktop does not: window handles mean nothing after the host restarts, and the guardian shows every window it finds hidden (flow 12), so a host restart merges all windows onto the first desktop. That is the stated cost of the fail-safe.
- Recovery journal: a third entry kind, `hiddenWindows`: `{ kind, windows: [ { hwnd, processId, processStartTicks, threadId, className } ] }`. One entry holds the whole hidden set. It is replaced, not appended, on each switch, and the entry written before a switch is the union of the old hidden set and the windows about to be hidden, so a crash at any point leaves a superset. The fingerprint has five fields because recovery cannot trust a handle: it shows a window only if all five still match (flow 12).
- `settings.json`: one additive optional key, `windowsKeys`: chord → command id, with the host's defaults supplied when absent. No `schemaVersion` bump.
- Presets: `wallpaper` becomes `{ default?: Wallpaper, monitors?: { <monitorId>: Wallpaper } }`, where `Wallpaper` is the first release's `{ image, fit }`. This is `presets` `schemaVersion` 2; the pure migration v1 → v2 wraps the old object as `default`.
- `plugin-data\subzerodev.notifications\state.json`: `{ lastSeen: instant, doNotDisturb: bool }`. **No notification content is ever persisted by the shell.** Windows keeps the history; the shell reads it.
- Sparse package for the notifications worker, shipped in the install root: a manifest, the logo, and the signed package file. Registered per user, with the plugin folder as its external location. The certificate it needs trusted is a public `.cer` shipped beside it.

**In memory** (host, rebuilt on every start):

- **ManagedWindow** (Host.Core). One entry per admitted window, kept until the window is destroyed. Identity: a *lifetime key* (handle plus a host-assigned generation) verified by a *fingerprint*; see *Lifetime identity* below. Facts, published as they are: `title`, `exePath`, `aumid` (now resolved from the window's property store or its process; the first release left it null), `groupKey`, `monitorId`, `desktopId`, `pinnedToAll`, the visibility, cloaking, ownership and style facts, `active`, `minimised`, `attention` (a flash, or an app tried to show itself while on another desktop), `progress` (`none`, `indeterminate`, `normal`, `error`, `paused`, plus 0–1), `overlay` (icon reference and description), `firstSeen` and `activatedAt`. **Derived:** `groupKey` is the AUMID when there is one, else the normalised executable path, with an override for windows hosted by a frame process (for example UWP frames). **There is no model-wide `eligible` field.** The first draft had one (the first release's strip rule, or in the hidden set); it made hiding look like leaving, so it is withdrawn. Each view derives its own list from the facts (*View rules*).
- **DesktopSet**: the ordered desktops, `currentId`, and per desktop the windows' z-order captured when the user left it. Invariant: at least one desktop; `currentId` names one.
- **HiddenSet**: the windows the shell has hidden, mirrored in the journal. A window is in it from the write-ahead flush before the hide until the show completes.
- **Chord table**: the effective `windowsKeys` map.
- **NotificationRecord** (inside the notifications worker only): `id`, `appId`, `appName`, `title`, `body`, `at`, `silent`, and `read`, derived from `lastSeen`. The history itself is Windows'. A dismissal or clear is a call back into Windows, not a local deletion.
- **QuickSetting** (inside the quick-settings worker): `id`, `kind` (`toggle` or `level`), `value`, `available`, `reason`. Always re-read from the device after a change; the shell never keeps its own idea of the device state.
- **JumpListEntry** (inside the running-windows worker): `category` (`recent`, `frequent`, `tasks`, or a custom name), `title`, `target`, `args`, `icon`.

**Ownership.** The window model belongs to the host's UI thread; everything else reads immutable snapshots. Device and notification state belongs to the worker that talks to the device.

**Registry boundary.** The registry is not today's visible running-windows list moved into the host. A top-level window of another process (not message-only, not owned by the host) is *admitted* once it has been visible with a non-empty title, or as soon as the shell hides it. It then stays until it is destroyed: a Destroy event, or a rescan or command-time check that finds the handle invalid or its fingerprint changed. Hiding it for another desktop, minimising it, cloaking it, or being left out of any view is a *visibility change*, never a destruction: the window keeps its desktop, group and badges. Windows never admitted (tool tips, input-method and other helper windows that were never titled and visible) cost nothing.

**Lifetime identity.** Two identities that are never mixed. *Lifetime identity* says which window. *Group identity* (`groupKey`) says which application and is shared by many windows. Commands and thumbnails address a window by lifetime identity and never by `groupKey`.

- The host assigns a **generation** (a 64-bit counter, on the UI thread, at admission) to every admitted window. The lifetime key is `(handle, generation)`; a generation is never reused within a host run.
- A **fingerprint** (process id, process start time, thread id, window class) is captured at admission and re-verified at every rescan and immediately before every state-changing command. A mismatch means the handle was reused: the old generation is destroyed and the handle is admitted afresh. Handle, process id and start time alone cannot tell a window from a successor created by the same process, so they are not the identity (the first draft said they were).
- **Public reference:** `wk1:<epoch>:<generation>`. The *epoch* is random per host start. A reference with another epoch is `StaleWindowReference`; a current-epoch generation that is gone or fails its fingerprint check is `WindowGone`. Thumbnail registrations take the same reference and give the same two errors.
- **Limits.** Between two checks a process can destroy a window and create another with the same handle, thread and class; a missed Destroy event then goes unseen until the next rescan (2 s) or the next command on it. The command-time check narrows that to the instant before the call but cannot close it, because the gap between checking a handle and using it is inherent to window handles. Generations do not survive a host restart: afterwards every old reference is `StaleWindowReference` and nothing is carried over. The recovery journal does not use generations; it records the fingerprint.
- **Legacy handle-based commands** (`windows.activate { hwnd }`, `windows.minimize { hwnd }`, `host.window.activate` and `host.window.minimize` with `hwnd` only) resolve the handle to whatever the registry holds under it now. They carry the gap above and have no stale rejection. If the registry does not hold the handle but the handle is a window, they act on it unmanaged, as the first release did; otherwise `WindowGone`. New callers pass `windowKey`.

**Published facts.** The feed publishes facts and leaves the verdict to the view:

| Fact | Meaning |
|---|---|
| `visible` | `IsWindowVisible` at the last observation; false while the shell has it hidden |
| `cloak` | `none`, `app`, `shell` or `inherited`, from the cloaked attribute |
| `shellHidden` | the shell itself hid it, for another desktop; it is in the hidden set |
| `ownerKey` | the owner's `windowKey`, or null |
| `toolWindow`, `noActivate`, `appWindow`, `titled` | the style and title facts the first release's strip rule used |
| `desktopId`, `pinnedToAll` | desktop membership |
| `firstSeen` | monotonic number, from the order of admission |
| `activatedAt` | monotonic number, set each time the window becomes the foreground window; 0 if never |
| `active` | the foreground window, with the first release's rule that a tool window taking the foreground leaves the previous window active; the rule now lives in the model so every view agrees |
| `minimised`, `attention`, `progress`, `overlay` | as before |

**View rules.** Each view applies its own default rule to the same facts. A difference between views is by design and is not an inconsistency. What must agree is the facts (which desktop a window is on, whether it exists) and the revision they were read at.

| View | Desktops | Monitors | Includes | Order |
|---|---|---|---|---|
| Running windows (`windows.running`, the strip) | the current desktop, plus pinned windows | all, or the contribution's `scope` setting (`this-monitor`) | the first release's rule: `visible`, `cloak` none, `titled`, and (`appWindow`, or unowned, not `toolWindow`, not `noActivate`); minimised windows included | `firstSeen` ascending |
| Task View | every desktop, grouped by `desktopId` | all | the same style rule, and `visible` or `shellHidden`, and `cloak` none or `shell`; minimised included; a pinned window appears once, in an all-desktops row | `activatedAt` descending within each desktop |
| Alt+Tab switcher | the current desktop, plus pinned windows | all | the same style rule and `visible`; an owned window is folded into its owner's entry; minimised included | `activatedAt` descending; the first step selects the second entry |

A plugin may offer settings that change its own rule (the strip's `scope` is the first release's example). None of them changes the facts.

**Feed consistency.** `host.windows` and `host.desktops` carry the same `revision`, a counter incremented once per published snapshot. Both feeds come from one immutable snapshot in the same dispatcher turn, and both publish completed state only (I30): `current` changes and windows are reported shown or hidden only after a switch plan has completed, and `host.desktops` carries `busy` and `switching` while one runs. A consumer that reads both (Task View, the switcher) uses the SDK's paired view. It renders only when the two revisions are equal. If they differ it holds the newer one for up to 250 ms, then re-reads the lagging feed's current snapshot, and if they still differ it renders from the lower revision and marks itself stale. A consumer that reads one feed needs no pairing. Because every consumer reads the same snapshot, two views that both include a window agree on its desktop, and all of them converge on the same completed switch.

## Module boundaries

The first release's graph is unchanged and stays acyclic; the new code goes into its existing layers.

```text
Protocol, Recovery -> Host.Core -> Host.Platform -> Host.Theming -> Host.Surfaces -> Host.Settings -> Host.App
Protocol -> Sdk -> plugins (a plugin references Sdk only)
```

| Module | Gains | Depends on (unchanged) |
|---|---|---|
| Host.Core | `IWindowModel` (the registry, lifetime keys and fingerprints, grouping, badges, attention, the snapshot `revision`), `IDesktopSet` (desktops, membership, switch plans), `IWindowEffects` (the port to the effects thread), the feeds `host.windows` and `host.desktops`, the commands `host.desktops.*` and `host.window.*`, the `hiddenWindows` journal kind, the chord table | Protocol, Recovery |
| Host.Platform | `IWindowTracker` (WinEvent hooks, the 2 s rescan, the shell-hook window; moved here from the running-windows plugin, and the only window discovery in the product), the **window-effects thread** (async hide and show, one thread), `ITaskbarBackchannel` (receives progress and overlay on the host's `Shell_TrayWnd`, and broadcasts `TaskbarButtonCreated`), `IThumbnailService` (DWM thumbnails), the extended Windows-key hook (chords, Alt+Tab sequence) | Core |
| Host.Surfaces | The `Thumbnail` element, hover popups, two popup placements (`toast`: a non-activating stack on a work-area corner; `overlay`: a full-monitor surface that takes keyboard focus), per-monitor wallpaper | Theming, Platform |
| Host.Settings | Pages: Desktops, Notifications setup, wallpaper per monitor | Surfaces |
| Host.App | Wires the above; the test hook gains fakes for each device and for the effects thread | all |
| Sdk / Protocol | Additive: host API and protocol 1.1 (hover popup property, popup placement, `Thumbnail` element, activation `trigger`) | — |
| Recovery / Guardian | Reverts the `hiddenWindows` entry by showing every listed window that still exists | Protocol |

**Plugins:**

| Plugin | Role | Reads | Modes |
|---|---|---|---|
| `subzerodev.running-windows` (changed) | The strip, then grouping, hover thumbnail, jump lists, badges. Its scanner, WinEvent hooks, shell-hook window and reconcile loop are removed in the first window slice (flow 18). Its **compatibility adapter** builds `windows.running` from `host.windows` (the first release's item shape plus additive fields; current desktop plus pinned) so dependants do not break. The adapter lives in this plugin; with the plugin disabled the feed is unavailable and shared tracking continues | `host.windows`, `host.desktops` | both; depth features only in replacement |
| `subzerodev.notifications` (new) | **The only packaged process.** Centre, pop-ups, bell with unread count, do-not-disturb | Windows' history | replacement |
| `subzerodev.desktops` (new) | Panel item and Task View (Win+Tab) | `host.windows`, `host.desktops` | replacement |
| `subzerodev.switcher` (new, last, droppable) | Alt+Tab switcher | `host.windows` | replacement |
| `subzerodev.quick-settings` (new) | Win+A popup: Wi-Fi, Bluetooth, airplane mode, brightness, do not disturb | the devices | replacement |
| `subzerodev.power-menu` (new) | Win+X menu | the WinX folders | replacement |
| `subzerodev.desktop-icons` (changed) | Native shell context menu on icons and on the background | the Desktop folders | replacement |

The notifications plugin reads nothing from the window model, and the window model knows nothing about notifications. A bug in one cannot corrupt the other. The power menu reads the WinX folders only and does not depend on the window model.

**What the host owns, and what stays in plugins.** The host's boundary is shared platform capability: window discovery, lifetime identity, desktop membership, group identity, progress and overlay facts, the state-changing commands, recovery, and rendering primitives (the `Thumbnail` element). Presentation stays in plugins: which windows a view shows and in what order, grouping presentation, jump-list menus and their interactions, and every other feature's behaviour. `host.tray.icons` is the precedent: the host owns the facts, plugins own the views.

**Plugin independence.** Disabling or crashing any view plugin leaves the model, both feeds and the commands running, because the host and not a plugin owns tracking. A restarted plugin resubscribes and receives the current snapshot; no plugin keeps a tracker of its own.

## Control flow

Flows 9–19 follow the first release's flows 1–8.

9. **A window appears, changes, hides or goes (the model update).** A WinEvent (create, show, hide, destroy, name change, foreground, minimise, cloak) or the 2 s rescan reaches the tracker, which posts an observation to the UI thread. The model admits a new window by the registry boundary, gives it a generation, and files it on the current desktop (an owned window follows its owner). For a known window it updates the facts observed: a hide or a cloak changes `visible` or `cloak` and nothing else. Only a Destroy, an invalid handle, or a fingerprint mismatch removes a window, and removal also clears it from the hidden set. It recomputes `groupKey`, increments `revision`, and publishes the next snapshot, at most once per frame. A window that shows itself while assigned to another desktop is hidden again and gains `attention`.
10. **Switch desktop (Win+Ctrl+Left/Right, a panel item, or `host.desktops.switch`).** The chord reaches the command router. `IDesktopSet` computes a *plan*: the windows to hide (the current desktop's, except pinned ones), and the windows to show (the target's, in their saved z-order, with the last active one last). The plan goes to the effects thread, which (1) replaces the journal's `hiddenWindows` entry with the union of the old hidden set and the windows about to be hidden, and flushes, (2) re-verifies each window's fingerprint, drops those that are gone, and applies the hides and shows with non-blocking window-position calls, (3) replaces the entry with the exact hidden set, (4) posts the result. While the plan runs `host.desktops` shows `busy` and `switching`; when it has completed, one snapshot changes `current` and each affected window's `visible` and `shellHidden`. A second switch request while one runs is held, and only the latest target is applied.
11. **Move a window (Task View drag, or `host.window.moveToDesktop`).** The model validates the `windowKey` (`StaleWindowReference`, `WindowGone`), the target (`DesktopNotFound`) and the window (`WindowNotMovable`), then re-files the window. If the target is not the current desktop, the plan hides it (journal first). Moving to "new desktop" creates one first; that desktop belongs to the call, and flow 19 says what happens to it if the window closes meanwhile.
12. **Host dies with windows hidden.** The guardian reads the journal, finds `hiddenWindows`, and for each listed window checks all five fingerprint fields (valid handle, process id, process start time, thread id, class), so a handle reused by another window is never touched. It shows only windows that are not visible, in the form that neither activates nor restores, so a minimised window comes back minimised; it clears the entry and relaunches the host (flow 5). The entry written before any hide is a superset of what can be hidden, so a crash before, during or after the hides of a switch strands nothing; a window already shown is skipped. After the relaunch every recovered window is on the first desktop (`desktops.json` keeps only the list, so membership does not survive a restart), and every old window reference is `StaleWindowReference`. When the relaunch budget is spent, the same revert runs before Explorer is started (I23). A window that still will not show is logged `WindowNotMovable` and left for the user.
13. **A notification arrives.** Windows adds it to the history and raises the listener's change event. The notifications worker re-reads the history, diffs it against what it published (by notification id), updates the `notifications.history` feed, and, unless do-not-disturb is on or the notification is silent, opens the `toast` popup (placement `toast`). The pop-up shows for `popupSeconds`, at most `maxVisible` at a time; the rest collapse into "N more". Clicking it activates the originating application (flow 15). The centre (Win+N or the bell) lists the history grouped by application, with dismiss and clear; both call back into Windows.
14. **A chord (Win+A, Win+N, Win+Tab, Win+X, Win+Ctrl+arrows, Alt+Tab).** The hook, on its own thread, sees the key. If the chord's command has a `Running` provider at that instant, the hook swallows the chord and posts the command id to the UI thread, which invokes it; otherwise the key passes through. Alt+Tab is a sequence: while Alt is held, Tab and Shift+Tab are swallowed and become `switcher.step`; Alt's release becomes `switcher.commit`; Esc becomes `switcher.cancel`.
15. **Activate a notification's source.** The worker starts the notification's application by its AUMID, passing the notification's launch string when there is one. Toast buttons and inline replies cannot be invoked through Windows' listener interface, so they are not offered (open question 1).
16. **Hover a taskbar group (thumbnail).** The element carries a hover popup with a 400 ms delay. The host opens the popup at the element without taking focus, closes it when the pointer leaves both, and the popup's view has one `Thumbnail` element per window in the group. The element asks Host.Platform for a DWM thumbnail of the window, drawn into the popup's own window. The registration lives as long as the element (I36).
17. **Right-click a desktop icon (or the background).** The desktop-icons worker builds the shell's context menu in its own process, with a hidden owner window, and shows it at the pointer after the host has granted it foreground rights (I18). The chosen verb is invoked by the same worker. Rename opens a popup with a TextInput instead of Explorer's in-place edit.
18. **Cutover: the host becomes the only tracker (the first window slice).** One pull request does three things together, so there is never a moment with two trackers. (1) Host.Platform gains `IWindowTracker`, and the host starts it at launch, before any plugin. (2) The running-windows plugin stops scanning and builds `windows.running` from a `host.windows` subscription: its compatibility adapter. (3) Its `WindowTracker`, WinEvent hooks, shell-hook window and reconcile loop are deleted. *Clean start:* the host enumerates the existing windows, admits them, and publishes revision 1 before supervising any plugin; the adapter publishes `windows.running` only after it has received a snapshot, so a dependant never sees a half-built list. *Plugin restart:* the supervisor restarts the worker, which resubscribes, receives the current snapshot and republishes; nothing is rescanned. *Plugin disabled:* the adapter stops and `windows.running` becomes `available: false`, and a command to its provider fails without starting the worker, as S6 decided; the model, the other feeds and the commands carry on. *Version mismatch:* the adapter declares `hostApi ^1.1`; a 1.0 host (an application rolled back) lists it as `Incompatible` with `HostApiMismatch` and does not start it, and a 1.0 plugin on a 1.1 host runs unchanged. *Rollback:* the application is one zip, so rolling back restores the first release's plugin and its scanner together, never the new host with the old scanner or the reverse. The one exception is a user package that overrides the built-in id (a user package with the same id replaces the built-in one): it is the user's plugin, it may still carry a scanner, and the host's model stays authoritative for every feed the host owns. It does not make a second authoritative tracker, only a second private one, and the host does not police it.
19. **A window closes during an operation.** Each operation has stages, and each stage has one defined outcome. A completed desktop switch is never undone because a window closed; a posted activation is never reported as proven foreground.
    - *Activate (`host.window.activate` with a `windowKey`):* (a) validate the reference: another epoch is `StaleWindowReference`, a gone generation or failed fingerprint is `WindowGone`, with no side effect. (b) If the window is on another desktop, run the switch (flow 10); if the window closes during it, the switch completes and stays, and the call returns `WindowGone`. (c) Restore if minimised, then request the foreground; a window that closes here gives `WindowGone`. (d) Verify the foreground after about 250 ms: the result is `{ outcome: "foreground" }` if the window is the foreground window, `{ outcome: "requested" }` if it is not (the request was posted and Windows may have refused it), and `WindowGone` if it closed in the interval. `requested` never means the window is in front. The legacy `hwnd` form returns no outcome.
    - *Move (`host.window.moveToDesktop`):* (a) validation as above. (b) A requested new desktop is created. (c) The window is re-filed, journal first if it will be hidden. (d) The hide runs on the effects thread. If the window closes before (c), the call returns `WindowGone` and removes the desktop it created, if that desktop is still empty and is not current. If it closes between (c) and the end of (d), the destroy observation removes it from the model and the hidden set, the effects thread treats a hide of a vanished window as done, the journal entry is rewritten without it, and the call returns `WindowGone` if the destruction was seen before the snapshot publishes, else success; the desktop it was filed on stays. After the call returns, a close is an ordinary destroy.
    - *Switch:* a window that closes anywhere in the plan is dropped from the plan, from the model and from the hidden set; it is never marked stuck. If the window that would have been shown last (the target's last active window) closes, the next window in the saved z-order takes its place.
    - *Cleanup invariant:* when any operation has returned, no hidden-set entry, journal entry or desktop created by that call names a window that is gone or a desktop nothing asked to keep.

**First-time notification setup (a flow of its own, run by Ben once).** The notifications plugin starts, and its worker reports whether it has package identity and whether access is `Allowed`. If either is missing, the plugin's popups show a **Set up** page, never an error: three steps with their current status (the machine trusts the certificate; the account allows this app under notification access; notifications are switched on), a command to copy for the one elevated step, and links to the matching Settings pages. Nothing is run for the user (I32). The page re-checks every few seconds while it is open.

## Distribution

Same channel as the first release: one zip on the GitHub release; the new plugins are built-in content, packaged by the same PackTool. Additions:

- **Notifications:** the zip carries the sparse package, its public certificate, and a script that registers it for the current user (the recipe in `tools/SubZeroDev.WindowManager.NotificationProbe`). The first run shows the guided setup above. Upgrade replaces the plugin folder; the package is re-registered only when its manifest changes.
- **Uninstall:** `--uninstall` also unregisters the sparse package for the current user. It does not remove the certificate from the machine's store and prints the command to do so.
- **Plugin SDK:** host API and protocol 1.1 are additive and ship in the same SDK package. A first-release plugin runs unchanged; one that uses a 1.1 feature declares `hostApi: "^1.1"`.
- **Replacement mode:** nothing new to register. Coexistence installs get the window model and the unchanged plugins and none of the new features.

## Failure modes

The first release's table stands. Rows added:

| Boundary | What fails | Detected by | System does | User sees | State left behind |
|---|---|---|---|---|---|
| Hiding windows | The host dies with windows hidden, before, during or after the hides of a switch | Guardian wait, or the next start finding the journal entry | Flow 12: show every listed window whose five-field fingerprint still matches | Every surviving window visible on one desktop | Journal cleared; desktop list kept |
| Hiding windows | A journal entry names a handle another window now holds | Fingerprint mismatch at recovery | Skip it; touch nothing | Nothing | Entry cleared |
| Window model | A Destroy event is missed and the handle is reused | The 2 s rescan, or the command-time fingerprint check | Destroy the old generation, admit the handle afresh | Nothing, or `WindowGone` for a command in the gap | None |
| Window model | A command carries a reference from an earlier host run, or a gone window | Epoch and generation check | `StaleWindowReference` or `WindowGone`; no side effect | A notice | None |
| Window model | A window closes during activate, move or switch | Destroy observation or the verification step | Flow 19 | At most a notice | None |
| Window model | `host.windows` and `host.desktops` are read at different revisions | The paired view | Hold up to 250 ms, re-read the lagging feed, else render from the lower revision and mark stale | Nothing, or a stale marker | None |
| Window model | WinEvent hooks cannot be installed | Hook call fails | Rescan only, every 2 s; log once | Slower updates | None |
| Window model | A view plugin is disabled or crashes | Supervisor | Model, feeds and commands continue; the plugin's own feed is unavailable; a restart resubscribes to the current snapshot | That view disappears or is replaced by the plugin's retry | None |
| Window model | A plugin built for 1.1 meets a 1.0 host | Compatibility resolution | `Incompatible`, `HostApiMismatch`; not started | The plugin manager shows the reason | None |
| Hiding windows | A window refuses to hide, is hung, or is elevated (a medium-integrity host cannot change a high-integrity window) | The window is still visible when the effects thread re-checks | Mark it `stuck`; leave it visible on every desktop; log `WindowNotMovable` | The window stays in view | None |
| Hiding windows | The journal write fails | Flush error | **Do not switch** (I6); raise `JournalWriteFailed` | A notice; the desktop does not change | None |
| Hiding windows | An application shows itself while hidden | WinEvent show on a window of another desktop | Hide it again; set `attention` | A flash on the desktop item | None |
| Hiding windows | A hidden window's process exits | Process handle or `IsWindow` | Remove it from the model and the hidden set | Nothing | Journal entry updated |
| Desktops | The last desktop is removed, or an id is unknown | Command validation | `LastDesktop` or `DesktopNotFound` | A notice | None |
| Desktops | Removing a desktop | — | Its windows move to the previous desktop, as Windows does | The windows appear there | None |
| Desktops | `desktops.json` invalid | Load validation | One desktop, "Desktop 1" | Settings banner | The user's file untouched |
| Notifications | Package identity missing (not registered, certificate untrusted, or the process started without it) | Worker `hello` reports `identity: none` | The plugin shows the Set up page | The page names the failed step | None |
| Notifications | Access `Denied`, or notifications switched off | Access status; an empty history with the switch off | The Set up page | Same | None |
| Notifications | Change events missed (they are not guaranteed) | A re-read on popup open and every 60 s finds new ids | Treat them as arrivals, without a pop-up for anything older than 30 s | The centre catches up | None |
| Notifications | Windows also draws its own pop-up | Seen in the first slice's gate | `popups` setting defaults to off if it does | Either one pop-up or none; never silently two | None |
| Notifications | A flood | More than `maxVisible` pending | Collapse into "N more"; the centre holds all | One summary pop-up | None |
| Notifications | The source application cannot be started | Activation error | Typed error to the caller | A notice in the centre | None |
| Notifications | Content in logs | — | The logs record counts, application ids and notification ids, never titles or bodies (I31) | Nothing | None |
| Quick settings | A device is absent or refuses | Capability probe at start; error on change | The tile is hidden (absent) or disabled with its reason (refused) | A tile that explains itself | None |
| Quick settings | A change did not stick | Re-read after the change | Show the device's real value | The control moves back | None |
| Thumbnails | The source window is gone, hidden or minimised | Registration or the properties query fails | Draw the icon and title card instead | A card | None |
| Taskbar badges | The host's `Shell_TrayWnd` receives a message it cannot decode | Size or format check | Ignore it, log once per application | No badge | None |
| Jump lists | A destination file is locked or corrupt | Read error | That application's jump list is empty | An empty list | None |
| Context menu | A shell-extension handler hangs or crashes | The worker's 3 s build timeout, or the worker faulting | The menu falls back to the plain verbs (Open, Delete, Properties); a crash takes only the worker (flow 3) | A shorter menu, or a brief placeholder | None |
| Wallpaper | A per-monitor image is missing, or the monitor id is not present | Load, or the monitor list | Fall back to `default`, then the system wallpaper | The fallback image | Preset untouched |
| Chords | No provider is running for a chord | The check at key-down | The key passes through | Windows' own behaviour, which is none without Explorer | None |

**Retry semantics** are unchanged: command invocations are never retried, and the Set up page re-checks passively. A failed notification re-read is retried by the next event or the 60 s resync.

## Concurrency and ordering

- **The window model has one writer: the UI thread.** The tracker, the hook and the backchannel only post observations. Generations are assigned there, in observation order. Readers get immutable snapshots, and both feeds are published from one snapshot, at most once per frame. The tracker is the only discovery source in the product; no view scans.
- **The window-effects thread is the only thread that hides or shows other processes' windows.** It uses the non-blocking forms of the calls, because a synchronous one can block on a hung window. It also owns the journal write for `hiddenWindows`, so the UI thread never awaits disk (I9). One plan runs at a time; a newer switch replaces any plan not yet started.
- **Switch order is fixed:** journal flush, then hides and shows, then the `current` change. The flush before the visible change is what makes recovery safe.
- **The model and the hook never share state.** The hook reads the chord table (replaced whole when settings change) and a set of command ids with a running provider (replaced whole by the supervisor). It answers within the system's hook timeout without calling into the UI thread.
- **Notifications run in their worker's single logical context.** A history re-read is serialised there; the change event only requests one. Two events close together cause one re-read.
- **Thumbnail registration, hover popup and element lifetime are on the UI thread**, and a registration is released in the element's unload.
- **Nothing in the second release is concurrent with the first release's journal for app bars**: both kinds share the same serialised journal writer.

## Alternatives considered

13. **Where window knowledge lives.** *Chosen (approach A):* one model in the host, published as a feed; running windows, Task View, the switcher and badges are views of it. *Rejected (B):* self-contained plugins. The window list would be scanned in several places, hiding would race with the strip's rescan, and grouping and jump lists would each re-derive window identity. *Lateral:* none. Windows' own flyouts need Explorer.
14. **What a virtual desktop is.** *Chosen:* the shell's own sets of windows. *Rejected:* Windows' own desktops. Their manager interface for other processes' windows is internal and changes between builds, and the service lives in Explorer, so none of it exists in replacement mode. The documented interface moves only a caller's own windows.
15. **How windows are kept off screen.** *Chosen:* hide them (non-blocking window-position calls), journalled. *Rejected:* cloaking, which Windows documents only for an application's own windows (to be checked from a shell-role process; open question 4). *Rejected:* moving windows off screen, because applications clamp, restore and re-place themselves, and their taskbar buttons stay. *Rejected:* minimising, which destroys the user's real minimised state.
16. **Task View previews of other desktops.** *Chosen:* live thumbnails for the current desktop, and a snapshot taken just before a window is hidden for the others. *Rejected:* live thumbnails everywhere, which a hidden window cannot give. *Rejected:* a titles-only view, which fails the "set of windows" purpose.
17. **How notifications reach the shell.** *Chosen:* a packaged worker reads Windows' history; the shell draws. *Rejected:* packaging the host (decision 7 stands, and identity would reach every plugin). *Rejected:* relying on Windows' pop-ups (they live in Explorer's shell experience; unverified without it). *Rejected:* capturing legacy balloon messages (only old tray applications use them). *Rejected:* undocumented notification-platform subscriptions (they break between builds).
18. **Scope of package identity.** *Chosen:* the notifications worker only. *Rejected:* every plugin or the guardian, which would drag the virtualised file and registry behaviour that decision 7 avoided.
19. **Hover thumbnails.** *Chosen:* DWM thumbnails in a host element. *Rejected:* screen-capture sessions, which polls, costs a GPU copy, and can show a capture border. *Rejected:* per-window bitmap grabs, which cannot show minimised windows and are slow.
20. **Taskbar badges.** *Chosen:* receive progress and overlay on the host's own `Shell_TrayWnd`, as applications send them to the shell, and broadcast `TaskbarButtonCreated` so applications start sending. *Rejected:* injecting into applications. *Rejected:* inferring progress from window titles.
21. **The desktop's file menu.** *Chosen:* the shell's own menu, built in the desktop-icons worker. *Rejected:* a hand-made menu of verbs, which loses every installed extension (archivers, Git). *Rejected:* building it in the host, because shell extensions are third-party code and would load into the process that owns the desktop (I1's reason).
22. **Win+X.** *Chosen:* read Windows' own WinX folders, falling back to a built-in list. This respects the user's customisations. *Rejected:* a fixed list only.
23. **Chords.** *Chosen:* extend the existing hook (first release decision 12). *Rejected:* `RegisterHotKey`, which Windows reserves for its own Windows-key chords.
24. **Jump lists.** *Chosen:* read the applications' own destination files. *Rejected:* keeping a shell-owned list of recents, which would show none of what applications record.
25. **What the registry holds.** *Chosen:* every admitted window for its whole life, with views filtering published facts. *Rejected:* a registry that is the visible running-windows list moved into the host, or one with a model-wide `eligible` flag: a window hidden for another desktop would drop out, hiding would be indistinguishable from destruction, and recovery could not tell what it hid. *Rejected:* tracking every top-level window including helpers that were never titled and visible, which costs events and memory for nothing any view shows.
26. **Window identity.** *Chosen:* a host-assigned generation, a fingerprint verified at rescan and before commands, and an epoch per host run, kept apart from `groupKey`. *Rejected:* handle, process id and process start time alone, because a process can destroy a window and create a successor at the same handle. *Rejected:* `groupKey` as identity, because it names an application, not a window. *Rejected:* persisting generations across a restart, because handles mean nothing after one.
27. **The `windows.running` contract.** *Chosen:* keep the feed id and the first release's fields, add fields, change nothing incompatibly, and derive it from the model in the plugin. *Rejected:* a new feed id, which breaks dependants for no gain when every change is additive. *Rejected:* two trackers during a transition, which could disagree.
28. **How the old tracker goes.** *Chosen:* one pull request that makes the host the tracker, moves the plugin onto the feed and deletes the scanner. *Rejected:* running both and comparing, which is two authorities. *Rejected:* building the model and leaving the scanner for a later slice, which leaves two scanners for as long as the gap lasts.
29. **Where the model is built.** *Chosen:* as its own first window slice, before virtual desktops. It is larger than a normal slice because choosing approach A makes desktop membership and window identity shared shell state, and the model, its public feed and commands, the compatibility adapter and the removal of the old tracker cannot be split without leaving two trackers or an unpublished contract. It does not move desktops ahead of notifications: S24 is notifications and S26 is desktops. *Rejected:* building desktops with a model of their own and extracting it later, which would migrate every consumer twice.

## Open questions

Marked **(Ben)** need a decision from Ben; the rest are checks the named slice runs, and a failing check is a decision entry, not a workaround.

1. **(Ben) Scope of the notification centre.** Default: it lists, opens the originating application (with its launch string), dismisses and clears. Toast buttons and inline replies are not available through Windows' listener interface. Is that "acted on"?
2. **(Ben) Which rows of `docs/replacement-mode.md` § *Features Explorer hosts* are in scope.** Default: Toasts, Notification centre (with the calendar flyout split off and left *Missing*), Quick Settings, Task View and virtual desktops, Win+X, and Taskbar. Start and Snap layouts are not named in the brief and stay as they are. Alt+Tab already degrades to a working classic switcher; the themed switcher is scheduled last and may be dropped.
3. **(Ben) The common toggles beyond Bluetooth and brightness.** Default: Wi-Fi, airplane mode, and do-not-disturb (the shell's own, which stops pop-ups; it does not change Windows' setting).
4. **(Ben) Jump lists: pin and unpin.** Default: read and launch only (recent, frequent, tasks and custom categories where readable); pinning is not in this release.
5. **(Ben) Who signs the notifications package.** Default: signed on the machine, as the probe is, with the key never leaving it.
6. **Check, first notifications slice:** do notifications reach the history with Explorer **not** running, and does Windows then show its own pop-ups too? Everything else in the design holds either way, except that an empty history makes notifications impossible.
7. **Check, first notifications slice:** does a worker created suspended and assigned to the job object (I3) keep its package identity? And does starting an application by AUMID with the notification's launch string reach it?
8. **Check, desktops slice:** can the shell-role process cloak other applications' windows? If so, live thumbnails of other desktops become possible.
9. **Check, taskbar slice:** does the host's `Shell_TrayWnd` receive progress and overlay once `TaskbarButtonCreated` is broadcast? If not, badges cannot ship and the design is reopened.
10. **Check, taskbar slice:** can jump-list tasks and custom categories be read without Explorer?
11. **Check, quick-settings slice:** radio control for an unpackaged process; DDC/CI on Ben's monitors; whether the machine has a built-in panel.
12. **Check, desktops slice:** DWM thumbnails inside a WinUI popup window: placement, clipping and DPI.
13. **Check, first window slice:** does the fingerprint plus the 2 s rescan keep wrong-window actions at zero under handle churn (a fixture that destroys and recreates windows at reused handles)?
14. **Check, first window slice:** does tracking every admitted window, not only the visible ones, stay inside the idle and event-rate budgets with hundreds of windows?
15. **Check, desktops slice:** does the form of show that neither activates nor restores keep a minimised window minimised on the supported Windows builds?

These three are engineering validation gates. They are not choices for Ben, and a failure is a decision entry.

**Acceptance checks.** Seven checks, planned requirements and not results. Each has a slice owner and an evidence method. *Automated* means the fakes and isolated fixtures of I38; *native* means a check against real windows that the test itself created, with a throwaway host instance and data root, never the user's windows, desktops, devices or settings, and reported separately.

| Check | Claim | Owner | Evidence |
|---|---|---|---|
| C1 Desktop consistency | After any desktop change, running windows and every switcher show the same completed switch and agree on each window's desktop; view-filter differences are not failures | S26 (S25 builds the paired view) | Automated: model and fake effects with two or more consumers, asserting equal revisions and desktop membership. Native: fixture windows across desktops |
| C2 Hidden windows | A window hidden for another desktop stays tracked, with its desktop, group and badges, and returns on switch back | S26 (S25 holds the rule: visibility is not destruction) | Automated: hide and show observations leave the generation unchanged. Native: a fixture window hidden by a real switch and found by `host.windows` |
| C3 Close during an operation | Flow 19: per-stage outcomes, `WindowGone` for a stale lifetime, a completed switch stays, no leftover hidden-set or journal entries or created desktops, and a posted activation never reported as foreground | S25 (activate), S26 (move, switch) | Automated: a fake effects port that closes the window at each stage. Native: closing a fixture window during a real switch |
| C4 Plugin independence | Disabling or crashing a view plugin leaves tracking, feeds and commands working; a restarted plugin resubscribes to the current snapshot without a tracker of its own | S25 (running windows), S26 (Task View) | Automated: supervisor kills and disables a plugin in a fake-host run and a second consumer keeps receiving revisions. Native: the same with the real worker |
| C5 Recovery | A host failure during journal write, hide or show, or part-way through a plan, is repaired by the guardian or the next start for every surviving window; restart membership is as stated (all on the first desktop); a stale or reused identity is never touched | S26 | Automated: journal replay against fakes at each crash point, including a reused handle. Native: kill a throwaway host with fixture windows hidden |
| C6 Published SDK interoperability | A plugin built separately against the packaged SDK consumes the feeds, invokes the commands and renders a `Thumbnail`; versions negotiate; stale references are rejected | S25 (feeds, commands, negotiation, stale rejection), S26 (`Thumbnail`) | Automated: a fixture plugin project that references only the packed SDK, run against a fake host. Native: the same plugin against a throwaway real host |
| C7 Budgets and isolation | The first release's startup, idle and interaction budgets hold with the model running; no test touches real notifications, desktops, devices or settings | every slice; S25 for the model | Automated: the S16 measurements re-run per slice with a test seam for event volume; fakes for every device. Native: budget runs only on throwaway instances |

*Answered or carried:* first-release question 7 (Explorer-hosted features) is superseded: the second release brings them in.

# Slices — SubZeroDev.WindowManager

Built from [10-design.md](10-design.md) and [20-contract.md](20-contract.md). The order follows risk. S1 to S3 are the brief's foundation prototype. They prove the three bets the design rests on first:
- a runtime-loaded XAML theme can style a WinUI 3 panel that behaves as an app bar;
- a separately built plugin process can drive that panel through the protocol;
- a failed worker leaves the host usable.

Each criterion runs as an automated test unless it says *manual*. A manual check is recorded in the slice's pull request with the observed result.

## S1 — Themed panel on the desktop
Status: done
Delivers: Ben can start the app and get a panel along the bottom of the main screen. Windows treats it like a second taskbar, its look comes from an editable theme file, and it disappears cleanly on quit.
Touches: solution and CI build; Guardian (launch and wait only); Host.App; Host.Core (`IConfigStore` read path for `settings.json`, layouts and presets; diagnostics log); Host.Platform (`IMonitorService`, `IAppBarService`, single-instance mutex); Host.Theming (theme load and validate, `Wm.Panel`, `Wm.Placeholder`); Host.Surfaces (panel window, placeholder); built-in theme `subzerodev.default`, layout `subzerodev.default`, preset `subzerodev.default`
Depends on: none
Acceptance:
  - S1.1 The build targets .NET 10, x64, Windows 11 22621 or later, with the Windows App SDK pinned to one exact stable version. CI runs `dotnet build` and `dotnet test` on `windows-latest` for every pull request, and the build fails when any warning is raised in host projects.
  - S1.2 Running the guardian exe starts exactly one host process, passing `--guardian-pid <its pid> --guardian-pipe <name>`. When the host exits with code 0, the guardian exits with code 0.
  - S1.3 Starting a second guardian while one runs exits with code 2, and the process count of each exe stays at 1.
  - S1.4 With no data root present, the host writes `settings.json` with `schemaVersion: 1` and `activePreset: "subzerodev.default"`. It then shows one panel on the primary monitor at the bottom edge, 40 DIP thick, behaviour `reserve`.
  - S1.5 While that panel is shown, the primary monitor's work area (`SPI_GETWORKAREA`) bottom equals the monitor bottom minus round(40 × scale) pixels, measured relative to the bottom it had before start. After a normal exit (`host.exit`, or closing through the tray-less test hook), the work area equals the pre-start value again.
  - S1.6 The panel's background brush comes from the theme's `Wm.Panel` style. Editing that style's background in the user copy of the theme and restarting the host changes the colour the test reads from the panel's root element.
  - S1.7 A theme whose XAML fails to parse is rejected with `ThemeErrorCode.XamlParse`, naming the file. The host still starts with the built-in fallback theme and logs one line naming the file to `logs\host-<date>.log`.
  - S1.8 A layout instance referencing a plugin that does not exist renders `Wm.Placeholder` in the `Missing` visual state.
  - S1.9 The foundation prototype checks (design open question 5) are run *manually* on the development machine, and the pull request records each as pass or fail: a runtime-loaded control template with visual states, a transparent panel with a backdrop, and the app-bar registration above. A fail is recorded in `90-decisions.md` before the slice merges.
Out of scope: plugin processes, palettes and asset packs, theme switching at runtime, the recovery journal and crash recovery, multiple monitors, settings UI.

## S2 — A separately built plugin draws on the panel
Status: done
Delivers: A plugin author can build a small clock as its own program, drop it into the plugins folder, and see it live on Ben's panel, without the host being rebuilt.
Touches: Protocol (messages, `ViewNode`, `ViewValidator`, manifest model); Sdk (`PluginRuntime`, `Plugin`, `ContributionInstance`, `Surface`, `View`, `Prop<T>`); Host.Core (`IPackageCatalog` discovery of built-in and user plugins, manifest validation, `IPluginSupervisor` start and stop, `IWorkerChannel`, plugin logs); Host.Platform (`IWorkerLauncher` with job object, pipe creation); Host.Surfaces (rendering Text, Stack, Icon (named only), Spacer, Separator; bindings; revisions); plugin `subzerodev.clock` (time only, no settings yet)
Depends on: S1
Acceptance:
  - S2.1 The clock plugin project references only the Sdk project, and an architecture test fails the build if any plugin project references a `Host.*` or `Guardian` project (I2). An architecture test also fails if any host project loads an assembly from a plugin folder (I1).
  - S2.2 With the clock package in `content\plugins\subzerodev.clock\` and an instance of `clock` placed in the default layout's end zone, the panel shows the current local time as `HH:mm` within 3 s of host start, and the shown minute changes within 2 s of the system minute changing.
  - S2.3 The worker is in the host's job object. Killing the host process with `TerminateProcess` causes the worker process to exit within 2 s (I3).
  - S2.4 A connection whose first message is not `hello`, or whose `hello` token differs from the launch token, is closed with `1010 Unauthorised`, and no other message from it is processed (I5).
  - S2.5 A `view.set` containing a duplicate element id, an unknown kind, or a prop named `background`, `foreground`, `fontSize` or `fontFamily` is rejected with `1005 InvalidView`. The previously shown view stays, and the issue appears in `logs\plugins\subzerodev.clock-<date>.log` (I11).
  - S2.6 A `state.patch` with a revision lower than the surface's current view revision has no effect on the rendered text (I12).
  - S2.7 Copying the same clock package with id `example.clock` into the user `plugins` folder while the host runs makes it discoverable without a restart, and placing it through a hand-edited layout file shows a second clock within 5 s of the file save.
  - S2.8 On normal exit, every worker receives `shutdown` and exits with code 0 within 3 s. A worker that does not exit is killed at 3 s.
Out of scope: heartbeats, fault handling and restart; commands and actions; feeds; plugin settings; popups; the PackTool zip format (S11).

## S3 — A crashed plugin does not take the desktop down
Status: done
Delivers: When a plugin crashes or hangs, Ben's desktop keeps working, shows which piece failed, and brings it back by itself. If it keeps failing, Ben can retry it.
Touches: Host.Core (`IPluginSupervisor` fault detection, heartbeat, backoff, restart budget, `RetryAsync`; `PluginStatus`; `FaultReason`); Host.Surfaces (`Wm.Placeholder` reasons Starting, Faulted, Unavailable with Retry); Sdk (ping reply off the callback context); a `samples.faulty` test plugin with crash, hang and flood switches
Depends on: S2
Acceptance:
  - S3.1 When the faulty plugin's process exits unexpectedly, its instance shows the `Faulted` placeholder within 1 s, and the clock instance on the same panel keeps updating.
  - S3.2 A worker that stops answering `ping` is marked `Faulted` with `HeartbeatTimeout` between 10 s and 15 s after its last reply, and its process is terminated.
  - S3.3 After a fault, the supervisor restarts the worker after 1 s, then 5 s, then 30 s for successive faults. Each restart replays `instance.create`, and the live view returns.
  - S3.4 The 4th fault within 300 s sets the plugin to `Unavailable` with no further automatic start (I17). Invoking the placeholder's Retry starts it again and resets the fault history.
  - S3.5 Throughout S3.1–S3.4, the host's UI thread never stalls for more than 50 ms, as measured by the debug dispatcher watchdog (I9), and a scripted click on another panel element is processed within 200 ms.
  - S3.6 Never more than one process for the faulty plugin id exists at any moment during restarts, sampled every 50 ms (I4).
  - S3.7 A worker sending more than the configured message rate for 10 s is faulted with `Flood`. Shorter bursts are coalesced latest-wins per state key, and the final rendered value equals the last value sent.
Out of scope: guardian recovery of the host, the plugin manager UI (S10), disabling plugins.

## S4 — The desktop is restored even when the app dies
Status: done
Delivers: If the app is killed or crashes, Ben gets the normal Windows desktop back without the panel's reserved space left behind, and the app comes back on its own.
Touches: Recovery (`Journal`, `Reverter`, `JournalDocument`); Host.Core (`IRecoveryJournal`); Host.Platform (`IAppBarService` journal-first registration); Guardian (stale-journal recovery, host watch and ping, relaunch budget, `--attach`, `--recover-only`, crash records); Host.App (guardian link, relaunching the guardian)
Depends on: S1
Acceptance:
  - S4.1 The `appBar` journal entry is on disk (read back by a separate process) before `ABM_NEW` is sent, and it is removed only after `ABM_REMOVE` returns (I6).
  - S4.2 After `TerminateProcess` on the host, the guardian reverts the journal and the primary work area equals the pre-start value within 5 s. A crash record exists in `logs\crash\`, and a new host process runs within 10 s.
  - S4.3 After a normal exit, the journal holds zero entries and `cleanExit: true` (I7).
  - S4.4 With a hand-written stale journal (dead `hostPid`) present, `--recover-only` reverts it, leaves the journal empty, and exits 0 without starting a host.
  - S4.5 Killing the host 4 times within 300 s leaves the guardian idle with the desktop restored, and the guardian exits with code 3 once its message is dismissed (I17).
  - S4.6 A host that does not answer the guardian's ping for 30 s is terminated and handled as in S4.2.
  - S4.7 Killing the guardian leaves the host running. Within 5 s, a new guardian runs with `--attach <hostPid>`.
  - S4.8 An unreadable journal is renamed with the suffix `.corrupt`, logged, and startup continues (`JournalUnreadable`).
  - S4.9 When a journal write fails (simulated read-only file), the app bar is not registered, and the panel shows as `overlay` with a diagnostic (`JournalWriteFailed`).
Out of scope: Explorer-restart re-registration (S12), `workArea` changes (replacement mode), sign-in registration (S15).

## S5 — Change the look while it runs
Status: done
Delivers: Ben can switch theme, colour palette or preset while the desktop is running, and every panel and plugin item restyles at once, with no plugin restarting.
Touches: Host.Core (`IConfigStore` write path, watcher, conflict detection, `.lastgood`); Host.Theming (`IThemeEngine.ApplyAsync`, palettes, `Wm.Color.*`/`Wm.Brush.*`, `Wm.Element.<Kind>[.<class>]` resolution, `panelBackdrop`, light and dark palette pairs); Host.Surfaces (re-templating from retained views); `host.preset.apply`; built-in palettes `subzerodev.light`, `subzerodev.dark`
Depends on: S2
Acceptance:
  - S5.1 Changing `activePreset` in `settings.json` to a preset with a different theme restyles every panel within 1 s of the file save, and no worker receives any message caused by the change.
  - S5.2 Replacing a palette's `accent` value changes the colour read from every element using `Wm.Brush.accent`. Elements using other tokens keep their colours.
  - S5.3 A token the theme declares but the palette lacks takes the theme's default colour.
  - S5.4 Applying a theme with a missing required key returns `ApplyResult.Applied == false` with `MissingRequiredKey`, and every surface keeps the previous dictionary (I13).
  - S5.5 An element with classes `["primary","compact"]` is styled by `Wm.Element.Text.primary` when it exists, else `Wm.Element.Text.compact`, else `Wm.Element.Text`.
  - S5.6 A preset with `palette: { light, dark }` uses the dark palette within 2 s of Windows switching to dark app mode.
  - S5.7 A `settings.json` made invalid by hand is not overwritten by any host write. The host keeps running on the last good copy, the store reports `DocumentHealth.Invalid`, and writes to it return `DocumentReadOnly` (I14).
  - S5.8 A host write whose `basedOnHash` no longer matches the disk returns `Conflict`, and the file content equals the external edit.
Out of scope: asset packs and icons (S13), the appearance picker UI (S10), the two sample configurations (S14).

## S6 — Clicks and hotkeys run commands
Status: done
Delivers: Plugin items become interactive: clicking a button or pressing a global shortcut runs a command in the right plugin, and a window the plugin brings forward actually comes to the front.
Touches: Protocol (`command.invoke`, `element.event`, actions); Sdk (`ICommands`, `OnEventAsync`); Host.Core (`ICommandRouter`, `commandProviders`, `hotkeys`); Host.Platform (`IHotkeyService`, `KeyChord`, `IForegroundService`); Host.Surfaces (Button and Toggle; action dispatch; notices via `Wm.Notice`; keyboard focus and navigation inside a panel); built-in `host.*` commands except `host.settings.open`, `host.plugin.debugAttach` and `host.preset.apply`
Depends on: S3
Acceptance:
  - S6.1 Clicking a Button with `{invoke:{command:"samples.echo"}}` delivers exactly one `command.invoke` to the provider with `source: pointer`. The worker received foreground rights (`AllowSetForegroundWindow` for its pid) before the message was written (I18).
  - S6.2 A command whose provider is not `Running` returns `1001 PluginUnavailable`, and a `Wm.Notice` appears next to the element. The command is not sent again (I19).
  - S6.3 A command taking longer than 5 s returns `1003 Timeout` to the caller.
  - S6.4 Two plugins declaring the same command: the provider is the one in `commandProviders`, else the lower plugin id by ordinal comparison, which is then written to `commandProviders` (I15).
  - S6.5 `KeyChord.Parse("Ctrl+Alt+Space")` yields Ctrl|Alt with VK_SPACE. `"Ctrl+Foo"` fails. A chord already registered by another process yields `HotkeyInUse`, and the command stays unbound for the session.
  - S6.6 Pressing a bound hotkey invokes its command with `source: hotkey`.
  - S6.7 `host.window.activate { hwnd }` makes the given window the foreground window, restoring it when minimised. A destroyed hwnd returns `WindowGone`.
  - S6.8 With a panel focused, Tab and Shift+Tab move focus across its interactive elements in visual order, and Enter or Space invokes the focused Button.
Out of scope: popups and text input (S8), the settings window (S10), feeds (S7).

## S7 — See and switch running windows
Status: done
Delivers: Ben gets a running-windows strip on the panel that lists open application windows with their icons and switches to one when it is clicked.
Touches: Protocol (`feed.publish`, `feed.update`); Sdk (`IFeeds`); Host.Core (`IFeedBroker`); Host.Platform (`IIconService`); Host.Theming (`IconResolver` for `app` and `window` icon refs, host default fallback); Host.Surfaces (List with item templates, `WmFitPanel`, `$item` bindings, Button `active` and `attention` states); plugin `subzerodev.running-windows` (feed `windows.running`, commands `windows.activate`, `windows.minimize`, contribution `windows` with `scope`)
Depends on: S6
Acceptance:
  - S7.1 Opening a top-level test window adds an item titled with its window title within 1 s. Closing it removes the item within 1 s.
  - S7.2 The list contains only windows that are visible, unowned, not tool windows, not cloaked, and have a non-empty title. A test fixture with one window of each excluded type shows none of them.
  - S7.3 Clicking an item brings that window to the foreground, restoring it when minimised. Clicking the item of the current foreground window minimises it.
  - S7.4 The item for the foreground window has `active: true`, and its Button is in the `Active` visual state.
  - S7.5 Each item shows the application icon from `IIconService`. A window whose icon cannot be extracted shows the host default icon.
  - S7.6 With `scope: "thisMonitor"`, an instance lists only windows whose monitor id equals its panel's monitor.
  - S7.7 Killing the running-windows worker sends `feed.update { available: false }` to subscribers, and the instance shows the `Faulted` placeholder.
Out of scope: grouping, thumbnails, drag and drop, jump lists, virtual desktops.

## S8 — Launch applications by search
Status: done
Delivers: Ben can press a shortcut or click the launcher button, type part of an application's name, and start it with Enter.
Touches: Protocol (`popup.*`); Sdk (`IPopups`, `PopupSurface`); Host.Surfaces (popup windows, `Wm.Popup`, light dismiss, TextInput with `navigates`, List selection); plugin `subzerodev.launcher` (popup `search`, commands `launcher.open`, `launcher.close`, `applications.launch`, feed `applications.index`, contribution `launcher`)
Depends on: S7
Acceptance:
  - S8.1 Pressing Ctrl+Alt+Space opens the search popup on the monitor under the pointer, with the text input focused, within 300 ms.
  - S8.2 The index contains every `.lnk` in the per-user and all-users Start-menu Programs folders whose target exists, plus the packaged apps listed by the shell's AppsFolder. A fixture shortcut appears in the index.
  - S8.3 Typing `note` lists entries whose name contains `note` (case-insensitive), with prefix matches first. The list updates no sooner than 150 ms after the last keystroke.
  - S8.4 Up and Down move the selection, and Enter launches the selected entry through `applications.launch`. The launched process becomes the foreground window and the popup closes.
  - S8.5 Escape or clicking outside closes the popup. The worker receives `popup.closed` with reason `Escape` or `LightDismiss`.
  - S8.6 Clicking the launcher panel item opens the same popup anchored to that item.
  - S8.7 `applications.launch` with a missing exe returns `1009 CommandFailed` with `retryable: false`, and a notice shows in the popup.
Out of scope: recent items, file search, web search, calculator, pinned favourites.

## S9 — Settings that survive restart
Status: done
Delivers: Ben can change how a plugin item behaves, for example the clock's format, and the change sticks across restarts and plugin updates.
Touches: Protocol (`settings.migrate`, `settings.save`, `instance.settingsChanged`, `plugin.settingsChanged`); Sdk (`MigrateSettingsAsync`, `SaveSettingsAsync`); Host.Core (settings schema subset validation, plugin-settings documents, migration and backup); clock settings `format` and `showDate`
Depends on: S6
Acceptance:
  - S9.1 Editing the clock instance's `settings.format` to `HH:mm:ss` in the layout file updates the shown text to include seconds within 2 s, with no worker restart.
  - S9.2 Settings saved through `SaveSettingsAsync` are present after a host restart.
  - S9.3 A `settings.save` failing the schema returns `1008 SettingsInvalid` and leaves the file unchanged.
  - S9.4 Plugin settings stored at `settingsVersion: 1`, read by a plugin declaring `settingsVersion: 2`, are migrated through `settings.migrate`. The prior file is kept as `subzerodev.clock.v1.bak`, and the new file has version 2.
  - S9.5 A migration that throws starts the plugin with defaults in memory and leaves the stored file byte-identical.
  - S9.6 A manifest using a schema keyword outside the subset (for example `oneOf`) makes the plugin `Invalid` with the keyword named.
Out of scope: generated settings forms (S10).

## S10 — Settings window and plugin manager
Status: done
Delivers: Ben can open one settings window to see every plugin and its health, enable, disable, retry and configure plugins, read their logs, and pick a preset, with no file editing.
Touches: Host.Settings (window, plugin manager, schema-generated forms, appearance picker); Host.Core (`EnableAsync`, `DisableAsync`, status and problem reporting, `Incompatible` and `Invalid` reasons); `host.settings.open`, `host.preset.apply`
Depends on: S5, S9
Acceptance:
  - S10.1 `host.settings.open { page: "plugins" }` shows every discovered plugin with its id, version, source (built-in or user) and state. An `Invalid` or `Incompatible` plugin shows each problem's code and detail.
  - S10.2 Disabling a running plugin stops its worker within 3 s, persists `enabled: false`, and shows the `Disabled` placeholder on its instances. Enabling reverses this, and the state survives a restart.
  - S10.3 A plugin whose `hostApi` is `^2.0` shows `Incompatible: HostApiMismatch` and never starts (I16).
  - S10.4 The settings form for a contribution instance is generated from its `settingsSchema`: string enums as choice lists, booleans as toggles, integers with min and max as number boxes. Saving writes the layout file through `IConfigStore`.
  - S10.5 Selecting a preset in the appearance page applies it as in S5.1 and persists `activePreset`.
  - S10.6 The plugin detail view shows the last 200 lines of that plugin's log and its recent fault reasons and times.
  - S10.7 Every control in the settings window is reachable by keyboard and has an accessible name (checked with UI Automation).
Out of scope: package install and removal (S11), layout editing (S11).

## S11 — Install, place and remove plugins
Status: done
Delivers: A plugin author can hand Ben a package file, and Ben can install it, add its items to a panel, rearrange or remove them, and remove the plugin, all from settings and without restarting.
Touches: PackTool (`wm-pack`); Host.Core (`IPackageCatalog.InstallAsync`/`RemoveAsync`, staging, upgrade with rollback, user override of built-in ids); Host.Settings (install, remove, layout editor: add, move and remove instances across zones and panels; add and remove panels)
Depends on: S10
Acceptance:
  - S11.1 `wm-pack bin\Release --manifest plugin.json --out x.zip` exits 0 and produces a zip whose root holds `plugin.json` and the entry exe. A missing entry exe exits 2. An invalid manifest exits 1 with the failing field named.
  - S11.2 Installing a valid zip from settings puts it in the user `plugins` folder and lists it as `Disabled` (user-installed default) within 2 s, with no host restart.
  - S11.3 A zip containing a path that escapes its root (`..\`) is rejected with `PackageInvalid`, and `staging\` is empty afterwards.
  - S11.4 Upgrading an installed plugin while it runs stops it, swaps the folder and restarts it. If the swap fails (simulated locked file), the previous version is in place and running.
  - S11.5 Adding a contribution to a panel zone in the layout editor shows it on the panel within 2 s, and the layout file contains the new instance with a fresh GUID.
  - S11.6 Removing an instance sends `instance.destroy` to its worker and removes it from the layout file.
  - S11.7 Removing a user plugin stops it, deletes its package folder, leaves `plugin-data\<id>\` unless the user ticked "delete data", and shows the `Missing` placeholder for any instance still placed. Removing a built-in plugin returns `PackageInUse`.
Out of scope: a marketplace or download from URLs; drag and drop between monitors (keyboard and buttons only are required).

## S12 — Monitors, DPI, full screen and Explorer restarts
Status: done
Delivers: Ben's panels follow the screens: they appear on the right monitors at the right size, step aside for full-screen games and presentations, and come back after Explorer restarts.
Touches: Host.Platform (`IMonitorService` topology and stable ids, DPI, `FullscreenChanged`, `ShellRestarted`); Host.Surfaces (per-monitor panels, `monitor: all | {device}`, `overlay`, `autoHide`, `desktop` behaviours, z-order); `docs/test-matrix.md`
Depends on: S4
Acceptance:
  - S12.1 A panel with `monitor: "all"` shows on every connected monitor. Disconnecting a monitor (or disabling it through DisplayConfig in the test) removes its panel and app bar within 2 s. Reconnecting restores them.
  - S12.2 Moving a panel between monitors at 100 % and 150 % scale keeps its thickness at 40 DIP, which is 40 px and 60 px.
  - S12.3 When a full-screen window is foreground on a monitor, panels on that monitor are not topmost within 1 s, and become topmost again within 1 s after it leaves.
  - S12.4 After `explorer.exe` is restarted, every `reserve` panel is registered again within 5 s, and the work area is reduced again.
  - S12.5 An `autoHide` panel collapses to 2 px at its edge when the pointer leaves it for 1 s, and expands within 200 ms of the pointer touching that edge.
  - S12.6 A `desktop` panel stays below every normal window after the user clicks the desktop (Win+D included).
  - S12.7 `docs/test-matrix.md` lists each monitor, DPI and full-screen scenario with its result on the available hardware, covering at least one monitor at 100 % and 150 % plus (when installed) a virtual second display. All rows pass, *manual*.
Out of scope: hiding Explorer's taskbar (open question 1), per-monitor layouts that differ beyond the `monitor` selector.

## S13 — Icon packs
Status: done
Delivers: Ben can install an icon pack and see the panel and plugin icons change everywhere at once, including application icons chosen for override.
Touches: Host.Theming (asset pack loading, `icons` and `appIcons`, the fallback chain asset packs → theme bundle → host default → system icon); Host.Core (asset pack catalog); preset `assetPacks`
Depends on: S7, S5
Acceptance:
  - S13.1 A `named` icon present in the first asset pack of the chain is shown from that pack. When it is absent there but present in the second, the second is used. When it is absent in all, the theme bundle's icon is used, then the host default.
  - S13.2 An `appIcons` entry matching `notepad.exe` replaces Notepad's icon in the running-windows list within 1 s of the preset change.
  - S13.3 An asset pack whose image path escapes its folder is `Invalid` with `PathEscape`, and the chain skips it.
  - S13.4 Changing the preset's `assetPacks` sends no message to any worker.
Out of scope: wallpapers, icon pack authoring tools.

## S14 — Two distinct sample looks
Status: done
Delivers: Ben can flip between two very different desktop looks, using the same clock, launcher and window list, to show what the theming can do.
Touches: content only — two themes, palettes, asset packs and presets (`subzerodev.sample-glass`, `subzerodev.sample-retro`), plus screenshots in `docs/`
Depends on: S8, S13
Acceptance:
  - S14.1 Both presets pass `IThemeEngine.ValidateAsync` with zero errors.
  - S14.2 Between the two presets, at least these differ: panel edge or shape, backdrop, typography (font family), palette accent, the visual-state transitions of `Wm.Element.Button`, and the icon pack.
  - S14.3 Switching between them changes no file in `plugin-settings\` and restarts no worker.
  - S14.4 `docs/samples.md` holds one screenshot per preset, taken on the same layout and the same open windows, *manual*.
Out of scope: a theme editor.

## S15 — Release, SDK package and a third-party sample
Status: done
Delivers: Another developer can download the SDK, build a plugin in their own project, package it, and install it into a released build of the app, and Ben can install that release on a clean machine without admin rights.
Touches: CI release workflow; Sdk and Protocol NuGet package; PackTool .NET tool; a `samples/` plugin solution consuming only the package from a local feed; Guardian `--first-run`, `--uninstall`, Run key; install layout
Depends on: S11
Acceptance:
  - S15.1 A tag `v*` produces a GitHub release with the app zip, `SubZeroDev.WindowManager.Sdk.<ver>.nupkg`, and the `wm-pack` tool package.
  - S15.2 In CI, the `samples/` plugin builds from a local feed holding only the release's packages, with no project reference into `src/`, and `wm-pack` packages it.
  - S15.3 In CI, the released host on `windows-latest` installs the sample package through the same install path as S11.2. After it is enabled, the plugin reaches `Running` (acceptance criterion 1).
  - S15.4 The first-party plugins in the release zip are byte-identical to the output of `wm-pack` run on their build output (acceptance criterion 2).
  - S15.5 `--first-run` creates the Start-menu shortcut and, with `startAtSignIn: true`, the `HKCU\...\Run` value. `--uninstall` removes both and leaves the data folder. Neither step requires elevation.
Out of scope: nuget.org publication (open question 3), auto-update, MSIX.

## S16 — Performance budgets
Status: done
Delivers: Ben has measured numbers for how fast the desktop starts, how little it uses at rest, and how quickly it reacts, plus a check that fails when a change makes them worse.
Touches: a benchmark harness under `tests/`; CI job; `docs/budgets.md`; `90-decisions.md` entry with the chosen budgets
Depends on: S12, S14
Acceptance:
  - S16.1 The harness measures, over 10 runs on the reference machine: time from guardian start to every first-party instance showing its live view, the host's and each worker's private bytes after 60 s idle, the host's CPU over 60 s idle, the hotkey-to-popup-visible latency, and the click-to-foreground latency.
  - S16.2 `docs/budgets.md` and a decision entry set each budget at no more than the measured 90th percentile plus 20 %.
  - S16.3 A CI job runs the startup and idle measurements on every pull request and fails when one exceeds its budget by more than 10 %.
Out of scope: optimisation work beyond meeting the budgets.

## S17 — Run as the Windows shell, and always get back to Explorer
Status: done
Delivers: Ben can make the app his Windows shell with one command and no admin rights, sign in to it instead of Explorer, and always get Explorer back: by command, from Settings, by holding Shift at sign-in, or automatically when the host keeps failing.
Touches: Guardian (`--shell on|off|status`, `--mode replacement`, `--take-over`, `shell.json`, Explorer fallback); Host.App (`--mode`, host mode to workers); Host.Core (mode filtering per I16, `host.shell.restoreExplorer`, `NotInReplacementMode`); Recovery (`workArea` revert); Host.Settings (a "Use as my shell" switch); `docs/replacement-mode.md`
Depends on: S15, S4
Acceptance:
  - S17.1 Against a test registry root, `--shell on` records the previous per-user `Winlogon\Shell` value (or its absence) in `shell.json`, then writes `"<install>\SubZeroDev.WindowManager.exe" --mode replacement`. `--shell off` restores the previous state exactly, absence included (I22). Neither needs elevation.
  - S17.2 With a Custom User Interface policy value present in the test root, `--shell on` exits 5, names the value, and writes nothing.
  - S17.3 The guardian started with `--mode replacement` starts the host with `--mode replacement`, and every worker receives `--wm-mode replacement`. A plugin whose `modes` lacks `replacement`, or with `requiresExplorer: true`, is `Incompatible` with `ModeUnsupported`.
  - S17.4 In replacement mode, when the host fails `maxRestarts + 1` times within the window, the guardian starts `explorer.exe` (a recording launcher in the test) before it shows its message (I23). With Shift reported held at start, it starts `explorer.exe` and never starts the host.
  - S17.5 `--mode replacement` while another process owns the shell window exits 6. With `--take-over`, it asks Explorer to exit (a recorded request in the test) and continues.
  - S17.6 In replacement mode, `host.shell.restoreExplorer` exits the host normally (journal empty) and starts `explorer.exe`; with `unregister: true` it also restores the registry as `--shell off` does. In coexistence mode it returns `NotInReplacementMode`.
  - S17.7 On Ben's machine (Windows 11 Enterprise 26200), after `--shell on` and signing out and in, the guardian runs and `explorer.exe` does not; `--shell off` and another sign-in bring Explorer back, *manual*. If the per-user value is not honoured, switching to the fallback in design open question 6 is a decision entry.
Out of scope: the shell roles (S18) and the notification area (S19); registration for other accounts.

## S18 — Shell roles and session start-up
Status: done
Delivers: Signed in to the app as his shell, Ben gets his start-up programs, windows report themselves to the running-windows list, minimised windows go to the panel instead of piling up on the screen, and File Explorer folder windows open as ordinary windows.
Touches: Host.Platform (`IShellRoleService`, `IStartupRunner`, replacement `IAppBarService` that sets the work area, `ARW_HIDE`); Host.App (replacement start order); Host.Core (`host.files.open`); plugin `subzerodev.running-windows` (shell-hook events in replacement mode); `docs/replacement-mode.md`
Depends on: S17, S7
Acceptance:
  - S18.1 In replacement mode with no other shell window, the host's desktop window is `GetShellWindow()`. Its hook window receives window created, destroyed, activated and flashing messages for a test window, and a flashing window sets `attention` on its running-windows button within 500 ms.
  - S18.2 With another process holding the shell window, the host logs `ShellRoleTaken`, runs without the roles, and every other feature still starts. Later slices test in this state.
  - S18.3 After the first panel shows, the host signals `ShellDesktopSwitchEvent` and sends `WM_USER` to the desktop window, once.
  - S18.4 Against a test registry root and test Startup folders, `IStartupRunner` runs HKLM RunOnce, RunOnceEx, HKLM Run, HKCU Run, the common Startup folder, the user Startup folder, then HKCU RunOnce, in that order. It deletes a RunOnce value before running it, or after a successful start when the name begins with `!`, and leaves an HKLM value it cannot delete alone and logged. It skips a source disabled by its `Explorer` policy value (`DisableLocalMachineRun`, `DisableCurrentUserRun`, `DisableLocalMachineRunOnce`, `DisableCurrentUserRunOnce`). A second host start in the same session runs nothing (I24).
  - S18.5 In replacement mode a `reserve` panel sets the work area itself (`SPI_SETWORKAREA`), journalled as `workArea` before it is applied (I6). After the host is killed, the guardian restores the original work area.
  - S18.6 In replacement mode, minimised windows are hidden (`ARW_HIDE` in `SPI_GETMINIMIZEDMETRICS`).
  - S18.7 `host.files.open { path }` starts `explorer.exe` with that path. On the replacement session this opens a folder window and no second shell, *manual*.
Out of scope: other applications' app bars (S19); session commands (S21).

## S19 — The notification area and other applications' app bars
Status: done
Delivers: In replacement mode, the icons applications put in the notification area (chat, cloud sync, VPN, updaters) appear in Ben's panel, react to clicks as they do on the taskbar, and show their tooltips and balloons. Docked toolbars from other applications still get their screen edge.
Touches: Host.Platform (`ITrayHost`: `Shell_TrayWnd` and `TrayNotifyWnd`, `WM_COPYDATA` for `Shell_NotifyIcon` versions 0 to 4 and `SHAppBarMessage`, the `TaskbarCreated` broadcast); Host.Core (`host.tray.icons` feed, `host.tray.invoke`); Host.Theming (`IconRef.Tray`); plugin `subzerodev.tray`; a test tray client under `tests/`
Depends on: S18, S6
Acceptance:
  - S19.1 A test client sending `NIM_ADD`, `NIM_MODIFY` (icon, tooltip) and `NIM_DELETE` to the host's `Shell_TrayWnd` produces the matching `host.tray.icons` entries within 250 ms. The tray item shows one button per visible icon, with its icon and tooltip.
  - S19.2 For a client on `NOTIFYICON_VERSION_4`, a click on its button sends `NIN_SELECT` and a right click sends `WM_CONTEXTMENU`, with the anchor coordinates packed as version 4 requires. A version 0 client receives `WM_LBUTTONUP` and `WM_RBUTTONUP`. The owner process gets foreground rights first (I18).
  - S19.3 A `NIF_INFO` balloon shows as a themed notice from the tray item and goes after its timeout.
  - S19.4 A malformed or truncated `WM_COPYDATA` payload is ignored and logged. The icons of a destroyed owner window disappear within 2 s.
  - S19.5 On host start in replacement mode, `TaskbarCreated` is broadcast, and the test client's re-added icon appears.
  - S19.6 A test app bar sending `ABM_NEW`, `ABM_QUERYPOS` and `ABM_SETPOS` for the top edge receives a rectangle that does not overlap the host's panels, and the work area shrinks by its height. `ABM_REMOVE` gives the space back. `ABM_GETTASKBARPOS` returns the primary panel's rectangle, and `ABM_GETSTATE` reflects its `autoHide`.
  - S19.7 In coexistence mode the host creates no `Shell_TrayWnd`, and the tray plugin is `Incompatible`.
Out of scope: legacy Shell Service Objects (`ShellServiceObjectDelayLoad`); toast notifications and the notification centre; `ITaskbarList` progress and overlays.

## S20 — Desktop, wallpaper and icons
Status: todo
Delivers: In replacement mode Ben's desktop shows his preset's wallpaper and the icons from his Desktop folders, which open on double-click, and Win+D brings it forward.
Touches: Host.Surfaces (the desktop surface, `host.desktop.show`); Host.Theming (preset `wallpaper`, asset-pack images); plugin `subzerodev.desktop-icons`
Depends on: S18, S13
Acceptance:
  - S20.1 In replacement mode the desktop surface covers every monitor at the bottom of the z-order and draws the preset's `wallpaper` with its `fit`. Without one, it draws the system wallpaper (`SPI_GETDESKWALLPAPER`). Switching presets changes it within 1 s.
  - S20.2 The desktop-icons gadget lists the user's and the public Desktop folders, merged and sorted by name. A file added to either appears within 2 s, and a deleted one disappears.
  - S20.3 Double-clicking an icon, or pressing Enter on it, opens it as the shell would (`ShellExecute` with the `open` verb), with foreground rights granted to the started process.
  - S20.4 `host.desktop.show` minimises every normal window, and a second call restores the same windows.
  - S20.5 In coexistence mode the desktop-icons gadget works on the `desktop` layer over Explorer's desktop.
Out of scope: placing icons by dragging (the view model has no drag); shell context menus on icons.

## S21 — Windows key and session commands
Status: todo
Delivers: In replacement mode the Windows key opens Ben's launcher, the usual Windows-key shortcuts still work, and he can lock, sign out, restart, shut down or sleep from the panel.
Touches: Host.Platform (`IWindowsKeyHook` on its own thread, `ISessionService`); Host.Core (`host.session.*`, default bindings); plugin `subzerodev.launcher` (`run` mode); plugin `subzerodev.session`
Depends on: S18, S8
Acceptance:
  - S21.1 In replacement mode, a lone Windows-key press and release, with no other key between, invokes `launcher.open` with `source: hotkey`, and so does Ctrl+Esc. Holding Windows with another key does not.
  - S21.2 Win+R invokes `launcher.open { mode: "run" }`, Win+E `host.files.open`, Win+D `host.desktop.show`, and Win+M minimises every window. Win+L still locks, because Windows handles it.
  - S21.3 The hook answers each key event from its own thread. With the UI thread blocked for 2 s, keys typed into another application are neither delayed nor dropped.
  - S21.4 `host.session.lock`, `signOut`, `restart`, `shutdown` and `sleep` call the matching `ISessionService` method (a recording fake in tests). A refusal returns `SessionCommandRefused`.
  - S21.5 The `power` item opens a menu with the five commands, reachable by keyboard. Sign out, restart and shut down ask for confirmation first.
  - S21.6 On the replacement session, each of the five commands works from the menu, *manual*.
  - S21.7 In coexistence mode the hook is not installed.
Out of scope: Task View and virtual-desktop keys (Win+Tab, Win+Ctrl+arrows); the Win+X menu.

## S22 — System indicators
Status: todo
Delivers: In replacement mode, where Windows' own volume, network and battery icons are gone, Ben sees and controls them from his panel.
Touches: Protocol and Host.Surfaces (the `Slider` element, `ViewValidator`); built-in theme (`Wm.Element.Slider`); plugin `subzerodev.indicators` (Core Audio, Network List Manager, power status)
Depends on: S17, S8
Acceptance:
  - S22.1 The volume item shows the default output device's level and mute state, updated within 500 ms of a change made elsewhere. Its popup's slider and mute toggle change them.
  - S22.2 The network item shows connected, limited or disconnected from the Network List Manager, updated within 2 s. Its popup opens `ms-settings:network`.
  - S22.3 The battery item shows the charge and whether it is charging, is hidden on machines without a battery, and its popup opens `ms-settings:batterysaver`.
  - S22.4 A `Slider` with a literal size or colour is rejected (I11). The built-in theme styles it, and the arrow keys move it by `step`.
  - S22.5 The indicators run in both modes. In coexistence mode they are not placed in the built-in layout.
Out of scope: choosing a Wi-Fi network inside the popup (Settings does it); Bluetooth; brightness.

## S23 — Replacement mode qualified for daily use
Status: todo
Delivers: Ben can sign in to the app as his only shell for daily work, with a checked list of what works, a measured time from sign-in to a usable desktop, and a tested way back.
Touches: `docs/replacement-mode.md` (matrix and known gaps); the benchmark harness (sign-in budget); `90-decisions.md`
Depends on: S16, S19, S20, S21, S22
Acceptance:
  - S23.1 `docs/replacement-mode.md` lists each brief criterion for replacement mode with its result on Ben's machine over a week of daily use, and which Explorer-hosted features (toasts, the notification centre, Quick Settings, Task View) work, degrade or are missing. All brief criteria pass, *manual*.
  - S23.2 The harness measures the time from the guardian's start in replacement mode to every first-party instance showing its live view. A budget is set by the S16 rule and checked.
  - S23.3 Each way back to Explorer is exercised once on a real sign-in: `--shell off`, Settings, `host.shell.restoreExplorer`, Shift at sign-in, and the automatic fallback after a host that cannot start (forced with a broken install), *manual*.
  - S23.4 A restart and sign-in with the app registered reach a usable desktop with no `explorer.exe` process running, *manual*.
Out of scope: other machines and editions, beyond recording what is known.

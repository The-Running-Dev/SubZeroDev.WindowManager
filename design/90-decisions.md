# Decision log

Append-only. Newest at the top. The rejected alternatives are the point — without them, every future session relitigates the same choice.

## Open
- **Explorer's taskbar in coexistence mode**: hide it when a `reserve` panel takes its edge, or leave it alone? Default until answered: leave it alone.
- **Monitor and DPI test matrix**: which real displays and scale settings exist to test on? Default: one monitor at 100 % and 150 %, plus a virtual second display if a driver is installed.
- **SDK on nuget.org**: needs Ben's account and key. Default: GitHub Releases plus a local feed only.
- **Arm64**: in scope? Default: x64 only.
- **Prototype checks**: runtime XAML templates with visual states, transparent shaped panels with backdrops, and app bars from WinUI 3. A failure is a new entry here.
- **Windows App SDK release**: pinned in the foundation slice.
- **Performance budgets**: set after the first working prototype.
- **Per-user shell value on build 26200**: honoured by Windows? Default until S17's check: yes; otherwise the per-user Custom User Interface value.

---

## 2026-11-01 — S7: the running-windows strip
- **Context:** The first feed and the first list of plugin items. The list must match what a taskbar shows, mark the window in front, survive many windows, and say why an icon is missing.
- **Chosen:**
  - A window is listed when it is visible, not cloaked, titled, and either `WS_EX_APPWINDOW` or unowned and neither a tool nor a no-activate window. WinEvent hooks mark the list dirty, a rescan follows within 30 ms, and a 2 s timer rescans regardless.
  - Panels are `WS_EX_TOOLWINDOW`, so they are never listed. While a tool window has the foreground, the last active window stays active, so a click on its button minimises it as a taskbar does.
  - Icons resolve in Host.Theming's `IconResolver` through Host.Platform's `IIconService`: `WM_GETICON`, then the class icon, then the executable's first icon, then the host default. Each element reports where its icon came from.
  - `WmFitPanel` lays out zones and lists: when items do not fit, the smallest keep their size and the rest share what is left, and labels trim with an ellipsis.
  - The acceptance tests open their own windows from the test plugin's `fixture-windows` mode, one of each excluded kind, instead of Notepad, whose window and process differ between Windows builds.
  - `aumid` is always `null` for now; packaged-app identity arrives with grouping.
- **Rejected:**
  - Polling alone, because a 1 s budget would need a scan several times a second.
  - Clearing the active window whenever the foreground is not listed, because clicking the panel would then always activate instead of minimise.
  - A scrolling or overflow-menu strip, because it hides the newest windows; grouping and overflow menus stay out of scope.
  - Notepad in the tests, because on current Windows it is a packaged app that may reuse one process for several windows and restores tabs.

## 2026-11-01 — S6: a command whose provider is not running fails, it never starts the worker
- **Context:** Workers start only for placed instances, so a plugin that only declares commands, or one the layout does not use, is never `Running` when its command is invoked.
- **Chosen:** The router returns `1001 PluginUnavailable` and the element shows a `Wm.Notice`; nothing is resent (I19). A click counts as `pointer` unless the element had keyboard focus, when Enter or Space produce it as `keyboard`. A Toggle never flips itself: it asks for the new value and shows whatever the plugin's state says next.
- **Rejected:**
  - Starting the provider on demand and then sending the invocation, because the user's action would run seconds later or twice after a restart, which I19 rules out.
  - Starting every command-providing plugin at boot, because a disabled or unused plugin would cost a process. Left for S9, where plugins gain an enabled switch.
  - Flipping the Toggle before the plugin answers, because a worker that refuses or fails would leave the panel showing a state nobody holds.

## 2026-11-01 — Replacement mode: the Windows key goes through a host hook
- **Context:** Without Explorer, nothing handles a lone Windows-key press or Win+R, Win+E, Win+D and Win+M.
- **Chosen:** A low-level keyboard hook in the host, on its own thread, installed only in replacement mode.
- **Rejected:**
  - `RegisterHotKey` alone, because it cannot express a lone Windows-key press.
  - A hook in a plugin process, because a worker restart would drop keys and a slow callback would delay every key press.

## 2026-11-01 — Replacement mode: the host owns the notification area
- **Context:** Applications send `Shell_NotifyIcon` and `SHAppBarMessage` to whichever window has the class `Shell_TrayWnd`. Without Explorer, nobody receives them.
- **Chosen:** In replacement mode the host creates `Shell_TrayWnd` and implements both protocols, as LiteStep's tray service did. The icons reach plugins through the `host.tray.icons` feed, and clicks go back through `host.tray.invoke`.
- **Rejected:**
  - A plugin owning the window, because it must exist before applications start and survive plugin restarts.
  - Hosting it in coexistence mode, because a second `Shell_TrayWnd` would take icons from Explorer's.

## 2026-11-01 — Replacement mode: registered through the per-user shell value
- **Context:** The app has to become the program Windows starts at sign-in, for Ben's account, without administrator rights, and always be reversible.
- **Chosen:** The guardian writes the per-user `Winlogon\Shell` value after recording the previous one in `shell.json`, and restores it on `--shell off`. A policy or Shell Launcher setting that overrides it is reported, never changed.
- **Rejected:**
  - The machine-wide `HKLM` value, because it needs elevation and changes every account.
  - Shell Launcher, because it needs elevation and an Enterprise feature, and it is built for kiosks.
  - Writing the Custom User Interface policy, because policies belong to administrators. It stays the fallback if Windows ignores the per-user value.
  - Replacing or renaming `explorer.exe`, because File Explorer, servicing and other accounts depend on it.

## 2026-11-01 — Full Explorer replacement is in scope
- **Context:** Ben: "I want this to be a full replacement of the windows explorer shell all together", as LiteStep was. The brief listed full shell replacement and tray hosting as non-goals.
- **Chosen:** The brief, design and contract now include replacement mode. Slices S17 to S23 are appended after S16, so coexistence mode is finished first and replacement mode builds on it. Toasts, the notification centre, virtual desktops, skinning other applications, and replacing File Explorer's folder windows stay out of scope.
- **Rejected:**
  - Building replacement mode before S6 to S16, because it needs the launcher, running windows, settings, icons and release that those slices deliver.
  - Skinning other applications in the WindowBlinds way, because it needs code injected into their processes.

## 2026-11-01 — S5: A broken live edit keeps the running preset
- **Context:** startup falls back to the default preset when the active one is missing or invalid. A live edit that breaks the active preset file would then switch the look and layout to the default while the user is still typing.
- **Chosen:** only startup falls back. A reload whose preset is invalid logs the problem and keeps the running preset. A preset whose theme fails to apply keeps the previous appearance, and later palette or app-mode changes build on that appearance, not on the refused one.
- **Rejected:**
  - Falling back to the default on every reload, because a half-saved file would flash the desktop to another look.
  - Reading the preset's last good mirror, because the running preset already is the last good one in memory.

## 2026-11-01 — S5: A preset change goes through settings.json
- **Context:** `host.preset.apply`, a hand edit of `activePreset` and an edit of the active preset file must all end in the same restyle.
- **Chosen:** `host.preset.apply` only writes `activePreset` with the hash it read. The store's change event then applies the preset, as it does after a hand edit. A preset with another layout switches the layout first; the appearance is applied under one gate so overlapping changes queue.
- **Rejected:**
  - Applying the preset directly and writing settings afterwards, because a failed or conflicting write would leave the screen and the file disagreeing.

## 2026-11-01 — S5: Theme and palette folders have their own watchers
- **Context:** Themes and palettes are not config documents, so the store's watcher does not see them, yet an edited palette must restyle at once.
- **Chosen:** The host watches `<data>\themes` and `<data>\palettes` (subfolders included) and re-applies the appearance 300 ms after the last change. A failed apply keeps the previous look and logs the errors.
- **Rejected:**
  - Polling, because it costs idle CPU and still lags.

## 2026-11-01 — S5: The built-in default preset names no palette
- **Context:** The built-in theme's token defaults already are the dark look; `subzerodev.dark` repeats them.
- **Chosen:** `subzerodev.light` and `subzerodev.dark` ship in `content\palettes`. The default preset names neither, so the first run is unchanged and a preset opts into `{ light, dark }` to follow the app mode.
- **Rejected:**
  - Making the default preset follow the app mode, because it would change the S1 first-run look without being asked.

## 2026-11-01 — S5: Tests switch the app mode on their own registry key
- **Context:** S5.6 needs a light-to-dark switch, and flipping the user's real `AppsUseLightTheme` would change their desktop.
- **Chosen:** With the test hook on, `SUBZERODEV_WM_APPMODE_KEY` names an HKCU key the host reads `AppsUseLightTheme` from instead of `Software\Microsoft\Windows\CurrentVersion\Themes\Personalize`. `AppModeWatcher` uses `RegNotifyChangeKeyValue` on whichever key it reads. The test deletes its key afterwards.
- **Rejected:**
  - Changing the real setting in tests, because it restyles every app the user has open.

## 2026-11-01 — S5: Restyles are shown to reach no worker by a message counter
- **Context:** S5.1 requires a restyle with no worker message, which a screenshot cannot show.
- **Chosen:** `WorkerChannel.MessagesSent` counts every request and notification sent to any worker, liveness pings aside. The test hook reports it as `workerMessages`, with the theme's `appliedAtUtcMs`, `palette`, `lastApply` and each text item's style key, colour and size.
- **Rejected:**
  - Asking the test plugin to log what it receives, because the clock and other workers would go unchecked.

## 2026-11-01 — S5: Invalid settings run on the last good copy
- **Context:** I14 forbids overwriting a broken `settings.json`, but the host still needs settings.
- **Chosen:** Every valid write, and every valid file the store loads, is mirrored to `.lastgood\<path>`. A broken `settings.json` is left untouched and read-only (`DocumentReadOnly`); the host runs on the mirror, or on defaults when there is none, and reports `Invalid` health.
- **Rejected:**
  - Running on defaults only, because the user's preset would vanish on a typo.

## 2026-11-01 — S4: The journal lives in its own Recovery project
- **Context:** The guardian must revert a journal without loading the host, and the host must write the same document.
- **Chosen:** `SubZeroDev.WindowManager.Recovery` holds `JournalDocument`, `Journal`, `Reverter` and `Recoverer`, with its own `Outcome` and `RecoveryErrorCode` (`JournalUnreadable`, `JournalWriteFailed`, `RevertFailed`). Host.Core's `RecoveryJournal` wraps it and maps a failed write to `CoreErrorCode.JournalWriteFailed`. The contract names only `JournalUnreadable`; `JournalWriteFailed` and `RevertFailed` are added.
- **Rejected:**
  - Putting the journal in Host.Core, because the guardian would then carry the host's dependencies.

## 2026-11-01 — S4: An appBar entry records the window, not the rectangle
- **Context:** Reverting an app bar is `ABM_REMOVE` for its window; the shell then recomputes the work area itself.
- **Chosen:** The `appBar` payload holds `hwnd`, `edge` and `thicknessPx`. The entry is written (and flushed) before `ABM_NEW` and ended after `ABM_REMOVE` returns; a refused `ABM_NEW` ends it at once.
- **Rejected:**
  - Recording the granted rectangle, because it is known only after `ABM_SETPOS` and is not needed to revert.

## 2026-11-01 — S4: A failed journal write shows the panel as an overlay
- **Context:** I6 forbids an app bar the journal does not know about.
- **Chosen:** When `BeginAsync` fails, `RegisterAsync` returns `JournalWriteFailed` without sending `ABM_NEW`. The surface manager shows the panel as `overlay` over the monitor bounds, sets `PanelWindow.ReserveError` and logs the error.
- **Rejected:**
  - Reserving anyway, because a crash would then leave space nobody can give back.

## 2026-11-01 — S4: Both the guardian and the host run startup recovery
- **Context:** A host can be started without a guardian (tests, a debugger), and the guardian may have died with the previous host.
- **Chosen:** `Recoverer.Recover` runs in the guardian before it starts a host and in the host before it writes its fresh journal. A journal whose host is alive (pid and start time within 2 s) is left alone; a dead host's entries are reverted newest first, the journal is cleared and a crash record is written. An unreadable journal is moved to `.corrupt` and startup continues.
- **Rejected:**
  - Recovery only in the guardian, because a host started on its own would overwrite a stale journal it never reverted.

## 2026-11-01 — S4: The host answers pings through the UI thread
- **Context:** S4.6 asks for a host that stops answering for 30 s to be killed. A hung UI thread with a healthy pipe thread must count as hung.
- **Chosen:** The guardian serves a `CurrentUserOnly` pipe and writes `ping` every 5 s. The host's guardian link answers `pong` only after a callback queued on the UI dispatcher has run. The guardian checks once a second and kills the host's process tree 30 s after the last reply, then recovers with reason `hostHung`.
- **Rejected:**
  - Answering from the pipe thread, because a frozen UI would still look alive.
  - Window-message probes (`SendMessageTimeout`), because the host may have no window yet during startup.

## 2026-11-01 — S4: Guardian restarts use their own budget copy and 1, 2, 4 s backoffs
- **Context:** I17 sets the host restart budget from `restartPolicy` (default 3 in 300 s). The guardian does not reference Host.Core, where `RestartBudget` lives.
- **Chosen:** The guardian reads `restartPolicy` from `settings.json` itself and keeps a sliding-window budget with backoffs of 1, 2 and 4 s. The host uses `RestartBudget` with the same backoffs to relaunch a dead guardian with `--attach <hostPid>`; the attached guardian listens on a pipe named from the host pid, so the host knows it without being told. When the budget is spent the guardian shows a message naming its log and exits 3.
- **Rejected:**
  - The plugin backoffs (1, 5, 30 s), because a host restart already takes seconds and S4.2 allows 10 s.
  - Referencing Host.Core from the guardian, because it would load the host's dependencies into the watchdog.

## 2026-11-01 — S4: Tests observe recovery through checkpoints and guardian.json
- **Context:** S4.1 needs the journal read by another process between the write and `ABM_NEW`, and S4.5 needs the guardian's message dismissed.
- **Chosen:** Under the test hook, `AppBarService` awaits named checkpoints (`before-appbar`, `after-remove`) that hold while `hook\hold-<name>` exists, a `hang` file sleeps the UI thread for 2 minutes, and the guardian writes `hook\guardian.json` (pid, args, state, hostPid) and waits for `hook\dismiss` instead of a message box. Without the hook the checkpoints are no-ops.
- **Rejected:**
  - Timing-based reads of the journal, because `ABM_NEW` follows the write within microseconds.

## 2026-11-01 — S4: The guardian exits 4 when it cannot start the host
- **Context:** The contract gives the guardian `0`, `2` and `3`, and none fits a guardian that never got a host running: a missing host executable or a bad `--attach` argument.
- **Chosen:** Exit `4` for that case. A host that exits `2` (another host running) makes the guardian exit `2`, and a spent budget is `3` as before. A host that exits `0` with entries still journalled is reverted before the guardian exits `0`.
- **Rejected:**
  - Exit `3`, because the restart budget was never touched and the log would point the wrong way.

## 2026-11-01 — S4: Supervision survives its own failures
- **Context:** Review found that one unexpected exception in the guardian's ping loop would stop the pings and get a healthy host killed as hung, and that a sleeping machine would count its sleep as 30 s of silence.
- **Chosen:** The ping loop logs any failure and serves the pipe again a second later; the host reconnects the same way. Silence is counted a tick at a time, at most 2 s per tick. A kill that does not end the host within 10 s is logged and tried again. Journal reads and replacements are retried for 250 ms while the other process has the file open.
- **Rejected:**
  - Wall-clock time since the last reply, because it jumps after sleep or a clock change.

## 2026-11-01 — S3: The message rate is 200 a second, measured over the trailing second
- **Context:** S3.7 needs a configured rate, and the design names none. A clock or a media widget sends a few patches a second; a drag or a progress bar can send dozens.
- **Chosen:** `SupervisorTimings.MessagesPerSecond` = 200, counted over the second before each message. Past the rate, events are dropped (with a rate-limited log line) and `state.patch` and `view.set` still pass, because the host coalesces them latest-wins. Ten seconds in which every message is over the rate close the channel with `Flood`, timed from the first; a message within the rate starts the count again.
- **Rejected:**
  - Whole-second windows, because a burst late in one second and a flood early in the next counted as one run, and CI saw a flood faulted 9 s after it began.
  - A token bucket, because it allows a sustained rate over the limit after an idle spell, and the trailing second is easier to reason about in logs and tests.
  - Dropping patches over the rate, because a dropped patch can leave the wrong final value on screen (S3.7).

## 2026-11-01 — S3: The heartbeat is checked every second
- **Context:** S3.2 allows 10–15 s from the last reply to the fault. Pings go out every 5 s with a 10 s timeout.
- **Chosen:** The heartbeat loop wakes every second (a tenth of the timeout when that is shorter), so the fault lands 10–11 s after the last reply. Only one ping is in flight at a time, and the fault needs one to be unanswered. A check that runs more than half the timeout late means the host stalled, so the silence is measured again from then.
- **Rejected:**
  - Faulting when a single ping times out, because that measures from the ping, not from the last reply, and could fault up to 15 s plus a ping interval late.
  - Counting silence across a host stall, because a debugger break or a sleeping laptop would fault every healthy worker.

## 2026-11-01 — S3: Start failures count against the restart budget
- **Context:** A worker that fails its handshake or `initialize` would otherwise be restarted forever, or never.
- **Chosen:** Every fault — exit, broken pipe, heartbeat, handshake, protocol violation, flood or failed start — goes through the same budget, backoff and Unavailable path. A start that throws part-way kills the process and closes the pipe, then faults with `StartFailed`; a start cancelled by shutdown cleans up the same way and leaves the plugin Stopped. After the supervisor is disposed, nothing starts.
- **Rejected:**
  - Treating start failures as permanent, because a slow virus scan on first launch makes them transient.

## 2026-11-01 — S3: A killed worker is awaited before anything restarts
- **Context:** I4 allows at most one process per plugin id, and `Kill` returns before the process has ended.
- **Chosen:** Killing waits up to 5 s for the process handle to signal, and the restart runs only after that, under the worker's gate.
- **Rejected:**
  - Starting the replacement straight after `Kill`, because S3.6 samples every 50 ms and would see two processes.

## 2026-11-01 — S3: Renders and published patches coalesce per instance
- **Context:** A burst of patches would queue one UI-thread render per patch and stall the dispatcher past 50 ms (I9, S3.5).
- **Chosen:** The contribution host publishes at most one pending change per instance, and the surface manager queues at most one render per instance; each render reads the latest snapshot, so the final value always shows.
- **Rejected:**
  - Throttling on a timer, because it adds latency to every single update.

## 2026-11-01 — S3: The UI watchdog runs in debug builds and under the test hook
- **Context:** I9 calls for a debug-build dispatcher watchdog, but the end-to-end tests run Release builds.
- **Chosen:** `DispatcherWatchdog` posts a probe every 25 ms and counts waits over 50 ms. It is on in Debug builds and whenever the test hook is set; the hook reports its count.
- **Rejected:**
  - Debug builds only, because then S3.5 could not be checked against the build that ships.

## 2026-11-01 — S3: Retry lives in the placeholder template
- **Context:** S3.4 needs Retry on the placeholder, and themes restyle the placeholder.
- **Chosen:** Both placeholder templates hold a `Retry` button bound to `RetryCommand`, shown by the Faulted and Unavailable visual states; the fault detail is the placeholder's tooltip. The command calls `RetryAsync` off the UI thread.
- **Rejected:**
  - A button added in code, because a theme could then neither place nor hide it.

## 2026-11-01 — S3: The faulty plugin is the test plugin with switch files
- **Context:** The slice names a `samples.faulty` plugin with crash, hang and flood switches.
- **Chosen:** The test plugin's `faulty` scenario, installed as `samples.faulty`, reads `crash`, `hang`, `burst` and `flood` files from its data folder every 50 ms and logs each create and exit. The SDK gains internal switches to stop answering pings. The test hook gains a `retry` file, pointer-press times, contribution bounds, fault fields and the watchdog count.
- **Rejected:**
  - A separate sample project, because it would duplicate the test plugin's build and copy steps for no extra coverage.

## 2026-11-01 — S2: Dispatcher callbacks catch their own exceptions
- **Context:** In WinUI 3 an exception escaping a `DispatcherQueue` callback or timer tick ends the process with 0xC000027B and never reaches `Application.UnhandledException`. CI hit this when the test hook's status replace met the test's open read.
- **Chosen:** Each dispatcher callback and timer tick catches and logs its own failures; unhandled exceptions are also written to the host log synchronously, and CI keeps host crash dumps.
- **Rejected:** Relying on `UnhandledException` with `Handled = true` (it does not see these exceptions).

## 2026-11-01 — S2: Plugin-facing projects target plain net10.0
- **Context:** Protocol, Sdk and plugin projects are built by third parties, and the host projects target `net10.0-windows10.0.26100.0`.
- **Chosen:** Protocol, Sdk, the clock and the test plugin target `net10.0` with `win-x64`, so a plugin needs no Windows SDK projection and the host can still reference Protocol.
- **Rejected:**
  - Windows-specific targets on the plugin side, because nothing in the wire contract needs them and they would force every plugin author onto the projection.

## 2026-11-01 — S2: The bundled clock is copied into the host's content output
- **Context:** S2.2 needs the clock at `content\plugins\subzerodev.clock\` beside the host, but the clock must stay a separately built project (I2).
- **Chosen:** Host.App builds the clock project with `ReferenceOutputAssembly=false` and copies its build output into `content\plugins\subzerodev.clock\` after build.
- **Rejected:**
  - A project reference, because the host would then load the plugin assembly (I1).
  - A manual copy step, because tests would run against a stale clock.

## 2026-11-01 — S2: The worker reads its id from plugin.json
- **Context:** The design's handshake carries the plugin id, but the launch arguments list only pipe, token, data folder and mode.
- **Chosen:** The Sdk reads `id` from `plugin.json` next to the entry exe, so one built clock serves as both `subzerodev.clock` and `example.clock` (S2.7).
- **Rejected:**
  - A `--wm-id` argument, because it would change the launch contract and could disagree with the manifest.

## 2026-11-01 — S2: S2.5, S2.6 and S2.8 use a test plugin
- **Context:** S2.5 names the clock's log, but the clock never sends an invalid view or a stale patch, and S2.8 needs a worker that ignores `shutdown`.
- **Chosen:** A `subzerodev.testplugin` project under `tests/` sends a valid view, then invalid views and a stale patch, or ignores `shutdown`, by switch. The E2E tests read `logs\plugins\subzerodev.testplugin-<date>.log`, which uses the same per-plugin log writer as the clock.
- **Rejected:**
  - Test switches inside the clock, because shipped code would carry them.

## 2026-11-01 — S2: IPluginSupervisor carries only the S2 subset
- **Context:** The contract lists fault detection, heartbeat, backoff and `RetryAsync`, which belong to S3.
- **Chosen:** S2 implements start, handshake, initialize, render, stop and status; the remaining members arrive with S3.
- **Rejected:**
  - Stub members that throw, because they would compile but lie about capability.

## 2026-11-01 — S2: IWorkerProcess and the job's process ids
- **Context:** S2.8 needs to prove that no worker outlives the host, and the launcher must be faked in unit tests.
- **Chosen:** Host.Platform exposes `IWorkerProcess` (pid, stderr, exit task, kill) from `WorkerLauncher`, which creates workers suspended in a kill-on-close job; the test hook reports the job's process ids.
- **Rejected:**
  - `System.Diagnostics.Process`, because it cannot start a process suspended inside a job before it runs.

## 2026-11-01 — S2: PlaceholderReason lives in Host.Core
- **Context:** The contract placed placeholder reasons in Host.Surfaces, but the supervisor sets them.
- **Chosen:** `PlaceholderReason` moved to Host.Core so the supervisor can report it without a dependency on Surfaces.
- **Rejected:**
  - A mapping layer in Surfaces, because it would duplicate the enum for no gain.

## 2026-11-01 — S2: Worker pipes use CurrentUserOnly
- **Context:** The design asks that only the current user can open a worker pipe.
- **Chosen:** `PipeOptions.CurrentUserOnly` on a single-instance pipe with a per-launch random name and token.
- **Rejected:**
  - A hand-built ACL, because .NET's option gives the same protection with less code.

## 2026-11-01 — S2: Contribution kinds beyond S2 render empty
- **Context:** The view model defines kinds that later slices implement.
- **Chosen:** `ContributionView` renders text, the clock's only need, and leaves other kinds empty until their slice.
- **Rejected:**
  - Rejecting unknown kinds as protocol violations, because later slices would have to undo it.

## 2026-11-01 — S2: Config file watching is opt-in
- **Context:** S2.7 needs the layout reloaded when the file changes, but unit tests construct many stores.
- **Chosen:** `ConfigStore` watches only when asked; the host turns it on. A save whose content is identical raises no change.
- **Rejected:**
  - Always-on watching, because tests would leak watchers.
  - Raising a change on every write, because the host's own saves would reload the layout.

## 2026-11-01 — S2: Icons show the named id only
- **Context:** The view model allows an icon reference, but icon resolution is a theme concern for a later slice.
- **Chosen:** An icon renders its id as text for now.
- **Rejected:**
  - Loading icon files, because the source and fallback rules are not designed yet.

## 2026-11-01 — S2: The Sdk reads before it says hello
- **Context:** Named pipes here have no buffer, so a write blocks until the other side reads; the first E2E runs deadlocked in the handshake.
- **Chosen:** The Sdk starts its read loop before it sends hello, and the host does the same before initialize.
- **Rejected:**
  - Pipe buffers, because they hide the ordering bug instead of fixing it.

## 2026-11-01 — S2: The host shuts down only on explicit exit
- **Context:** A layout edit closes every panel before it opens the new ones; WinUI's default shutdown on last window close ended the dispatcher and crashed the host.
- **Chosen:** `DispatcherShutdownMode.OnExplicitShutdown`; the host ends only through `Exit()`.
- **Rejected:**
  - Opening the new panels before closing the old ones, because app-bar space would be claimed twice.

## 2026-11-01 — S2: End-to-end timing waits carry 5 s of slack
- **Context:** The first run of a newly written worker exe waits on the virus scanner, which pushed S2.2 and S2.7 past their 3 s and 5 s budgets on a cold build.
- **Chosen:** E2E waits use the criterion's time plus 5 s; the real budgets are measured in S16.
- **Rejected:**
  - Exact budgets in E2E tests, because they fail on scanner timing outside the product.

## 2026-11-01 — S1: Pin Windows App SDK 1.8.260317003
- **Context:** The platform baseline left the exact Windows App SDK release to the first slice, and the prototype checks had to pass on it.
- **Chosen:** Windows App SDK 1.8.260317003 with SDK.BuildTools 10.0.26100.4654, pinned in `Directory.Packages.props`. Runtime-loaded templates with visual states, an acrylic backdrop behind a translucent panel, and app bars all work on it unpackaged and self-contained.
- **Rejected:**
  - A 2.x release, because none had been validated against the prototype checks.
  - Floating or wildcard versions, because a silent runtime change would invalidate the checks.

## 2026-11-01 — Platform baseline: .NET 10, pinned Windows App SDK, Windows 11 22H2, x64
- **Context:** The brief left versions, minimum build and architectures open.
- **Chosen:** .NET 10 (LTS) for the host, guardian, SDK and plugins. A stable Windows App SDK release, pinned in the first slice. Windows 11 22H2 (build 22621) minimum. x64 only.
- **Rejected:**
  - .NET 9 (STS), because its support ends sooner than the project's first year.
  - Targeting Windows 10, because the brief says modern Windows 11 and the backdrops need 22H2.
  - Arm64 now, because there is no Arm64 machine to test on (open question).

## 2026-11-01 — One UI thread for all host surfaces
- **Context:** The host owns many panel, popup and gadget windows.
- **Chosen:** One UI thread owns every window and resource dictionary. IO and IPC stay off it.
- **Rejected:** A thread per panel. XAML resources are thread-affine, so every thread would need its own theme copy, and an atomic theme switch would need cross-thread coordination.

## 2026-11-01 — Desktop-layer gadgets stay at the bottom of the z-order
- **Context:** Layouts may place panels on the desktop beneath windows (premise 2).
- **Chosen:** Ordinary host windows kept at the bottom of the z-order.
- **Rejected:** Re-parenting into Explorer's desktop worker window. It is undocumented, it breaks across Windows builds, and it does not exist in replacement mode.

## 2026-11-01 — Unpackaged, per-user, self-contained distribution
- **Context:** Ben is the single first-release user, and the brief needs no-admin install and a later shell-replacement path.
- **Chosen:** A zip on GitHub Releases, extracted to `%LOCALAPPDATA%\Programs`, with self-contained .NET and Windows App SDK. The SDK ships as a NuGet package and a PackTool .NET tool on the same release.
- **Rejected:** MSIX. Package identity virtualises file and registry access, which complicates user plugin folders and the later shell launch, and it needs a signing certificate. It may return as an extra channel.

## 2026-11-01 — Guardian process as the entry point, with a write-ahead journal
- **Context:** The brief says abrupt-failure recovery cannot rely on cleanup in the failed process.
- **Chosen:** The guardian exe launches and watches the host. Every reversible desktop change is journalled and flushed before it is applied. The guardian reverts the journal when the host dies or at the next start, and relaunches the host within a budget.
- **Rejected:**
  - Cleanup in the host only, because a dead process cleans nothing.
  - A guardian spawned by the host inside its job object, because it would die with the host.
  - Restarting Explorer, because it disrupts the user and does not exist in replacement mode.

## 2026-11-01 — Configuration as JSON files, one per document, written atomically
- **Context:** Configuration is data the user may hand-edit and the settings UI also writes (premise 3).
- **Chosen:** JSON files with `schemaVersion`. There is one writer queue with content-hash conflict detection and temp-file-then-replace writes. An invalid file is never overwritten, and the last good copy is kept.
- **Rejected:**
  - SQLite, because it is not hand-editable or diffable.
  - The registry, for the same reason, and because it is harder to share as a preset.

## 2026-11-01 — Themes are runtime-loaded XAML resource dictionaries over a host template vocabulary
- **Context:** Deep, WindowBlinds/Enlightenment-level appearance control without themes carrying code.
- **Chosen:** Themes are `ResourceDictionary` files loaded with `XamlReader`. Styles target host templated controls by keys (`Wm.Panel`, `Wm.Element.<Kind>[.<class>]`, and so on). Palettes are separate token → colour files. Views carry no literal styling.
- **Rejected:**
  - A custom JSON styling language, because it would be a second rendering engine and never reach control-template expressiveness.
  - Compiled theme assemblies, because they are executable code in the host.

## 2026-11-01 — JSON-RPC 2.0 over per-worker named pipes
- **Context:** Host ↔ worker transport for small, frequent messages that a developer must be able to debug.
- **Chosen:** One duplex pipe per worker with length-prefixed UTF-8 JSON-RPC 2.0. Each pipe has a current-user ACL and a random name, and the connection must start with a one-time token.
- **Rejected:**
  - Standard input and output, because stray console writes corrupt the stream and it cannot attach a debugger-started worker.
  - gRPC, because it is a heavy stack for a local link.
  - Out-of-process COM, because it needs registration and is hostile to third-party authors.
  - MessagePack for now, because it is unreadable in logs. It can change behind the Protocol module with a version bump.

## 2026-11-01 — One worker process per plugin
- **Context:** Criterion 7 requires a failed plugin to leave the host responsive and recoverable.
- **Chosen:** Every plugin, first-party ones included, runs in its own headless process inside a kill-on-close job object. The host never loads plugin code.
- **Rejected:**
  - In-process loading through AssemblyLoadContext, because it gives no crash or hang isolation.
  - One shared worker process, because one bad plugin takes down all of them.
  - A process per contribution instance, because memory scales with placements for no gain.

## 2026-11-01 — Approach: declarative contributions rendered by the host ("minimal viable")
- **Context:** Choosing the overall shape against the brief's criteria with a single developer.
- **Chosen:** Plugins send declarative view trees and state. The host renders them with the active theme. A plugin may have one worker process, and a guardian restores the desktop.
- **Rejected:**
  - Plugin-drawn UI embedded across processes, because cross-process input, focus, DPI and accessibility are fragile, and themes cannot restyle it.
  - WebView2 surfaces with HTML and CSS themes, because the per-surface browser cost works against the startup and idle budgets.
  - Trusted in-process WinUI controls in the first release, because they share the host's crash risk. Deferred.

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

---

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

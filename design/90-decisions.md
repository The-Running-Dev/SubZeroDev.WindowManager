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

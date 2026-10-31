---
title: "Modern Windows shell: plugins, deep theming and desktop vision"
project: SubZeroDev.WindowManager
date: 2026-10-26
type: investigation
tags: [windows-shell, csharp, plugins, theming, litestep, enlightenment]
---

# Modern Windows shell: plugins, deep theming and desktop vision

## Question and scope

The discussion began with this request:

> Shit is about to get crazy, look at D:\Dropbox\Projects\LiteStep-Original, then at D:\Dropbox\Projects\LiteStep, deep inspect both, the original and the clone. Tell me how feasible is to re-surect this project and to create a new windows shell for modern windows 11

Ben then clarified the intended project:

> I want this to be built in C Sharp in, say, Maui or WinUI, whichever the latest one is, so it's modern looking. It could replace the shell, but it initially doesn't have to. It could just run on top until all the kinks are worked out. Very important that a lot of these things are provided through plugins, not just everything built in, but have a base that then could be extensible to plugins, and then build some basic plugins to do things, and then we'll keep extending those plugins to provide all kinds of other functionality.

Additional requirements and references:

> So besides that, yes, this will be a new project, not a copy of Lightstep, but you could use Lightstep as a reference.

> There was an application a while back, and it's probably still called Windows Blinds, which lets you customize all kinds of shit. So I'd want to be able to use the same idea to customize colors, the icons, all that shit.

> Oh right, the other thing I loved and would love to brin in here is Enlightenment, a Linux desktop environtment, beautiful and customizable.

This note captures the discussion through the request to write it up. It records explicit requirements, inspected facts, assistant proposals, and unresolved choices separately. It is a concept and architecture record, not a completed implementation specification.

## Short answer

Build a new C# desktop environment for modern Windows 11, with a small extensible host, separately packaged functional plugins, and substantial visual customization. Begin in coexistence with Explorer and preserve a path to a tested replacement-shell mode.

WinUI 3 with the Windows App SDK is the proposed Windows UI foundation. MAUI was considered; the exact framework choice and dependency versions have not been formally finalized.

LiteStep informs the modular shell architecture. WindowBlinds informs skinning and customization. Enlightenment informs the expressive, configurable desktop experience. These are references, not a commitment to copy their code, assets, theme formats, or binary plugin interfaces.

## Explicit requirements from Ben

1. This is a new project, not a copy of LiteStep.
2. Implement it in C#.
3. Use a modern UI technology; MAUI and WinUI were named as possibilities.
4. Target a modern Windows 11 desktop experience.
5. It may eventually replace the Windows shell.
6. It does not need to replace the shell initially; running on top of the existing desktop is acceptable while problems are worked out.
7. A significant amount of functionality must be provided through plugins.
8. Build an extensible base, then basic plugins, then keep extending the system.
9. Provide extensive appearance customization, including colors and icons, inspired by WindowBlinds.
10. Bring in the beauty and customizability Ben values in Enlightenment.
11. LiteStep may be used as a reference.
12. Ben can install WindowBlinds for later inspection or reverse engineering if that becomes useful.

The project name, new repository, final SDK versions, package format, and detailed release scope remain undecided.

## What was found in the LiteStep repositories

### Inspected baselines

| Item | LiteStep-Original | LiteStep clone |
| --- | --- | --- |
| Absolute directory | `D:\Dropbox\Projects\LiteStep-Original` | `D:\Dropbox\Projects\LiteStep` |
| Git remote | https://github.com/The-Running-Dev/LiteStep-Original | https://github.com/The-Running-Dev/LiteStep |
| Inspected head | `2b1c9ee`, 2017-07-24 | `4158784`, 2025-10-16 |
| Relationship | Original development baseline | Same ancestry, 37 additional commits |
| Toolchain configuration | VS2017-era `v141_xp` | VS2022 `v143` |
| Architectures configured | Win32 and x64 | Win32 and x64 |
| Main contents | Shell core, SDK/examples, documentation, historical 0.24.8 material | Shell core plus bundled nModules and experimental Theme Engine V2 |

Across the shared `litestep`, `lsapi`, and `utility` directories, the comparison found 74 unchanged files, 35 modified files, and 14 added files. The clone remains substantially the original architecture.

The inspected clone contained approximately 54,000 non-vendor C++ source/header lines. This is a source inventory, not a measure of feature completeness.

Neither original source directory was modified. Compilation experiments used isolated copies under:

`D:\Dropbox\Documents\Codex\2026-10-26\new-chat-16\inspection-build`

Those copies and logs are temporary investigation material, not the new project.

### Useful architectural references

The original implements:

- Module DLL loading, initialization, unloading, reload and recycle.
- Traditional `step.rc` configuration, variables and bang commands.
- Message routing between the core and modules.
- Tray icon management and taskbar integration messages.
- Startup application handling.
- Fullscreen monitoring and module hiding.
- Wallpaper-related services.
- A recovery menu with a keyboard shortcut.

The clone adds or changes:

- Explorer coexistence through an overlay startup mode.
- Startup/module logging and diagnostics.
- A pooled background task executor.
- Bundled modules for desktop features.
- Window filtering and taskbar behavior.
- Panel layout code.
- Display and remote-session changes that trigger UI recycling.
- Theme Engine V2 source loading, lexing, parsing and diagnostics.

The bundled nModules framework contains real Direct2D, DirectWrite and Windows Imaging Component integration. It is useful as a reference for how desktop components behave; the new C# implementation is not committed to reusing that renderer.

### Confirmed defects and incomplete features

| Finding | Evidence | Implication |
| --- | --- | --- |
| Missing SDK header | `D:\Dropbox\Projects\LiteStep\modules\ModuleKit\LiteStep.h` includes `../../sdk/include/lsapi.h`; the clone has no `sdk` directory | Bundled modules cannot compile unchanged |
| Invalid theme types | `D:\Dropbox\Projects\LiteStep\litestep\themev2\ThemeTypes.h` embeds an incomplete `Value` type and defines members outside their namespace without qualification | MSVC reproduced source-level compilation errors |
| Theme V2 stops at parsing | `D:\Dropbox\Projects\LiteStep\litestep\themev2\ThemeEngineV2.cpp` loads and parses a document and logs diagnostics | No completed render/layout/style/module binding pipeline was found |
| Theme V2 is opt-in | Initialization checks `LSTHEME_V2_ENABLED` | It is not the default working desktop engine |
| Incomplete assets | `theme.lsx` references missing `constants.lsx` and image assets; traditional configuration references an absent wallpaper directory | Sample configuration is not a complete delivered theme |
| Virtual-desktop stub | `D:\Dropbox\Projects\LiteStep\modules\VWM\ModuleEntry.cpp` initializes the module but implements no desktop management | The default configuration exposes desktop commands that were not found implemented |
| Old scripting engine | `D:\Dropbox\Projects\LiteStep\modules\External\v8\src\version.cc` identifies V8 3.20.15.5 | Scripting needs substantial modernization if pursued |
| Incomplete blur implementation | `BlurBehind` is parsed into settings; no matching implementation applying it was found | Configuration and README appearance claims exceed verified functionality |
| Stale SDK/documentation references | The clone retains references to removed SDK/docs paths | Project cleanup and build repair remain unfinished |
| Incomplete task API declaration | New task functions exist in implementation, but declarations were not found in the remaining public `lsapi.h` | The public API wiring needs repair |

### Compilation results and limits

Direct MSVC syntax checking reproduced:

```text
error C2079: 'litestep::themev2::ObjectProperty::value' uses undefined struct 'litestep::themev2::Value'
error C2653: 'SourceLocation': is not a class or namespace name
fatal error C1083: Cannot open include file: '../../sdk/include/lsapi.h': No such file or directory
```

The clone's `TaskExecutor.cpp` passed the targeted syntax check. The original repository's `Module.cpp` also passed its targeted syntax check.

A full clone solution build encountered an environment restriction during Windows SDK discovery:

```text
error MSB4184: The expression "[Microsoft.Build.Utilities.ToolLocationHelper]::GetLatestSDKTargetPlatformVersion(Windows, 10.0)" cannot be evaluated. Requested registry access is not allowed.
```

No successful full build or runtime validation was established. A successful syntax check of an individual file does not establish a functioning shell.

## Feasibility and Windows constraints

The investigation's assessment was that a modern modular Windows desktop is feasible. The expensive work is everyday compatibility and recovery, rather than merely drawing panels or configuring shell startup.

Microsoft documents:

- A Custom User Interface / CustomShell policy that can start another interface instead of Explorer, including on Windows 11 Pro.
- Shell Launcher on Enterprise, Education, and IoT Enterprise editions, with additional lifecycle management.
- DWM backdrop APIs for materials such as Mica and Acrylic on supported Windows builds.
- Per-Monitor V2 DPI awareness and application responsibilities when DPI changes.
- A public `IVirtualDesktopManager` interface covering window desktop membership and window movement, but not a complete create/enumerate/switch desktop management API.

The underlying Windows operating system and composition facilities remain in place. An alternate shell does not require implementing an operating system or replacing DWM.

Material integration concerns:

- Tray-host compatibility: inherited LiteStep code receives undocumented `WM_COPYDATA` protocols using `Shell_TrayWnd`.
- Modern notifications: tray tweaks alone do not establish a complete notification center.
- Mixed-DPI monitors, monitor changes, remote sessions and fullscreen applications.
- Application activation and window tracking, including packaged and elevated applications.
- Virtual-desktop integration beyond the limited public API.
- Reliable restoration of a usable desktop when the host or a plugin fails.
- Third-party plugins sharing a process can crash or corrupt that process.

Initial rough estimates were discussed for repairing and modernizing the legacy code: several weeks for basic build/launch work, roughly 2-3 months for useful coexistence, 4-9 months for a personal replacement shell, and 9-18+ months for a broader public release. These were assistant estimates for a bounded scope with one experienced Windows developer. They were not commitments, were not validated, and are not a schedule for the subsequently clarified new C# implementation. A new implementation needs its own estimate.

## Proposed C# foundation

The assistant recommended C# with WinUI 3 and the Windows App SDK.

Reasoning:

- WinUI 3 is Microsoft's recommended native UI framework for new Windows desktop applications.
- MAUI's standard Windows backend already uses WinUI 3.
- This project targets Windows-specific desktop behavior, so directly using WinUI avoids introducing a cross-platform abstraction without an established cross-platform requirement.
- WinUI can provide native controls, styling and accessibility foundations.
- Windows interop is still needed for work areas, positioning, window monitoring and shell behavior.

This recommendation does not mean a fixed Windows 11 appearance. The rendering and theme design must support substantially different visual identities.

The exact supported .NET release, Windows App SDK version, minimum Windows build, deployment approach and interop tooling remain open. Use verified supported releases when implementation begins rather than treating a version mentioned in a discussion as permanently current.

## Proposed host and plugin architecture

### Infrastructure versus features

The proposed base owns shared infrastructure. Features are delivered through plugins, including the default desktop features.

| Host responsibilities | Plugin responsibilities |
| --- | --- |
| Process/application lifecycle and recovery | Launcher and application search |
| Plugin discovery, compatibility and dependency management | Running-app taskbar and window switcher |
| Desktop surfaces, monitors and DPI | Clock and calendar |
| Layout, theme resources and persisted configuration | Media controls |
| Command registration and routing | System monitoring and desktop widgets |
| Shared Windows integration interfaces | Desktop shortcuts and wallpaper features |
| Settings and diagnostics infrastructure | Tray integration and virtual-desktop features |

The host should understand reusable contribution types such as panels, items, commands, settings pages and data providers. It should not contain special cases for each feature.

First-party plugins should use the public SDK and the same packaging route offered to later extensions. This tests whether the system is actually extensible.

Example composition discussed:

```text
Bottom panel: Launcher -> Running applications -> Media controls -> Clock
```

This is a configured arrangement of plugin contributions. Changing or removing a feature should not require editing the host.

### Plugin contract

Proposed package metadata and contracts:

- Stable identity and plugin version.
- Supported host API version.
- Dependencies and requested capabilities.
- Exported commands, visual contributions, settings and data providers.
- Start/stop lifecycle and resource cleanup.
- Configuration schema and migrations.
- Diagnostic state.

Plugins communicate through published interfaces and commands, not through another plugin's internal implementation. Examples discussed were `applications.launch` and `launcher.open`.

Shared providers should allow several components to consume the same running-window or other system data without implementing redundant monitors.

Exact interfaces, transport, manifest format and version-negotiation rules have not been designed.

### Isolation and visual contributions

Two approaches were proposed:

| Approach | Intended use |
| --- | --- |
| Plugin supplies data, actions and supported visual descriptions; host renders them | Standard widgets, buttons, lists, launchers and status displays |
| Trusted plugin supplies custom WinUI controls | Extensions needing UI beyond the standard contribution model |

The first approach allows background plugin logic to run in a separate process, with an unavailable state and restart if the process fails.

Custom controls loaded into the host share its failure risks. The discussion did not establish that arbitrary WinUI controls could be seamlessly hosted across process boundaries.

.NET `AssemblyLoadContext` helps with assembly loading and dependency management. It is not a security or reliability boundary. Separate processes help with crash isolation; they do not automatically prevent a plugin from accessing the user's files or other resources.

The preferred direction proposed was a sufficiently capable standard contribution model, with custom controls as an advanced trusted extension path. Final process boundaries, recovery supervision and custom-control policy remain open.

## Coexistence first, replacement later

### Initial coexistence mode

- Explorer remains running.
- The new desktop's panels, launchers and widgets run alongside it.
- Closing the new application removes its surfaces and restores any desktop work-area changes it made.
- Explorer-dependent features declare that dependency.
- Features can be developed and tested without making shell replacement a prerequisite.

### Later replacement-shell mode

- Reuse the same host and plugin architecture.
- Add and validate the responsibilities required when Explorer is no longer providing the desktop shell.
- Allow plugins to declare coexistence support, replacement-mode support, or both.
- Validate startup, taskbar/tray behavior, application activation and recovery as an integrated experience.

Coexistence does not establish replacement-shell compatibility. The same architecture should accommodate both without creating two unrelated desktop products.

## WindowBlinds-style customization

### Verified product behavior

Stardock still offers WindowBlinds 11 for Windows 10/11. Its documentation describes skins for window frames, controls and other interface elements, with configurable colors, fonts, textures, transparency, presets and per-application exclusions. SkinStudio is the companion skin editor. Start-menu skinning requires Start11.

Stardock's separate IconPackager product provides icon packages, file-type icon customization and recoloring. It is a relevant conceptual reference for Ben's icon requirement. Its product page listed Windows 10/8/7; current Windows 11 compatibility was not established here.

No installed WindowBlinds copy was inspected and no binaries were reverse engineered. Public documentation establishes capabilities, not the details of the current internal implementation. Ben offered to install it if deeper inspection becomes useful.

### Proposed customization boundaries

| Capability | Scope |
| --- | --- |
| Our desktop's appearance | Panels, menus, launchers, widgets, icons, colors, typography, spacing, animation and backgrounds |
| Customization outside our desktop | Other applications' frames/controls and Windows-wide icons through dedicated integrations |

Both can be represented in the architecture, but they require different implementation work. Changing our WinUI resources does not automatically reskin other applications.

Deep external skinning was discussed as a later integration/plugin area, not an already verified C# capability or a first-release commitment.

## Enlightenment influence

Ben explicitly identified Enlightenment as a desktop he loved for its beauty and customizability.

Relevant documented concepts:

- Shelves: configurable panels, including multiple panels per screen.
- Modules and gadgets: features supplied through modular code and visual desktop components.
- Gadgets positioned on the desktop as well as in shelves.
- Extensive graphical settings.
- Palettes that can change colors independently of the selected theme.
- Edje themes containing layout, graphical assets, visual states and animation.
- Scalable components and theme-driven backgrounds.

Enlightenment is also a window manager and compositor on its supported systems. The proposal is to translate useful desktop concepts into Windows facilities, not port an entire Linux compositor.

### The three references

| Reference | Proposed influence |
| --- | --- |
| LiteStep | Small shell base, plugins, commands and configurable composition |
| WindowBlinds | Skin packages, appearance customization, and optional external-application styling |
| Enlightenment | Expressive visual design, shelves, gadgets, animation and extensive configuration |

No decision was made to import Enlightenment's EDJ packages or WindowBlinds skins directly. Compatibility with their formats would require separate investigation.

## Theme, layout and asset model

The discussion proposed four independently selectable parts:

| Part | Controls |
| --- | --- |
| Plugins | Available functionality |
| Layout | Positioning, sizing, panels and monitor assignments |
| Theme | Component shapes, typography, materials, transitions and visual states |
| Asset packs | Icons, wallpapers and replaceable graphics |

A desktop preset can combine these selections.

Themes should be installable packages distinct from executable functional plugins. Standard plugin components inherit shared theme styles and assets, with supported overrides.

A theme should be able to substantially transform the desktop: minimalist, industrial, translucent, retro, textured or other styles. This was proposed as an architectural requirement from the beginning. It requires configurable templates and composition, rather than each plugin hardcoding its appearance.

The goal is to change a desktop's visual identity while retaining its functionality. The exact theme schema, editor, supported animation model and custom rendering escape hatches remain unspecified.

## Proposed first implementation slice

The assistant proposed proving the architecture with:

1. A configurable panel.
2. A separately packaged clock plugin with a settings page.
3. A launcher plugin with application shortcuts and basic search.
4. A running-windows plugin for switching applications.
5. A plugin manager for enabling, disabling, configuring and diagnosing plugins.

Tray replacement and comprehensive virtual-desktop management were proposed for later work.

This is a proposed scope, not an approved detailed backlog.

### Proposed acceptance checks

- Build a plugin in a separate project against the SDK.
- Install it without modifying or rebuilding the host.
- Add its visual contribution to a configured desktop surface.
- Change its settings and preserve them across restart.
- Disable/remove it without editing host source.
- Switch themes while standard plugin contributions remain coherent.
- Fail an isolated plugin process and leave the host usable.
- Exit coexistence mode and restore any work-area changes.
- Validate a small multi-monitor/display-change scenario before expanding shell responsibilities.

These are design validation goals. They have not been run against a new C# implementation, which does not yet exist in this discussion.

## Open decisions and follow-ups

- Choose the project/repository name and implementation location.
- Confirm WinUI 3 as the UI foundation and select supported dependency versions.
- Define the smallest public plugin SDK and host API.
- Decide which functionality runs in separate processes and how it communicates.
- Define dependency, configuration and API-version compatibility behavior.
- Define standard visual contributions and the trusted custom-control path.
- Specify theme, palette, asset-pack, layout and preset formats.
- Establish recovery behavior for host failure as well as plugin failure.
- Bound the initial coexistence release.
- Investigate Windows-specific gaps through focused prototypes.
- Inspect WindowBlinds only if needed to answer concrete questions about external skinning.
- Develop a fresh effort estimate for the new C# project.

No new C# implementation or project repository was created during this discussion.

## Quick reference

| Item | Path or source |
| --- | --- |
| Original reference checkout | `D:\Dropbox\Projects\LiteStep-Original` |
| Clone reference checkout | `D:\Dropbox\Projects\LiteStep` |
| Original inspected head | `2b1c9ee` |
| Clone inspected head | `4158784` |
| Temporary investigation copies/logs | `D:\Dropbox\Documents\Codex\2026-10-26\new-chat-16\inspection-build` |
| Central notes repository | `D:\Dropbox\Notes` |
| Remote notes repository | https://github.com/The-Running-Dev/Notes |
| Framework recommendation | C# + WinUI 3 + Windows App SDK |
| Initial operating mode | Coexistence with Explorer |
| Later operating mode | Tested shell replacement |
| Core product direction | Extensible host, plugins, deep theming, configurable desktop |

## Sources consulted during the discussion

All links below were consulted on 2026-10-26. Documentation establishes the claims described above, not a successful implementation in this project.

### Microsoft

- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/) — native Windows UI framework.
- [Windows App SDK](https://learn.microsoft.com/en-us/windows/apps/windows-app-sdk/) — platform foundation.
- [What is .NET MAUI?](https://learn.microsoft.com/en-us/dotnet/maui/what-is-maui?view=net-maui-10.0) — standard Windows backend.
- [Create a .NET application with plugins](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support) — loading/dependencies and isolation limits.
- [CustomShell policy](https://learn.microsoft.com/en-us/windows/client-management/mdm/policy-csp-admx-winlogon#customshell) — alternate user interface and edition applicability.
- [Shell Launcher overview](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/) — replacement-shell lifecycle and edition requirements.
- [Configure Shell Launcher](https://learn.microsoft.com/en-us/windows/configuration/shell-launcher/configure) — startup and exit behavior.
- [High DPI desktop application development](https://learn.microsoft.com/en-us/windows/win32/hidpi/high-dpi-desktop-application-development-on-windows) — DPI responsibilities.
- [IVirtualDesktopManager](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-ivirtualdesktopmanager) — documented desktop methods.
- [DWM_SYSTEMBACKDROP_TYPE](https://learn.microsoft.com/en-us/windows/win32/api/dwmapi/ne-dwmapi-dwm_systembackdrop_type) — supported backdrop materials.
- [Shell_NotifyIcon](https://learn.microsoft.com/en-us/windows/win32/api/shellapi/nf-shellapi-shell_notifyicona) — application-side tray protocol.
- [RegisterShellHookWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-registershellhookwindow) — documented warning about future availability.

### Stardock

- [WindowBlinds](https://www.stardock.com/products/windowblinds/) — customization, presets, per-application behavior and SkinStudio.
- [WindowBlinds history](https://www.stardock.com/products/windowblinds/history) — examples of ongoing application/Windows compatibility work.
- [SkinStudio](https://www.stardock.com/products/skinstudio/) — visual skin authoring; some linked documentation is historical.
- [IconPackager](https://www.stardock.com/products/iconpackager/) — separate icon-package functionality.

### Enlightenment

- [About Enlightenment](https://www.enlightenment.org/about-enlightenment) — modules, shelves, gadgets, palettes and desktop capabilities.
- [Themes and Edje](https://www.enlightenment.org/docs/themes/start) — packaged theme data, scalable layouts and animation.
- [About EFL](https://www.enlightenment.org/about-efl) — rendering and theme-library structure.
- [Enlightenment](https://www.enlightenment.org/) — window-manager/compositor role.

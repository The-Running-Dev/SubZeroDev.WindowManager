---
title: "Modern Windows Desktop Project Brief"
project: SubZeroDev.WindowManager
date: 2026-10-26
type: guide
tags: [windows-shell, csharp, plugins, theming, project-brief]
---

# Modern Windows Desktop Project Brief

Build a modern C# desktop environment for Windows 11 that users can extend through plugins and transform through themes, layouts and icon packs. Start alongside Explorer, deliver a useful daily desktop, and progressively develop a tested shell-replacement mode.

The foundation should support continued extension without turning every new feature into a change to the host.

## Vision and references

Give users control over how their desktop looks, how it is arranged, and which features it provides. Support both restrained workspaces and expressive desktops with distinctive materials, shapes, graphics and motion.

| Reference | Influence |
| --- | --- |
| LiteStep | A small extensible shell foundation, modular features, commands and configurable composition |
| WindowBlinds and SkinStudio | Installable skins, appearance customization, presets and a path toward deeper Windows/application styling |
| Enlightenment | Beautiful, highly configurable desktops with shelves, gadgets, palettes, visual states and animation |

This is a new project. LiteStep is a behavioral and architectural reference. Reusing its code, legacy module interfaces or theme formats is not required. WindowBlinds and Enlightenment guide the experience; direct compatibility with their packages is a separate possible investigation.

## Requirements and proposed baseline

### Requirements established by Ben

- Implement the project in C# for modern Windows 11.
- Use a modern UI foundation.
- Begin with a desktop that can run on top of the existing Windows desktop.
- Preserve a path to full shell replacement after the integrations mature.
- Provide a substantial share of functionality through plugins.
- Build a base and basic plugins first, then extend them over time.
- Make colors, icons and broader appearance deeply customizable.
- Draw on Enlightenment's beauty and configurability.

### Proposed technical baseline

Use **C# with WinUI 3 and the Windows App SDK**. WinUI is the recommended direction from the discussion; the final framework decision should be confirmed by an early desktop-windowing and theming prototype. MAUI remains a considered alternative, but there is no established cross-platform requirement.

Select supported .NET and SDK releases when implementation begins. The exact versions, minimum Windows build, deployment format and architecture support beyond the initial development target remain open.

WinUI supplies UI infrastructure. Windows interop supplies desktop behavior. Templates, composition and shared visual resources must allow themes to substantially change the appearance.

The project name and implementation repository have not been selected.

## Product model

Keep functionality and presentation independently configurable.

| Part | Responsibility |
| --- | --- |
| Plugins | Commands, data providers, widgets and other desktop functionality |
| Layouts | Panels, desktop placement, sizing, alignment and monitor assignments |
| Themes | Templates, shapes, typography, materials, visual states and transitions |
| Palettes | Shared colors that can change independently of a theme |
| Asset packs | Replaceable icons, wallpapers and other graphics |
| Presets | A saved combination of the above |

A user should be able to change a desktop's visual identity while keeping the same functionality, or replace a feature while keeping the same layout and appearance.

For example, a bottom panel might combine a launcher, running applications, media controls and a clock. These are plugin contributions arranged by configuration. Changing the launcher must not require a host source change.

## Architecture and extension contract

### Host

Keep the host focused on:

- Application lifecycle, diagnostics and recovery.
- Plugin discovery, compatibility, dependencies and lifecycle.
- Creating desktop surfaces and handling monitors, DPI and fullscreen behavior.
- Layout, theme resources and persisted configuration.
- Command routing and shared service contracts.
- Settings and plugin-management infrastructure.

Use a Windows integration layer behind explicit interfaces. Share platform primitives where several plugins need them; keep feature-specific behavior in plugins.

### Feature plugins

Deliver the default clock, launcher and running-window features as separately packaged plugins using the public SDK. Later plugins can add media controls, calendars, monitoring, shortcuts, wallpaper features, tray integration and desktop management.

A plugin declares:

- Stable identity, version and compatible host API versions.
- Dependencies, required capabilities and supported operating modes.
- Commands, data providers, visual contributions and settings.
- Start/stop behavior and resource cleanup.
- Configuration schema and migration behavior.

Plugins communicate through public contracts and commands such as `launcher.open` and `applications.launch`. They do not depend on another plugin's internal implementation. Multiple views should be able to consume one shared data provider.

Treat capability declarations as metadata until an enforcement mechanism is implemented. A manifest alone does not restrict operating-system access.

### Visual contributions and isolation

Use two explicit extension paths:

| Path | Proposed behavior |
| --- | --- |
| Standard contributions | A plugin provides data, actions and supported visual descriptions; the host renders them using the active theme |
| Advanced trusted UI | A plugin can supply custom WinUI controls when the standard model is insufficient |

Design standard contributions so background plugin logic can run in a separate process. If it fails, its contribution can become unavailable and be restarted while the host stays usable.

Custom controls loaded into the host share its process risks. Assembly loading contexts are useful for dependency management, not a crash-isolation or security boundary. Separate processes provide a reliability boundary but do not automatically sandbox access to the machine.

The initial contract, process boundaries and communication transport must be decided before expanding the plugin catalog. Do not promise arbitrary custom-control isolation or universal hot unloading.

## Appearance and customization

Deep customization is part of the foundation. It must reach beyond a selection of accent colors.

Support theme-controlled component templates, icon selection, typography, spacing, borders, materials and interaction states. Provide shared defaults so standard plugin contributions remain coherent when a theme changes. Allow documented overrides where needed.

Enlightenment's shelves and gadgets suggest multiple configurable panel surfaces and desktop widgets. Its independent palettes suggest changing the color scheme without rebuilding the theme. Its theme-driven layouts and animation suggest a visual system that can support markedly different desktop identities.

Keep two scopes distinct:

| Scope | Delivery direction |
| --- | --- |
| Our desktop | Theme the panels, menus, launcher, widgets, icons and other surfaces we own from the beginning |
| Other applications and Windows-wide appearance | Develop dedicated integration plugins after focused research and compatibility testing |

Changing this application's WinUI resources does not automatically change other applications' title bars or controls. WindowBlinds-style external skinning remains a later research and implementation track.

A visual theme editor is a possible later tool. The initial package format and sample themes must make authoring and replacement practical before an editor is built.

## Operating modes and recovery

### Initial coexistence mode

Explorer remains running. The new host provides its own desktop surfaces and features. Installation and normal launch do not require changing the configured Windows shell.

Desktop positioning and work-area changes must be reversible. Normal exit restores what the application changed. Recovery from abrupt host failure needs explicit design and validation; it cannot depend only on cleanup code in the failed process.

Plugins declare dependencies on Explorer or on a particular mode. Overlays must behave sensibly around fullscreen applications, monitor changes and differing DPI settings.

### Later replacement mode

Use the same host, contracts and plugins, adding the Windows integration coverage required when Explorer no longer provides the desktop shell.

Treat replacement mode as a separately validated milestone. Its entry criteria include startup, application activation, taskbar/tray compatibility, recovery and a reliable return to a usable Windows desktop. Edition-specific shell-launch mechanisms must be handled explicitly.

## Proposed first release

Deliver a useful coexistence-mode desktop that proves the architecture.

| Deliverable | Initial scope |
| --- | --- |
| Host and plugin SDK | Discovery, lifecycle, compatibility checks, commands, settings and diagnostics |
| Configurable panel | Arrange plugin contributions and save placement |
| Clock plugin | Time display and configurable settings |
| Launcher plugin | Application shortcuts and basic search |
| Running-windows plugin | Show eligible windows and switch to them |
| Plugin manager | Enable, disable, configure and inspect extensions |
| Theme and asset support | Apply shared visual styling, palettes and replaceable icons |
| Sample customization | Demonstrate two substantially different visual configurations using the same functional plugins |
| Recovery and display handling | Reversible coexistence behavior, isolated-plugin failure handling and an initial monitor/DPI test matrix |

The two visual configurations are a proposed acceptance demonstration, not a requirement for two independent rendering engines.

### Outside the first release

Keep full shell replacement, comprehensive tray/notification hosting, complete virtual-desktop management, external-application skinning, a marketplace, and legacy theme/plugin compatibility outside the initial delivery boundary.

Retain extension points for those capabilities without making their completion a prerequisite for the first useful release.

## Acceptance criteria

The first release is ready for evaluation when:

1. A developer can build a plugin in a separate project against the published SDK and install it without modifying or rebuilding the host.
2. The default feature plugins use that same SDK and packaging mechanism.
3. A user can place, configure, disable and remove a plugin contribution through supported configuration or UI.
4. Layout and plugin settings survive restart.
5. Changing the theme, palette or icon pack updates standard contributions consistently without replacing their functionality.
6. The two sample visual configurations demonstrate a meaningful change in appearance.
7. A failed isolated plugin process leaves the host responsive and gives the user a recoverable unavailable state.
8. Normal exit restores desktop changes; abrupt-failure recovery is demonstrated in a defined test scenario.
9. The clock, launcher and running-window features work in coexistence mode.
10. Monitor changes, mixed-DPI behavior, keyboard access and fullscreen applications pass a documented initial test matrix.

Performance needs measurable startup, idle-resource and interaction-response budgets before release. Set those budgets after the first working prototype rather than inventing targets in advance.

## Delivery sequence and decisions

| Milestone | Result |
| --- | --- |
| Foundation prototype | Confirm WinUI window behavior, a themeable surface and one separately built plugin; demonstrate an isolated worker failure |
| Functional desktop | Integrate the clock, launcher, running-window plugin, configuration and plugin manager |
| Customization and stability | Demonstrate distinct visual configurations, icon replacement, persistence and recovery/display checks |
| Expanded platform | Add further plugins and investigate deeper Windows integrations; qualify replacement mode separately |

Resolve early:

- Project name, repository and implementation location.
- UI framework confirmation, supported dependencies and initial Windows test targets.
- Plugin package/manifest format, API versioning and dependency behavior.
- Process isolation, communication transport and trusted custom-UI policy.
- Theme/template, layout, palette, asset and preset schemas.
- Recovery ownership and deployment/update behavior.
- Measured performance budgets and first-release test coverage.

No delivery date is set. The earlier estimates concerned legacy modernization and are not a validated schedule for this new implementation.

## References

- [Full discussion and LiteStep investigation](2026-10-26-windows-shell-plugins-themes-architecture.md)
- [WinUI 3](https://learn.microsoft.com/en-us/windows/apps/winui/winui3/)
- [.NET plugin loading and isolation limits](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support)
- [WindowBlinds](https://www.stardock.com/products/windowblinds/)
- [About Enlightenment](https://www.enlightenment.org/about-enlightenment)
- [Enlightenment themes and Edje](https://www.enlightenment.org/docs/themes/start)

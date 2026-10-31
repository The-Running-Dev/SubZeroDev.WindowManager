# Brief — SubZeroDev.WindowManager

> The problem, the non-goals and the definition of done are mine. A model may interrogate this
> file (`/brief`) or type it from my answers (`/interview`). It may not originate it from an idea.

Sources: [notes/2026-10-26-windows-shell-plugins-themes-architecture-brief.md](../notes/2026-10-26-windows-shell-plugins-themes-architecture-brief.md) (the project brief) and [notes/2026-10-26-windows-shell-plugins-themes-architecture.md](../notes/2026-10-26-windows-shell-plugins-themes-architecture.md) (the full discussion and LiteStep investigation).

## Problem
Build a modern C# desktop environment for Windows 11 that users extend through plugins and transform through themes, layouts, palettes and icon packs. It starts by running on top of the existing Windows desktop alongside Explorer, delivers a useful daily desktop, and keeps a path open to full shell replacement once the integrations mature.

Requirements stated by Ben:
- This is a new project, not a copy of LiteStep. LiteStep may be used as a behavioural and architectural reference; reusing its code, module interfaces or theme formats is not required.
- Implement it in C#, with a modern UI technology, for modern Windows 11.
- Begin as a desktop running on top of the existing Windows desktop; it does not have to replace the shell initially.
- Preserve a path to replacing the Windows shell after the kinks are worked out.
- Provide a substantial share of functionality through plugins, not built into the host: build an extensible base, then basic plugins, then keep extending those plugins.
- Make colours, icons and broader appearance deeply customisable, in the spirit of WindowBlinds.
- Bring in the beauty and customisability of Enlightenment.

Product model (from the supplied brief): functionality and presentation are independently configurable — plugins (commands, data providers, widgets), layouts (panels, placement, monitor assignment), themes (templates, shapes, typography, materials, visual states, transitions), palettes (colours, independent of the theme), asset packs (icons, wallpapers, graphics), and presets (a saved combination). Changing the visual identity keeps the functionality; replacing a feature keeps the layout and appearance. Default features ship as separately packaged plugins built on the same public SDK offered to third parties.

## What we do instead today (and what it costs)
Explorer's own desktop. LiteStep (`D:\Dropbox\Projects\LiteStep-Original`, `D:\Dropbox\Projects\LiteStep`) was investigated as a reference; it does not build as-is, and its Theme Engine V2 stops at parsing (details in the discussion note).

## Who it is for
Users who want control over how their desktop looks, how it is arranged, and which features it provides. Plugin developers building against the published SDK in separate projects.

## Non-goals
Outside the first release (the supplied brief keeps extension points for these, without making them prerequisites):
- Full shell replacement.
- Comprehensive tray/notification hosting.
- Complete virtual-desktop management.
- Skinning external applications or Windows-wide appearance.
- A plugin/theme marketplace.
- Compatibility with legacy LiteStep, WindowBlinds or Enlightenment theme and plugin formats.

## Definition of done
The first release is ready for evaluation when:
- A developer can build a plugin in a separate project against the published SDK and install it without modifying or rebuilding the host.
- The default feature plugins use that same SDK and packaging mechanism.
- A user can place, configure, disable and remove a plugin contribution through supported configuration or UI.
- Layout and plugin settings survive restart.
- Changing the theme, palette or icon pack updates standard contributions consistently without replacing their functionality.
- Two sample visual configurations, using the same functional plugins, demonstrate a meaningful change in appearance.
- A failed isolated plugin process leaves the host responsive and gives the user a recoverable unavailable state.
- Normal exit restores desktop changes; abrupt-failure recovery is demonstrated in a defined test scenario.
- The clock, launcher and running-window features work in coexistence mode.
- Monitor changes, mixed-DPI behaviour, keyboard access and fullscreen applications pass a documented initial test matrix.
- Measurable startup, idle-resource and interaction-response budgets exist and are met (budgets set after the first working prototype, not in advance).

First-release deliverables: host and plugin SDK (discovery, lifecycle, compatibility checks, commands, settings, diagnostics); a configurable panel; clock, launcher (shortcuts and basic search) and running-windows plugins; a plugin manager (enable, disable, configure, inspect); theme and asset support (shared styling, palettes, replaceable icons); the two sample configurations; recovery and display handling.

## Narrowest useful version
The foundation prototype: a themeable desktop surface running alongside Explorer, one separately built plugin loaded by the host, and a demonstrated isolated-worker failure that leaves the host usable. The functional desktop (clock, launcher, running windows, configuration, plugin manager) follows; then customisation and stability; then the expanded platform and a separately qualified replacement mode.

## Environment
- Windows 11 desktop, single user, local and offline.
- C#.
- Coexistence mode: Explorer keeps running; installation and normal launch do not change the configured Windows shell. Desktop positioning and work-area changes are reversible.
- Multiple monitors, mixed DPI, monitor changes and fullscreen applications must be handled.
- Replacement mode (later) uses the same host, contracts and plugins, and must handle edition-specific shell-launch mechanisms (CustomShell policy, Shell Launcher on Enterprise/Education/IoT) explicitly.
- Project location: `D:\Dropbox\Projects\SubZeroDev\WindowManager`.

## Lifespan
Maintained: a base that keeps being extended with plugins over time.

## Assumptions and open questions
- Assumed: the project name is SubZeroDev.WindowManager, taken from the notes folder and the project location.
- Assumed: the UI foundation is WinUI 3 with the Windows App SDK — the discussion's recommendation, to be confirmed by the foundation prototype's windowing and theming checks. MAUI was considered; there is no cross-platform requirement.
- Assumed: Ben is the first user; the plugin SDK is meant to be usable by other developers (first criterion), but distribution to other users is not part of the first release.
- Assumed: the two sample visual configurations are an acceptance demonstration, not a requirement for two rendering engines.
- Assumed: plugin capability declarations are metadata only until an enforcement mechanism exists; they do not restrict operating-system access.
- Open: .NET and Windows App SDK versions, minimum Windows build, architectures beyond the development machine, and deployment/update format — affects packaging, the plugin SDK's target framework and test targets.
- Open: whether this folder becomes a git repository and which GitHub remote it uses — affects the `/agentkit:next` build loop, which works through branches and pull requests.
- Open: the initial monitor/DPI test matrix — which physical or virtual display setups are available to test against.

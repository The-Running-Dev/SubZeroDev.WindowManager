# Brief — SubZeroDev.WindowManager

> The problem, the non-goals and the definition of done are mine. A model may interrogate this
> file (`/brief`) or type it from my answers (`/interview`). It may not originate it from an idea.

Sources: [notes/2026-10-26-windows-shell-plugins-themes-architecture-brief.md](../notes/2026-10-26-windows-shell-plugins-themes-architecture-brief.md) (the project brief) and [notes/2026-10-26-windows-shell-plugins-themes-architecture.md](../notes/2026-10-26-windows-shell-plugins-themes-architecture.md) (the full discussion and LiteStep investigation).

## Problem
Build a modern C# desktop environment for Windows 11 that users extend through plugins and transform through themes, layouts, palettes and icon packs. It starts by running on top of the existing Windows desktop alongside Explorer, delivers a useful daily desktop, and then replaces the Explorer shell altogether, as LiteStep did, with the same host, plugins and themes.

Requirements stated by Ben:
- This is a new project, not a copy of LiteStep. LiteStep may be used as a behavioural and architectural reference; reusing its code, module interfaces or theme formats is not required.
- Implement it in C#, with a modern UI technology, for modern Windows 11.
- Begin as a desktop running on top of the existing Windows desktop; it does not have to replace the shell initially.
- Preserve a path to replacing the Windows shell after the kinks are worked out.
- Replace the Windows Explorer shell altogether, as LiteStep did: the app becomes the shell Windows starts at sign-in, with Explorer not running (Ben, 2026-11-01, superseding the earlier non-goal "Full shell replacement").
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
- Toast notifications and the notification centre. Hosting the notification area (other applications' tray icons) is in scope for replacement mode.
- Virtual-desktop management. Task View and virtual desktops belong to Explorer, so they are unavailable in replacement mode.
- Skinning external applications or Windows-wide appearance. It needs code injected into other processes.
- Replacing File Explorer's folder windows. In replacement mode they still come from `explorer.exe`, started as an ordinary application.
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
- The clock, launcher and running-window features work in coexistence mode and in replacement mode.
- Replacement mode: with the app registered as Ben's shell, signing in starts it instead of Explorer and gives a complete daily desktop. That means panels, the launcher and running windows; the notification area with other applications' icons; a desktop with wallpaper and icons; the Windows key; lock, sign out, restart, shut down and sleep; volume, network and battery indicators; and File Explorer windows on demand. Registering and unregistering need no administrator rights. If the host cannot start, the session falls back to Explorer without Ben doing anything.
- Monitor changes, mixed-DPI behaviour, keyboard access and fullscreen applications pass a documented initial test matrix.
- Measurable startup, idle-resource and interaction-response budgets exist and are met (budgets set after the first working prototype, not in advance).

First-release deliverables: host and plugin SDK (discovery, lifecycle, compatibility checks, commands, settings, diagnostics); a configurable panel; clock, launcher (shortcuts and basic search) and running-windows plugins; a plugin manager (enable, disable, configure, inspect); theme and asset support (shared styling, palettes, replaceable icons); the two sample configurations; recovery and display handling; replacement mode (shell registration and the way back to Explorer, shell roles and start-up programs, the notification-area and app-bar host, the desktop, the Windows key and session commands, system indicators).

## Narrowest useful version
The foundation prototype: a themeable desktop surface running alongside Explorer, one separately built plugin loaded by the host, and a demonstrated isolated-worker failure that leaves the host usable. The functional desktop (clock, launcher, running windows, configuration, plugin manager) follows; then customisation and stability; then the expanded platform; then replacement mode, built and qualified as its own milestone until Explorer is no longer needed.

## Environment
- Windows 11 desktop, single user, local and offline.
- C#.
- Coexistence mode: Explorer keeps running; installation and normal launch do not change the configured Windows shell. Desktop positioning and work-area changes are reversible.
- Multiple monitors, mixed DPI, monitor changes and fullscreen applications must be handled.
- Replacement mode uses the same host, contracts and plugins. It is registered per user, through the account's own `Winlogon\Shell` value. It detects and reports the edition-specific mechanisms that override that value (the Custom User Interface policy, Shell Launcher on Enterprise/Education/IoT) rather than changing them. Ben's machine runs Windows 11 Enterprise.
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

### Second release (drafted 2026-11-02, assumed until Ben confirms)

Ben asked for the next brief once the first release's slices had merged. The stated goal it serves is the Problem's: the app replaces the Explorer shell altogether. Nothing below is stated by Ben yet, so each entry stays `Assumed:` or `Open:` until he confirms, corrects or drops it.

- Assumed: the second release closes the gaps the first release leaves in replacement mode, as recorded in `docs/replacement-mode.md` § *Features Explorer hosts*. Done means a working day in replacement mode needs nothing that only Explorer provides.
- Assumed: in scope, in this order of priority:
  1. **Other applications' notifications.** They show as they arrive, and a history can be opened, acted on and cleared. This lifts the first-release non-goal "Toast notifications and the notification centre".
  2. **Virtual desktops.** Several sets of windows, switched and moved between from the keyboard and from a panel item. This lifts the first-release non-goal "Virtual-desktop management".
  3. **Quick settings beyond volume, network and battery.** Bluetooth on and off, display brightness, and the other common toggles, reachable from one popup.
  4. **The remaining Windows keys and menus.** A power-user menu in place of Win+X, with Win+A, Win+N and Win+Tab reaching their replacements.
  5. **Taskbar depth in running windows.** Grouping by application, a thumbnail on hover, jump lists, and progress and overlay badges.
  6. **The desktop.** The usual file context menu on desktop icons, and wallpaper chosen per monitor.
- Assumed: each in-scope feature ships as a plugin built on the published SDK, as the first release's features do, and works in replacement mode with Explorer not running.
- Assumed definition of done for the second release:
  - every in-scope feature works in replacement mode with Explorer not running;
  - in `docs/replacement-mode.md` § *Features Explorer hosts*, no row in scope still reads *Missing*;
  - the first release's startup, idle and reaction budgets are still met;
  - automated tests never touch the user's real notifications, desktops, devices or settings, as the first release's tests do not.
- Open: whether Windows delivers other applications' notifications to a shell other than Explorer on build 26200. It decides whether item 1 can be built at all, so a check on the real machine comes before `/design`.
- Open: virtual desktops as Windows' own, or as the shell's own sets of windows. Windows' own belong to Explorer, so they are absent in replacement mode. The shell's own work without Explorer, but applications that ask Windows which desktop they are on will not see them. The cost and the behaviour differ materially.
- Open: whether second-release features also run in coexistence mode, where Explorer already provides them. Win+A, Win+N and Win+Tab would otherwise conflict with Explorer's own handling of those keys.
- Open: whether these belong in the second release or later: custom plugin UI drawn inside the host's own windows (deferred by the first release's design), a theme editor, and automatic updates. Each one widens the release's scope.
- Open: the manual checks in issue #28, S17.7 (the per-user shell value) and S23.4 (a cold start) above all. A failure there changes replacement mode itself, and this release builds on it.

# Second-release design handoff — shared window model

Continue the **second-release design for SubZeroDev.WindowManager**, using its repository, project notes and current design brief. This is design work; do not begin implementation.

**Take approach A: one authoritative window model owned by the host, exposed through the published SDK.** Premises 1–6 remain agreed. Preserve the brief’s priorities and scope.

The host owns window discovery, lifetime identity, monitor and desktop membership, shared application/group identity, and progress/overlay state. It publishes a window feed and exposes commands for activation, desktop switching and moving windows. Feature plugins consume those public contracts.

Keep running windows, Task View/Win+Tab, Alt+Tab and Win+X as separately packaged plugins. Their presentation, filtering, ordering and interactions remain plugin behavior. Share window facts wherever relevant; do not force every feature to display an identical window set or make unrelated menu actions depend on the window model.

Use the existing host-feed/plugin-view split for `host.tray.icons` as the architectural precedent. Add a host-rendered thumbnail element to the public SDK so workers can request and arrange thumbnails without drawing their own windows.

Keep notifications independent: the packaged notification worker publishes its feed, and plugin contributions supply pop-ups and the centre through host rendering. Preserve the agreed notification premises and record any remaining validation gates without reopening settled decisions.

The important implementation constraint to capture in the design is that **the authoritative registry cannot simply be today’s visible running-windows list moved into the host**. Windows hidden because they belong to another shell desktop must remain tracked. Separate window existence and desktop membership from visibility and each view’s eligibility rules. Distinguish a particular window’s lifetime identity from its application grouping identity.

Make extracting the shared model and migrating running windows onto it the first **window-related** slice. This does not move virtual desktops ahead of notifications in the release priorities. Specify compatibility or explicit versioning for the published `windows.running` feed and existing window commands; avoid leaving two authoritative trackers active.

Update the design, public contracts, ordered slices and decision ledger to make this concrete. Keep the host boundary limited to shared platform capabilities, state-changing commands, recovery and rendering primitives. Grouping presentation, jump-list menus and other feature behavior stay in plugins.

Include acceptance checks demonstrating that:

- Desktop changes produce consistent results across running windows and switchers.
- Windows remain tracked while hidden on another desktop.
- Moving or activating a window has a defined outcome when it closes during the operation.
- Disabling or crashing a feature plugin leaves shared tracking available.
- Host failure and restart recover shell-hidden windows without stranding them.
- Independently built plugins can consume the feed, commands and thumbnail element through the published SDK.
- Existing resource budgets and test isolation requirements remain satisfied.

Explain the larger initial window-model slice openly. The reason for choosing A is that desktop membership and window identity are shared shell state, and independent plugins should consume that state through a stable contract.

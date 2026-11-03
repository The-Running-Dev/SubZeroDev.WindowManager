# Replacement mode: the app as your Windows shell

In coexistence mode (the default) the app runs alongside Explorer. In replacement mode Windows starts the app at sign-in instead of Explorer, and Explorer does not run.

Nothing here needs administrator rights. Everything is per user.

## Turning it on and off

```text
SubZeroDev.WindowManager.exe --shell on       make the app your shell from the next sign-in
SubZeroDev.WindowManager.exe --shell off      put back what was there before
SubZeroDev.WindowManager.exe --shell status   print the registration as JSON
```

The Appearance page in Settings has a **Use as my shell** switch that runs the same commands.

`--shell on` writes the per-user value `HKCU\Software\Microsoft\Windows NT\CurrentVersion\Winlogon\Shell`:

```text
"<install folder>\SubZeroDev.WindowManager.exe" --mode replacement
```

Before it writes anything, it records the value that was there, or that there was none, in `%LOCALAPPDATA%\SubZeroDev\WindowManager\shell.json`. `--shell off` puts that back exactly, including removing the value when there was none. Running `--shell on` twice keeps the first record.

Sign out and back in for either change to take effect.

### When the per-user value is ignored

Two settings override the per-user value. With either one present, `--shell on` writes nothing, names the setting, and exits 5:

- the Custom User Interface policy: a `Shell` value under `Software\Microsoft\Windows\CurrentVersion\Policies\System`, in HKCU or HKLM;
- Shell Launcher: `HKLM\SOFTWARE\Microsoft\Windows Embedded\Shell Launcher`, for all users or for your account.

Remove the setting, or ask your administrator to, then run `--shell on` again.

## Getting Explorer back

You can always get Explorer back:

- **For this session:** run the `host.shell.restoreExplorer` command, for example from a launcher item or a hotkey binding. The host exits normally and the guardian starts Explorer. With the argument `{"unregister": true}` it also does what `--shell off` does, so the next sign-in gets Explorer too.
- **At sign-in:** hold Shift while you sign in. The guardian starts Explorer and does not start the host.
- **When the app keeps failing:** when the host fails more often than `restartPolicy.maxRestarts` allows within its window, the guardian starts Explorer before it shows its message.
- **For good:** `--shell off`, or the **Use as my shell** switch, then sign out and in.

In coexistence mode `host.shell.restoreExplorer` does nothing and returns `NotInReplacementMode`.

### Starting Explorer while the app is still registered

Explorer only takes the shell role (taskbar, desktop, Start) when the shell value names Explorer. Started while the value names the app, it may open only a folder window. So whenever the guardian starts Explorer while the app is registered, it sets the value to `explorer.exe`, waits up to 10 seconds for Explorer's shell window, then puts the app's value back. If the guardian is killed in between, the value names Explorer, and the next sign-in gets Explorer. That is the safe direction.

## When another shell is already running

`--mode replacement` checks whether another process owns the shell window. If one does, the guardian exits 6 and changes nothing. Run it with `--take-over` to ask Explorer to exit the way its own **Exit Explorer** command does, so Windows does not restart it, and continue.

## Plugins in replacement mode

The guardian passes `--mode replacement` to the host, and the host passes `--wm-mode replacement` to every plugin worker. A plugin whose manifest:

- has `modes` without `replacement`, or
- sets `requiresExplorer: true`

is not started. It shows as `Incompatible` with `ModeUnsupported`, and the Plugins page says why.

## What the host does as the shell

In replacement mode the host takes the roles Explorer plays:

- **Shell window and shell hook.** Its desktop window becomes the shell window, and a hook window hears windows come, go, activate and flash. If another process already holds the shell window, the host logs `ShellRoleTaken` and runs without the roles. Everything else still starts.
- **Desktop ready.** Once the first layout is placed, the host signals `ShellDesktopSwitchEvent` and sends `WM_USER` to its desktop window, once, so Windows takes down the welcome screen.
- **Minimised windows.** They are hidden (`ARW_HIDE`) instead of piling up at the bottom of the screen. Use the running-windows list to bring them back. The setting lasts for the session only.
- **Work area.** A `reserve` panel sets the work area itself. The original is journalled first, so if the host dies the guardian puts it back.
- **Start-up programs.** The host runs them once per session, in Explorer's order: HKLM RunOnce, RunOnceEx, HKLM Run, HKCU Run, the common Startup folder, your Startup folder, then HKCU RunOnce. Sources turned off by the `Explorer` policy values are skipped. A restarted host starts nothing again.
- **Folder windows.** `host.files.open {"path": "C:\\Some\\Folder"}` opens a File Explorer folder window. Explorer does not become the shell.
- **Notification area.** The host's `Shell_TrayWnd` hears the icons applications add, and broadcasts `TaskbarCreated` so running apps add theirs again. The `subzerodev.tray` plugin shows them as buttons with their tooltips and the latest balloon. A click selects the icon and a right click opens its menu, with the app allowed to take the foreground first. If another process already holds `Shell_TrayWnd`, the host logs `TrayTaken` and runs without a notification area.
- **Desktop and wallpaper.** A window per monitor at the bottom of the z-order draws the preset's `wallpaper` with its `fit`, or the system wallpaper when the preset names none. Switching presets changes it. `host.desktop.show` minimises every normal window, and a second call restores them.
- **Desktop icons.** The `subzerodev.desktop-icons` gadget lists your Desktop and the public Desktop, sorted by name, and follows changes. A double click or Enter opens an icon as Explorer would.
- **Windows key.** A lone press of the Windows key, or Ctrl+Esc, opens the launcher. Win+R opens it in run mode, Win+E opens This PC, Win+D shows the desktop and Win+M minimises every window. Other Windows-key shortcuts, Win+L among them, still go to Windows, and the Start menu stays closed.
- **Power menu.** Add the `power` item from plugin `subzerodev.session` to a panel. It locks, sleeps, signs out, restarts or shuts down; the last three ask first. An application with unsaved work may refuse, and the menu says so. The same actions are the `host.session.*` commands.
- **Volume, network and battery.** Plugin `subzerodev.indicators` shows each as a panel item. The volume popup has a mute switch and a level slider (the arrow keys move it by 2 %); the network and battery popups show the state and open the matching Settings page. The battery item hides itself on a machine without a battery.
- **Built-in layout.** A preset that uses the built-in `subzerodev.default` layout gets the `subzerodev.shell` layout in replacement mode: launcher, running windows, notification area, the notifications bell, the three indicators, clock and power menu along the bottom. Save your own `subzerodev.default` to keep it instead.
- **Notifications.** Plugin `subzerodev.notifications` shows other applications' Windows notifications as pop-ups in a corner (`toastCorner` in `settings.json`: `topLeft`, `topRight`, `bottomLeft` or `bottomRight`), lists them in a centre opened from the bell or with Win+N, and opens, dismisses or clears them. The bell's settings are `popupSeconds` (2–30, default 6), `maxVisible` (1–5, default 3) and `popups`. Do-not-disturb (`host.notifications.setDoNotDisturb {"on": true}`) keeps the pop-ups away but not the history. The shell keeps no notification text on disk or in its logs; only `lastSeen` and the do-not-disturb choice are saved. Other Windows-key chords can be bound in `settings.json` under `windowsKeys` (chord to command id, for example `"Win+B": "notifications.openCentre"`); the file is read once at start-up, so a change needs a restart, and a chord is swallowed only while its command exists and is available.
- **Notification setup.** Windows only lets an application read other applications' notifications when it has a package identity and you have allowed it, so the one-time steps are yours; the shell changes none of these settings. Build the sparse package, then run `register.ps1` from `src/SubZeroDev.WindowManager.Plugins.Notifications/package`. It prints the `Import-Certificate` command that trusts the package's certificate; run that yourself from an elevated prompt. Then allow *Notification access* for SubZeroDev Notifications in Settings › Privacy & security › Notifications, and switch notifications on. Until each step is done the bell shows *Notifications need setting up* and its Set up page names the step that is missing (`IdentityMissing`, `AccessDenied` or `NotificationsOff`). Checks S24.1 and S24.9 are done by hand on Ben's machine.
- **Other applications' app bars.** A docked toolbar gets its edge from the same work-area service as the host's `reserve` panels, so the two never overlap. `ABM_GETTASKBARPOS` reports the primary monitor's first panel as the taskbar.

Add the tray item to a panel with plugin `subzerodev.tray`. In coexistence mode it shows as `Incompatible`, because Explorer keeps the notification area.

Flashing windows mark their running-windows button for attention until you activate them.

## Qualified for daily use

Each criterion the brief sets for replacement mode, with what the automated tests show and the result of a week of daily use on Ben's machine (Windows 11 Enterprise 26200). The week's column is filled in by hand; until then it says *pending*.

| Criterion | Automated evidence | Week of daily use |
| --- | --- | --- |
| Signing in starts the app instead of Explorer | S17 registration and restore; S18 guardian start with faked shell roles | *pending* (S17.7, S23.4) |
| Panels, the launcher and running windows | S1, S7 and S8 in both modes; the `subzerodev.shell` layout (S22.5) | *pending* |
| Notification area with other applications' icons | S19 with a tray client of the tests' own | *pending* |
| Desktop with wallpaper and icons | S20 with files of the tests' own | *pending* |
| The Windows key | S21.1–S21.3 with marked keys | *pending* (S21.6) |
| Lock, sign out, restart, shut down and sleep | S21.4–S21.5 with a recording fake | *pending* (S21.6) |
| Volume, network and battery indicators | S22.1–S22.5 with fake devices | *pending* |
| File Explorer windows on demand | S18 `host.files.open` with a faked start | *pending* (S18.7) |
| No administrator rights to register or unregister | S17: per-user value only | *pending* |
| Falls back to Explorer when the host cannot start | S17 restart budget and fallback with a faked Explorer | *pending* (S23.3) |
| Sign-in to a usable desktop within budget | S23.2: `replacementStartupMs` in [budgets.md](budgets.md) | *pending* |

### Features Explorer hosts

These come from Explorer or processes it hosts, so in replacement mode they work, degrade or are missing. The expected result is recorded here; the week confirms or corrects it.

| Feature | Expected | Instead |
| --- | --- | --- |
| Start menu and search | Missing; the Start menu stays closed | The launcher (Windows key, Ctrl+Esc) |
| Taskbar | Missing | Panels with running windows |
| Toast notifications | Missing: applications that rely on them show nothing | None in the first release |
| Notification centre and calendar flyout | Missing | The clock item |
| Quick Settings (Win+A) | Missing | The volume, network and battery items; Settings for the rest |
| Task View and virtual desktops (Win+Tab, Win+Ctrl+arrows) | Missing | None in the first release |
| Alt+Tab | Degrades to the classic switcher Windows keeps without Explorer | Running windows |
| Win+X menu | Missing | The launcher and the power item |
| Snap layouts flyout | Degrades: Win+arrow snapping stays, the layouts flyout may not appear | — |

## Ways back to Explorer, checked on a real sign-in

Each is exercised once with the app registered (S23.3). Until then each says *pending*.

| Way back | Result |
| --- | --- |
| `--shell off`, then sign out and in | *pending* |
| **Use as my shell** switch in Settings | *pending* |
| `host.shell.restoreExplorer` | *pending* |
| Shift held at sign-in | *pending* |
| Automatic fallback after a host that cannot start (a broken install) | *pending* |

## Guardian exit codes

| Code | Meaning |
| ---: | --- |
| 0 | Normal exit. |
| 2 | Another guardian is already running. |
| 3 | The restart budget is spent and the message was dismissed. |
| 4 | The host did not start. |
| 5 | `--shell on`: a policy or Shell Launcher overrides the per-user value. |
| 6 | `--mode replacement`: another process owns the shell window. |

## Known gaps

- **Is the per-user value honoured on your build?** Windows 11 Enterprise 26200 is expected to honour it. If it does not, the fallback is the per-user Custom User Interface policy value, and that switch will be a decision entry. This is checked by hand (S17.7): after `--shell on` and signing out and in, the guardian runs and `explorer.exe` does not; after `--shell off` and another sign-in, Explorer is back.
- Real audio, network and battery devices, and whether each `ms-settings:` page opens without Explorer, are checked by hand; the tests use a file of their own. The indicators show text labels until an asset pack gives their icons images. Wi-Fi networks are chosen in Settings, not in the popup.
- The real Windows key and the five power commands are checked by hand; the tests inject marked keys the host swallows and record the commands. Win+Tab, virtual-desktop keys and the Win+X menu are not handled.
- Real tray apps and docked toolbars are checked by hand; the tests use a client of their own. Other app bars are not told (`ABN_POSCHANGED`) when the host's own panels change.
- Your real wallpaper and Desktop folders are checked by hand; the tests use files of their own. Desktop icons stack in one column, with no wrapping grid, and cannot be dragged.
- A window that changes its icon can be marked for attention as if it flashed, until you activate it.
- The week of daily use, the ways back on a real sign-in and a cold restart without `explorer.exe` are recorded by hand in the tables above.

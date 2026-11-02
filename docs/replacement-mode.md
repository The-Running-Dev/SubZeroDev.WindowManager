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

Flashing windows mark their running-windows button for attention until you activate them.

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
- Until later slices land, replacement mode has no notification area (S19), no desktop or wallpaper (S20), no Windows key or session commands (S21) and no system indicators (S22).
- A window that changes its icon can be marked for attention as if it flashed, until you activate it.

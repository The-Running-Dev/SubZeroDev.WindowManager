# Test the built shell in a VM

No Codex, Git, SDK or GitHub login is needed in the guest. Use this checklist at
the VM console and bring the evidence ZIP back to the development machine.
Commands below assume installation at `C:\WindowManager`. Use a normal test user.

## Install and start

Install Windows 11 x64 and the hypervisor's guest graphics tools. Take a snapshot
that can be restored from outside the guest. Open 64-bit Windows PowerShell:

```powershell
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
$setup = Join-Path $env:TEMP 'Start-AcceptanceVM.ps1'
Invoke-WebRequest -UseBasicParsing 'https://raw.githubusercontent.com/The-Running-Dev/SubZeroDev.WindowManager/acceptance-vm-2026-11-05-r2/tools/Start-AcceptanceVM.ps1' -OutFile $setup
powershell.exe -NoProfile -ExecutionPolicy Bypass -File $setup
```

This downloads and verifies the candidate, starts it alongside Explorer, and
opens instructions. The script is saved locally so an error stays visible. It
does not change the machine's execution policy, stop Explorer or sign out.
The old `vm-acceptance-2026-11-05` script has a Windows PowerShell drive-root bug;
use the `acceptance-vm-2026-11-05-r2` URL above.

Open the installed checklist and capture baseline information:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Test-AcceptanceVM.ps1
```

The same runner accepts `-Action Status`, `Start`, `EnableShell`, `RestoreShell`,
`Record`, or `Collect`. Its default is `Checklist`. To use a different install,
pass `-InstallDirectory` to both setup and runner. Results remain in the install's
`acceptance-results` folder across sign-ins. Setup repeats verify installed files;
an existing unrelated installation is preserved, so choose a fresh destination.

## First pass: alongside Explorer

Check that panels and the launcher appear; launch two ordinary applications,
switch between them using running-window buttons, open settings, and check that
the shell remains responsive. Explorer retains shell roles in this mode, so
this first pass does not qualify the replacement tray, desktop or Alt+Tab.
Record the observation, including the exact symptom when something fails:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Test-AcceptanceVM.ps1 -Action Record -Test Coexistence -Result Pass -Notes 'Panels, launcher, settings and app activation worked.'
```

Use `-Result Fail`, `Blocked` or `NotRun` when appropriate. Record only what you
actually observed. Put screenshots and additional `.txt`/`.md` notes in
`C:\WindowManager\acceptance-results`.

## Replacement sign-in

Before replacing Explorer, read `docs\replacement-mode.md` and keep snapshot
restoration accessible. The separate native taskbar transport comparison needs
TaskbarProbe and should be collected before registration when available; it is
not included in this package. If unavailable, record that gate as Blocked. Other
observations below do not qualify taskbar progress or overlays.

After taking a fresh snapshot, explicitly enable this guest user's shell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Test-AcceptanceVM.ps1 -Action EnableShell -SnapshotTaken -Force
```

Sign out and back in manually. Check that the shell starts once and provides
usable panels and desktop without Explorer's taskbar. Capture `-Action Status`
and record `-Test ReplacementSignIn`. If registration fails or Explorer still
owns the desktop, preserve that result rather than killing processes to force
a pass. The runner never stops Explorer, signs out, or reboots automatically.

## Actual input tests under the replacement shell

| Test name for `-Test` | What to do | Observation needed |
| --- | --- | --- |
| `WinX` | Open Win+X and mouse-click a harmless entry such as Settings 20 times; close the opened app between attempts | Count attempts, missed clicks, correct launches and focus handoff. Enter does not qualify mouse selection |
| `Hover` | Hover a running app's button, keep its window changing, move into the preview, then leave | Delayed open, real updating thumbnail, no stolen focus, correct leave/close |
| `AltTab` | Open two real apps; use Alt+Tab, repeated Tab, Shift+Tab, release Alt to commit, Escape to cancel | Correct target/focus, live thumbnails, foreground app menu bar stays closed |
| `Desktop` | Create disposable desktop files; right-click an icon and empty space, rename one, delete another and restore it from Recycle Bin | Correct hit target, rename takes focus, deletion can be restored |
| `Other` | Open a File Explorer folder from the launcher; exercise normal app windows and available indicators | Usability and exact failures. VM hardware absences are Blocked, not Pass |

For example, record missed clicks without reclassifying them as success:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Test-AcceptanceVM.ps1 -Action Record -Test WinX -Result Fail -Notes '4 missed mouse clicks in 20 attempts; Enter worked.'
```

## Restore Explorer and collect results

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Test-AcceptanceVM.ps1 -Action RestoreShell
```

Sign out and back in. Confirm Explorer's taskbar and desktop return; record
`-Test ExplorerRestore`. If the desktop is unusable, use Ctrl+Shift+Esc, Task
Manager's Run new task, and `powershell.exe` to run that restoration command.
If recovery is inaccessible, restore the hypervisor snapshot. Shift held at
sign-in and guardian failure recovery are separate pending tests, not assumed
from a normal restoration.

Collect the observations, build provenance, status captures and application logs:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Test-AcceptanceVM.ps1 -Action Collect
```

Copy the printed `C:\WindowManager\acceptance-results\WindowManager-evidence-*.zip`
back to the host and review its contents before sharing. The collector excludes
settings and notification history, reports missing/unreadable logs, and never
marks acceptance passed. If setup itself fails, its error is saved at
`%TEMP%\WindowManager-bootstrap-error.log`.

## Gates this first pass does not finish

Native taskbar progress/overlays remain unsupported (`TaskbarDecoder.Unverified`
decodes nothing). Native notification history/identity requires separate setup;
opening a notification panel alone is insufficient. TaskbarProbe, compiler,
notification certificates and automated tests are not installed. Real radio,
DDC/CI, built-in display brightness, multi-monitor DPI, Shift/failure recovery,
and a week of daily use need their own evidence. See `docs\acceptance.md` for the
complete ledger. A running application or an evidence ZIP is not a passed gate.

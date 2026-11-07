# Scripted acceptance run

`Invoke-AcceptanceAuto.ps1` runs as much of the [acceptance ledger](../acceptance.md) as a script can, inside the disposable VM, and collects the evidence. It drives the real desktop with `SendInput` and observes it through the host log, window enumeration, UI Automation and screenshots. It never sets the test hook, so the real key pipeline is exercised.

It is available in bundles built after it was added. Check for `C:\WindowManager\Invoke-AcceptanceAuto.ps1`; an older pinned bundle does not carry it.

## Run it

Take a VM snapshot, install with the [one-command setup](VM-START-HERE.md), then in 64-bit Windows PowerShell:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Invoke-AcceptanceAuto.ps1 -ConfirmDisposableVm
```

Leave the VM alone while it runs: it sends real keys and mouse clicks. The coexistence stage takes a few minutes. It refuses to run unless the machine reports both a hypervisor and a VM manufacturer or model, and unless you pass `-ConfirmDisposableVm`.

To include the replacement-shell stage (registers the guest shell, signs out, probes after sign-in, restores Explorer, signs out again and checks Explorer is back), use an elevated PowerShell and add the flags that acknowledge the risk:

```powershell
powershell.exe -NoProfile -ExecutionPolicy Bypass -File C:\WindowManager\Invoke-AcceptanceAuto.ps1 -ConfirmDisposableVm -IncludeReplacement -SnapshotTaken -AllowSignOut
```

The run resumes itself after each sign-in through a scheduled task. **You sign in yourself both times**; no password is handled. Set the guest to sign in automatically if you want it fully unattended.

| Option | Meaning |
| --- | --- |
| `-Only Coexistence,WinX,AltTab,Hover,Desktop,ExplorerRestart` | Run only those probes. |
| `-WinXAttempts 20` | Real-mouse clicks on a Win+X entry (1-200). |
| `-Resume` | Used by the scheduled task. Continues an interrupted run. |
| `-Abort` | Stops the host, restores the previous shell registration and removes the sign-in task. |
| `-ReportOnly` | Rebuilds the report and ZIP from the results already on disk. |

## What you get

Everything lands in `C:\WindowManager\acceptance-results`: `auto-observations.csv`, `auto-report.md`, screenshots, UI Automation dumps and the evidence ZIP. Each scripted gate is also recorded through `Test-AcceptanceVM.ps1 -Action Record`, so the manual runner's `observations.csv` and ZIP include it. Bring the ZIP back to the development machine.

Verdicts: **Pass** (the scripted observation held), **Fail**, **Inconclusive** (recorded as `Blocked`; a person must look at the attached evidence) and **Skipped** (recorded as `NotRun`). Nothing the script could not observe is reported as a pass.

## Scripted

- Coexistence: the host starts beside Explorer, panels exist, 30 s stability, no log errors or warnings of concern.
- S28: Win+X, then a real mouse click on the Run entry, repeated; each attempt classified (launched, click lost, popup closed without launch, launched without focus, popup not opened). The click-loss probe gathers evidence for the open bug; it does not fix it.
- S31: Alt+Tab commits with real chords across three fixture windows, menu-mode control, the Alt-release mask, Esc cancels.
- S29.2: hover over a taskbar button.
- S30: desktop right-click menu, rename, delete to the Recycle Bin and restore, with cleanup.
- Explorer restart while the shell runs.
- Replacement stage (optional): Explorer absent, host owns the tray and desktop, the Windows key opens the launcher, and the way back to Explorer.

## Still manual

- S24.1, S24.9 and issue #28: notification certificate trust, notification access and the switch.
- S27.4: radios from an unpackaged process, DDC/CI monitors and the built-in panel.
- S29.3 badges (needs the native TaskbarProbe, not in the VM package) and S29.4 jump lists beyond the readable subset.
- S30 and S31 judgement calls: how thumbnails and menus look in the attached screenshots.
- A real second monitor, 150 % DPI and full-screen applications ([test matrix](../test-matrix.md)).
- S14.4 sample screenshots.
- A week of daily use and the open design questions.
- The sign-in password, unless the guest signs in automatically.

If the guest becomes unusable, restore the snapshot from the hypervisor. `-Abort` is the in-guest way back.

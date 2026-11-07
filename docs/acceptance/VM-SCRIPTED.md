# Scripted acceptance run

`Invoke-AcceptanceAuto.ps1` runs as much of the [acceptance ledger](../acceptance.md) as a script can, inside the disposable VM, and collects the evidence. It drives the real desktop with `SendInput` and observes it through the host log, window enumeration, UI Automation and screenshots. It never sets the test hook, so the real key pipeline is exercised.

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
| `-Only Coexistence,WinX,AltTab,Hover,Desktop,TaskbarTransport,ShowDesktop,FullScreen,Samples,ExplorerRestart` | Run only those probes. |
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
- S29.3: the native `TaskbarProbe.exe` (shipped in the VM package) runs under Explorer to record an Explorer baseline, then again under the replacement shell with the host taskbar trace switched on (`SUBZERODEV_WM_TASKBAR_TRACE`, set for the run and removed afterwards). Native call counts, HRESULTs and the trace are compared. This is always Inconclusive: a person still has to look at the taskbar.
- S12.6: Win+D hides a real window, the desktop panel stays, a second Win+D restores it, and the panels return below a clicked window.
- S12.3: a borderless full-screen window covers the monitor and holds the foreground; the panels must drop below it and return afterwards. A real game or presentation is recorded as not run.
- S14.4: the two sample presets are switched through `settings.json`, the host log confirms each hot reload, and one screenshot per preset is attached. The settings file is restored afterwards. Whether they look right is Inconclusive for a person.
- S30: desktop right-click menu, rename, delete to the Recycle Bin and restore, with cleanup.
- Explorer restart while the shell runs.
- Replacement stage (optional): Explorer absent, host owns the tray and desktop, the Windows key opens the launcher, and the way back to Explorer.

## Still manual

Each of these needs something a script in a VM cannot supply:

- S24.1, S24.9 and issue #28: a trusted notification certificate and a packaged identity.
- S27.4: radios, DDC/CI monitors and a built-in panel are physical hardware.
- Whether the taskbar visibly draws progress and the overlay (S29.3), jump lists beyond the readable subset (S29.4), and whether the S30, S31 and S14.4 screenshots look right: a person looking.
- A real second monitor and 150 % DPI: the VM has neither ([test matrix](../test-matrix.md)).
- A real full-screen game or presentation.
- A week of daily use and the open design questions.
- The sign-in password, unless the guest signs in automatically.

If the guest becomes unusable, restore the snapshot from the hypervisor. `-Abort` is the in-guest way back.

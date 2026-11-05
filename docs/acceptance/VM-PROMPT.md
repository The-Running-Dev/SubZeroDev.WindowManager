# Optional prompt for an assistant inside the VM

Codex is not required in the VM. For manual testing use [VM-MANUAL.md](VM-MANUAL.md) and the installed Test-AcceptanceVM.ps1 runner. If you choose to install an assistant in the guest, the following prompt is optional:

```text
Continue SubZeroDev.WindowManager acceptance from PR #46:
https://github.com/The-Running-Dev/SubZeroDev.WindowManager/pull/46

This is my disposable Windows 11 x64 VM, prepared for native shell acceptance.
The bootstrap installs to C:\WindowManager and initially launches alongside Explorer.
If it is not installed, run the one-command setup in the repository's
docs/acceptance/VM-START-HERE.md. Read the installed docs/acceptance/VM-START-HERE.md,
docs/acceptance.md, docs/acceptance/INSPECTION.md, docs/replacement-mode.md and
docs/taskbar-backchannel-experiment.md before proceeding. Read vm-install.json
and acceptance-artifacts.json for the exact installed build and hashes.

Inspect this guest and work through the acceptance ledger. Record the Windows
build, graphics support, tested build, commands, expected/observed results,
failures and recovery under C:\WindowManager\acceptance-results. Keep notification
contents and other personal data out of shared evidence. Keep work in PR #46;
fetch its branch into a separate source checkout if code changes are needed.

I authorize installing/running this candidate and performing the documented
session-only experiments and per-user shell on/off tests inside this VM.
Confirm that I have taken an externally restorable VM snapshot before changing
shell registration or stopping Explorer. Keep Explorer configured until the
taskbar comparison is prepared. Do not change my host machine.

Prioritize coexistence and recovery, then native Win+X mouse selection, hover
focus and real thumbnails, notifications, desktop context/rename/Recycle Bin
actions, real Alt+Tab, and the native taskbar transport comparison. Check missing
prerequisites before running a gate: the bootstrap does not install a compiler,
TaskbarProbe, Spy++, notification certificates, or the automated test suite.
Do independent checks while identifying how to obtain those prerequisites.

Implementation slices being done does not establish acceptance. Pointer input
and notification idle CPU remain unresolved. TaskbarDecoder.Unverified decodes
nothing, so do not claim native progress/overlay support. Mark physical radio,
DDC/CI, built-in brightness, real multi-monitor DPI and week-long daily-use gates
as requiring separate evidence. Do not mark missing/skipped gates passed or merge
the PR merely because the application launches. Finish with the evidence paths,
actual results, remaining blockers and the next concrete action.
```

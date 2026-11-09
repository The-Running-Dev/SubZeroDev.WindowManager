# Issue #28: bounded sign-in and recovery drill

This tests S17.7, the five S23.3 ways back, and S23.4. It does not qualify S29,
hardware, other shell features or the week of daily use. No policy mechanism is
changed. The canonical implementation is [guardian startup](../../src/SubZeroDev.WindowManager.Guardian/Program.cs),
[shell registration](../../src/SubZeroDev.WindowManager.Guardian/ShellRegistration.cs),
[Explorer handoff](../../src/SubZeroDev.WindowManager.Guardian/ShellStart.cs) and
[journal recovery](../../src/SubZeroDev.WindowManager.Recovery/Recoverer.cs).
Use [replacement-mode.md](../../docs/replacement-mode.md) for product behavior.

## Boundary and prerequisites

Use a disposable Windows 11 x64 VM or an isolated standard account with a separate
working administrator account. Have console access, an externally restorable
snapshot and a tested way to return to that account. An isolated account alone
is insufficient for deliberately broken-install tests: use a VM snapshot for those.
Save work before each manual sign-out. Run all drill commands as the test user,
in 64-bit PowerShell 5.1 or 7; never switch credentials through Run as administrator.
HKCU would then refer to the wrong user. No SDK, Pester or Git is needed in the guest.

Stage a complete, known candidate in `C:\WindowManager` using the existing
[VM setup](../../docs/acceptance/VM-MANUAL.md), or a complete release publish.
Copy this directory (`Invoke-SignInDrill.ps1` and README.md) from the PR checkout
to `C:\WM-DrillTools`. The older pinned VM bundle does **not** contain this new
harness. Do not mix files from different builds. Retain the candidate's commit,
artifact/run URL, receipt and hashes. Initialize with WindowManager fully exited,
Explorer working, no coexistence Run registration, and no `SUBZERODEV_WM_*`
variables at process, user or machine scope. Fake registry roots, host paths,
data roots and hooks invalidate a real sign-in test; the harness rejects them.
It only accepts an absent per-user Shell or `REG_SZ explorer.exe` and no competing
Shell policy/Launcher value or non-Explorer machine shell. Use another test account instead of clearing someone
else's shell configuration. Disable `startAtSignIn` before initializing.

Keep evidence outside the installation and app data, across sign-ins:

```powershell
$drill = 'C:\WM-DrillTools\Invoke-SignInDrill.ps1'
$e = Join-Path $env:USERPROFILE 'WM-signin-evidence-01'
& $drill -Action Initialize -EvidenceDirectory $e -InstallDirectory C:\WindowManager -IsolatedAccount -SnapshotTaken
# The independent recovery copy and baseline survive a broken installation:
$drill = Join-Path $e 'Invoke-SignInDrill.ps1'
```

`baseline.json` is immutable within the harness: never edit it or reuse a folder
for another candidate. It stores account/computer identity, exact original Shell
presence/type/value, original `shell.json` bytes and candidate hashes. Protect
this folder from other users. A repeated Initialize is refused. Configuration
writes require `-Force`; default Capture changes only evidence files. Register
and Unregister run the actual guardian command with a 15 s limit. A timeout or
nonzero exit means **stop before sign-out** and restore the baseline. Every write
attempt captures before/after state, even on failure. RestoreBaseline requires
neither the guardian executable nor a healthy installation.

Each capture has build/UBR/edition, boot time, token logon ID, session ID,
process PID/parent/owner SID/path/command/creation time, `GetShellWindow` and
`Shell_TrayWnd` owner PIDs, relevant registration/override/Run values, `shell.json`,
restart policy, settings hash and candidate hashes. It retains at most two host
logs, two guardian logs, eight crash records and the current session's recovery
journal, at most 1 MiB each. Metadata reports truncation, missing logs and probe
errors. Plugin logs and full settings are excluded. Review paths, usernames,
command lines and logs before sharing. Any failed probe needed to judge a case
makes that case Blocked until evidence is collected independently.

## Recovery to rehearse before enabling

1. Open Ctrl+Alt+Delete → Task Manager → Run new task → `powershell.exe -NoProfile`.
   Rehearse this from the VM console before registering. Keep this document and
   the recovery command available outside the guest.
2. Prefer `-Action Unregister -Force` (actual `--shell off`) while the guardian is
   intact. It restores registration for the next sign-in; it does not end the
   running replacement session. Use normal host Exit to return to Explorer now.
3. If the install is broken, first capture. Dismiss the repeated-failure dialog
   or normally exit the host and wait for guardian exit. Check the captured PIDs,
   owner SID and paths. If normal exit is impossible, manually end **only this
   test session's** guardian and host in Task Manager, guardian first, then host;
   recheck because the host can briefly relaunch its guardian. Never stop Explorer
   or another user's processes. The harness refuses baseline rollback while either
   process still runs in this session, avoiding a registration-write race.
4. Run the independent recovery copy as the test user:

   ```powershell
   $e = Join-Path $env:USERPROFILE 'WM-signin-evidence-01'
   & "$e\Invoke-SignInDrill.ps1" -EvidenceDirectory $e -Action RestoreBaseline -Test Registration -Force
   ```

   This restores original Shell presence/type/value and original `shell.json`,
   verifies equality, and captures the result. It leaves the registry key itself
   and all unrelated values intact. It does not undo other settings/file edits,
   recover desktop journals, or start Explorer. Once registration is restored,
   run `explorer.exe` from Task Manager, then sign out/in manually and capture
   `-Phase restored`. Check Explorer owns desktop and tray and the desktop is
   usable. A folder window alone is insufficient.
5. If journal cleanup is necessary after a forced kill, restore renamed host files
   first, then run `C:\WindowManager\SubZeroDev.WindowManager.exe --recover-only`
   once with no host running. Capture errors and check wallpaper/work area/hidden
   windows. If recovery fails, the desktop is unusable, the wrong account is open,
   or console access is lost, restore the external VM snapshot. Do not improvise
   HKLM/policy changes. Out-of-band recovery is a failed/blocked case, never a pass.

## Common capture and result commands

All examples use the same `$drill`/`$e` above; redefine these after each sign-in.
Capture before and immediately after each operator action. Do not open folder
windows before the first replacement capture: Explorer should initially be absent.

```powershell
& $drill -EvidenceDirectory $e -Action Capture -Test Registration -Phase before
& $drill -EvidenceDirectory $e -Action Register -Test Registration -Force
# Operator signs out/in; no script does it, and do NOT use --take-over.
& $drill -EvidenceDirectory $e -Action Capture -Test Registration -Phase after
```

For each case, supply an observed result and paths relative to `$e`, including
before, after and restored capture folders plus screenshots/notes when useful:

```powershell
& $drill -EvidenceDirectory $e -Action Record -Test Registration -Result Pending
# Replace these placeholders with real capture directory names after exercising:
& $drill -EvidenceDirectory $e -Action Record -Test Registration -Result Pass -Notes 'Observed at console; build ..., logon IDs ..., shell owner ...; usable desktop; restored sign-in ...' -Evidence @('CAPTURE-before','CAPTURE-after','CAPTURE-restored')
```

`Record` is an append-only operator assertion, not a computed verdict. Existing
evidence paths and notes are required for Pass/Fail/Blocked. Later results never
erase earlier failures. Identify which run supersedes an observation and why.
Missing exercise is Pending, missing prerequisites is Blocked. Collection never
marks anything passed. A snapshot reversion may discard guest evidence: copy it
out before reverting and use a fresh evidence directory for the next run.

## Sequence and acceptance observations

Start every row from a clean Explorer sign-in and restored baseline, with the
same candidate. Use Register, sign out/in and capture the new session before
exercising its recovery action. After **every** row, unregister, exit normally,
RestoreBaseline, sign out/in and capture `-Phase restored` for that row. Retain
the application's own `--shell off` result **before** the harness restores its
baseline; baseline rollback must not conceal an application restoration defect.

| Test | Operator action | Evidence required before a Pass |
| --- | --- | --- |
| Registration (S17.7) | Register as above; manually sign out/in without Shift | Shell value exactly names candidate guardian with `--mode replacement`; `shell.json.previous` matches baseline; different token logon ID, guardian starts host in replacement mode, host owns shell and tray, initially no Explorer in this session; usable desktop; paths/hash/build known. Record console timing and screenshot. Then verify ShellOff below |
| ShellOff (S17.7/S23.3) | In the real replacement session run `-Action Unregister -Test ShellOff -Force`; capture before signing out, then sign out/in | Original Shell presence/type/value restored by app; `shell.json.registered` false; new logon starts usable Explorer shell/tray, no guardian/host. Capture this before RestoreBaseline |
| Settings (S23.3) | Open Settings → Appearance; switch **Use as my shell** off; capture; sign out/in | Same app registration/restored-session checks as ShellOff; switch reports success. Do not use CLI off as a substitute for this observation |
| RestoreCommand (S23.3) | From an actual replacement host, invoke `host.shell.restoreExplorer` via the configured hotkey or launcher command | Normal host/guardian exit, recovery journal clean, Explorer owns shell/tray in the **same logon**, usable desktop; registration still names app. This is a session-only recovery; subsequently unregister and verify next sign-in |
| Shift (S23.3) | Register, sign out; hold Shift through sign-in until desktop appears | New logon; guardian log says `Shift is held; starting Explorer instead.`; no host start in that logon, Explorer owns shell/tray; after handoff registration still names app. Release Shift, capture, unregister and restore |
| MissingHost (S23.3, exit 4) | Follow bounded broken-install steps below | Guardian's missing/start-failed host evidence and Explorer ownership without intervention; app still registered after handoff; no budget-spent claim. Restore file before another sign-in |
| RestartBudget (S23.3, exit 3 after dismissal) | Follow controlled repeated-failure steps below | More host faults than configured allowance within window, corresponding guardian/crash records, budget-spent log; Explorer owns shell/tray **before** dismissing dialog or manual recovery; desktop usable. Restore and verify next sign-in |
| ColdStart (S23.4) | From a restored row, Register; manually restart Windows and sign in | Boot time changes, new token logon ID; same replacement ownership/no-Explorer/usability checks as Registration. No takeover or manual host launch used |

To configure the command without hooks, add `"host.shell.restoreExplorer": "Ctrl+Alt+F12"` to the existing `hotkeys`
object in the test user's `settings.json` **while the app is stopped**, preserving
all other fields; restart via a genuine sign-in. A hotkey supplies no arguments.
The optional `unregister:true` argument is not exercised by a hotkey;
record it separately if tested through an existing command surface that supplies
arguments. Back up any manually edited settings and restore them after the row. The harness records their hash; it does not edit or restore settings.

### Deliberately broken host: VM only

Take and name a fresh VM snapshot and copy current evidence out. Do not change
the guardian, registration mechanism, environment variables or policy. Verify
all host/guardian processes are stopped before changing files. The harness never
renames files or kills the host for you.

**MissingHost:** find which candidate `FindHost` will use: `host\SubZeroDev.WindowManager.Host.exe`
first, then the adjacent `SubZeroDev.WindowManager.Host.exe`. Record and rename
**every existing candidate** to `.drill-disabled` (refuse if that backup already
exists), retaining bytes/hash; leave guardian intact. Capture, Register, manually
sign out/in once. Observe for at most 60 s. Missing executable should trigger
immediate fallback (exit 4), not a restart-budget loop. Capture before any manual
Explorer start. Rename all files back to their exact names, compare hashes with
baseline, unregister, restore and check a new Explorer sign-in. If fallback never
appears, use the recovery procedure or snapshot. Never leave files renamed.

**RestartBudget:** use an intact installation and genuine replacement sign-in.
Capture the effective `restartPolicy` (absent/invalid means default: three
restarts in 300 s; source is guardian `LoadPolicy`/`Budget`). Keep it unchanged.
If the configured maximum is above 3 or window below 60 s, mark this run Blocked
and start a clean default test account. At the console, for each of at most
`maxRestarts + 1` failures (four for default), capture the live host PID/path,
owner SID, creation time and guardian parent PID; manually end that host only
in Task Manager. Leave guardian running and wait for its new host before the
next fault. Never use a name-wide `Stop-Process` or an unbounded kill loop.
Finish within the configured window and within 120 s total. Stop at the first
fallback or unexpected condition. Capture each restarted host and final fallback
**before** dismissing the error dialog. If you did not exceed the allowance in
the window, record Blocked; if the allowance was exceeded but no fallback is
observed, record Fail and recover. Forced termination tests repeated host failure;
it does not prove every possible corrupt-install startup failure. MissingHost is
the separate broken-install launch test. Both remain pending until exercised.

## Determine the registration mechanism from evidence

Use Registration and ShellOff on the exact tested build/UBR, not registry writes
alone. Explorer at the new sign-in, no guardian launch and an unchanged correctly
registered value with no conflicting policy/Run entries is evidence the value
may have been ignored; it is not proof until startup logs and candidate validity
exclude crash/fallback or another autostart. A guardian start followed by host
failure is an application startup/recovery result, not an ignored Shell value.
A manually launched guardian/takeover never establishes that Windows honored it.

Record **honored**, **observed ineffective**, or **inconclusive** with build,
logon IDs, paths, hashes, overrides and before/after/restored evidence. On failure
stop further replacement scenarios, restore and keep them Pending/Blocked. Only
a verified ineffective real sign-in justifies considering the documented per-user
Custom User Interface alternative in a separate decision/change. This harness
never writes policy; tests must not assume either mechanism works on build 26200.

Copy the whole evidence folder out (or zip it to a destination outside itself).
Report case results and evidence links against issue #28 and the tables in
replacement-mode.md. Keep all unexercised rows pending; do not close #28 based on
harness validation. File a reproducible observed failure separately, and record
any changed registration decision in design/90-decisions.md. Existing S29 evidence
and diagnostics are outside this drill.

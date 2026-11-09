# Issue #28 sign-in drill: verification and acceptance record

Prepared 2026-11-05 against main `83657d6` (PR #47 included).
[Issue #28](https://github.com/The-Running-Dev/SubZeroDev.WindowManager/issues/28)
remains open. Use the [operator procedure](../../tools/SignInDrill/README.md)
and [PowerShell harness](../../tools/SignInDrill/Invoke-SignInDrill.ps1).
This is independent of PR #45/S29 transport diagnostics.

## Implementation inspection

- `ShellRegistration` saves `shell.json` before writing HKCU Winlogon Shell;
  repeated On retains the first previous value. Off restores the value or its
  absence. Policy/Launcher overrides return 5; no alternative policy is written.
- Guardian checks physical Shift before starting a host. Explorer handoff
  temporarily names Explorer in Winlogon, waits up to 10 s for a shell window,
  then restores the app value. Shift and fallback do not permanently unregister.
- A missing/unlaunchable host immediately falls back (exit 4). Repeated faults
  exhaust a separate sliding restart budget (default three restarts in 300 s),
  recover the journal, and start Explorer before showing the failure dialog
  (exit 3 after dismissal). These need separate observations.
- Normal host exit in replacement mode returns to Explorer. The restore command
  writes a marker, then exits normally; `unregister:true` additionally calls Off.
  The Settings switch runs the registration command and requires another sign-in.
- Recovery checks journal process identity, reverts desktop entries newest first,
  and records failures/crash evidence. Registration rollback alone does not
  establish desktop/work-area or hidden-window restoration.

These are source observations, not observed Windows acceptance results.
No application behavior or registration mechanism was changed for this drill.

## Non-destructive validation

Run `tools/tests/Test-SignInDrill.ps1` with no SDK/Pester. On Linux PowerShell
7.4.13, 44 assertions passed, including rejection cases: competing registration,
wrong user/computer baseline, existing host, fabricated/missing evidence,
failed registry read-back, and nonzero/timed-out command results. Real JSON/CSV
and bounded file capture exercised original `shell.json` byte restoration,
absent-value/file rollback through mocked registry adapters, empty logs, 1 MiB
truncation, quoted failure notes, and reported probe errors. All process launches
and registry writes were mocked. PowerShell syntax parsing passed.

CI adds Linux PowerShell, Windows PowerShell 7 and Windows PowerShell 5.1 runs.
The Windows runs also read native shell/tray owners, token logon ID, CIM process
state, build and registration. These probes change no desktop or registration.
CI status is reported on the PR; it is not a real sign-in acceptance run.

Not run locally: Windows 5.1 execution, actual Windows registry/Win32/CIM probes,
real registration, sign-out/in, cold restart, Settings switch, physical Shift,
real host failure/restart-budget exhaustion, or a week of daily use. This editing
environment is Linux; no interactive Windows VM/account was available. Existing
application .NET tests were not rerun for this script/documentation-only change.

## Actual acceptance results

| Case | Criterion | Result | Evidence / tested build |
| --- | --- | --- | --- |
| Initial registration and Windows honoring HKCU Shell | S17.7 | **Pending** | No real sign-in; build 26200 applicability unverified |
| CLI `--shell off` and new Explorer sign-in | S17.7 / S23.3 | **Pending** | Not exercised |
| Settings switch and new Explorer sign-in | S23.3 | **Pending** | Not exercised |
| `host.shell.restoreExplorer` session handoff | S23.3 | **Pending** | Not exercised |
| Physical Shift at sign-in | S23.3 | **Pending** | Not exercised |
| Missing/unlaunchable host fallback | S23.3 | **Pending** | Not exercised |
| Repeated host failures exhaust restart budget | S23.3 | **Pending** | Not exercised |
| Restart and replacement sign-in | S23.4 | **Pending** | Not exercised |
| Restored baseline and usable Explorer after each case | S17.7 / S23.3 | **Pending** | Only mocked rollback tested |

Registration mechanism verdict: **inconclusive — not exercised on Windows**.
Keep the existing per-user Winlogon mechanism. Only evidence from a genuine
sign-in can justify considering the Custom User Interface alternative in a
separate decision/change. Registry write success or a session takeover is not
sufficient. Append actual build/UBR, candidate commit/artifact, logon/boot IDs,
observations and before/after/restored evidence links here and in the existing
replacement-mode tables when the operator returns evidence. Preserve failures
and leave everything not exercised pending; no issue #28 checkbox is completed
by the harness tests.

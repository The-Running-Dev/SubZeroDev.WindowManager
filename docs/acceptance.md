# Finishing replacement-shell acceptance

The implementation slices S1–S31 are marked done. That records implementation progress, not completion of the native acceptance gates. Do not call the replacement shell accepted or publish native taskbar-badge support while the required evidence below is missing.

This ledger reconciles the S31 handoff with main at `4951f01` (including PR #45's taskbar transport diagnostic). Automated tests use isolated data, marked keys, synthetic windows and fake devices. They cannot establish behavior at a real Explorer-free sign-in.

A script can run most VM checks and collect the evidence: see the [scripted acceptance run](acceptance/VM-SCRIPTED.md), which also lists what stays manual. The [VM start guide](acceptance/VM-START-HERE.md), [inspection and verification report](acceptance/INSPECTION.md), and [local build provenance](acceptance/acceptance-build.json) are retained with this ledger. The historical candidate patch is beside them; downloadable CI builds are Actions artifacts rather than Git source files.

## Remaining gates

| Area | Evidence still needed | Where to run | Completion rule |
| --- | --- | --- | --- |
| Shell registration and recovery, issue #28 | Cold sign-in without Explorer, startup once per session, usable desktop; normal restoration, Shift escape, failed-host fallback; week of daily use | Disposable account/VM first; daily-use evidence on the intended machine | Fill the tables in [replacement-mode.md](replacement-mode.md) with build, date and observations; keep #28 open until its own criteria are met |
| S24.1 / S24.9 notifications | History with Explorer absent, Windows' own popup behavior, identity retained under job launch, real AUMID activation; certificate trust/access/switch setup | VM with a real notification-producing app | Record the native result; a failed validation gate reopens the design. Use test notifications; keep notification text out of logs |
| S26.8 thumbnails | Real live source into a WinUI popup; placement, clipping and scaling on screen | VM with working DWM, then real DPI/monitor setup | Observe live changes and correct bounds. Synthetic-card fallback is insufficient |
| S26.9 other desktops | Shell-role access to cloak other applications remains unproven | Disposable session | Keep hide/show and cards for other desktops; live thumbnails of other desktops remain excluded unless this gate passes |
| S27.4 devices | Unpackaged Wi-Fi/Bluetooth control, DDC/CI on the actual monitors, built-in panel brightness | Physical hardware | Read back actual state and restore the original state; absent VM devices do not qualify this row |
| S28 pointer selection | Repeated real mouse selection after Win+X, successful launch and focus handoff | VM, then intended desktop | Pointer regression coverage must pass; repeat native clicks. Keyboard Enter does not qualify pointer acceptance |
| S29.2 hover | Enter, delayed open, update without taking focus, transfer into popup, leave/close | VM and intended desktop | Check the native pointer path and real thumbnails; automated synthetic coverage is supplementary |
| S29.3 native progress/overlays | Explorer control, native API calls, readiness for existing and late-created windows, receiver/payload evidence and visible decoded state | Disposable VM | Follow [taskbar-backchannel-experiment.md](taskbar-backchannel-experiment.md). PR #45 is a diagnostic, not a decoder. `TaskbarDecoder.Unverified` currently returns null; native badges do not ship |
| S29.4 jump lists | Real implicit/explicit AppIDs, readable entries and launches without Explorer | VM with known sample apps | Record the supported subset and failures. Pin/unpin and a complete Windows jump-list implementation are outside the agreed scope |
| S30 desktop actions | Right click icon/empty space, correct hit target, rename focus, Recycle Bin deletion and restoration | VM with disposable files | Test each actual input path; verify the deleted file can be restored from the Recycle Bin |
| S31 Alt+Tab | Real unmarked chords, Alt-release menu mask, overlay focus and return, live thumbnails, fallback when switcher unavailable | VM with real app windows | Repeat Tab/Shift+Tab, commit and cancel; inspect the foreground app's menu bar. Marked-key/synthetic tests cannot qualify this |
| Monitor/DPI/full screen | Real second display, scale change, full-screen app and shell restart | Physical setup | Complete the manual rows in [test-matrix.md](test-matrix.md) |
| S24.8 idle CPU stability | The fake-history worker exceeded the unchanged 2% limit in the full run and one isolated run, then passed a further isolated run | Idle reference desktop; measure separately from builds and startup | Preserve every attempt and investigate startup work versus sustained idle cost. Do not extend the budget retry to this test or infer a pass from a rerun |

## VM sequence

1. Create Windows 11 x64, preferably the same build as the target machine. Keep Explorer configured as the shell. Use an ordinary test user for the application. Keep the VM console and snapshot restoration accessible.
2. Snapshot the clean installed guest. Record Windows build, display resolution/scale, VM graphics driver, app commit and artifact hashes. If developing inside the guest, use the SDK requested by `global.json`; a published self-contained app needs no installed .NET.
3. Copy a complete matching release folder, including its `host` and `runtime` folders. Run coexistence first; verify basic panels, input and normal exit. Do not copy the main machine's personal configuration, Desktop files or notification history.
4. Before permanent registration, run PR #45's [session-only Explorer/replacement/restored-Explorer comparison](taskbar-backchannel-experiment.md#comparison-and-restoration). It deliberately leaves sign-in registration alone. This gives us the native taskbar evidence early.
5. Snapshot again. Follow [replacement-mode.md](replacement-mode.md) to register with `--shell on`, sign out/in and qualify the cold-sign-in/recovery rows. Save `--shell status` before and after. Exercise `--shell off` and Shift escape separately; confirm Explorer returns. A successful session-only takeover does not establish cold-sign-in acceptance.
6. Test the S24/S26/S28–S31 rows above using disposable app windows/files. Stop on a failed gate, preserve logs without private notification content, and record the exact reproduction before changing the implementation.
7. Restore Explorer and verify registration, wallpaper and work area. Revert the VM snapshot when repeating destructive recovery scenarios. Hardware rows and the week of daily use remain separate.

VM performance is useful for detecting hangs, but it must not replace the reference/CI budgets or be used to raise them. A VM with no radio, DDC/CI display, built-in panel or accelerated DWM cannot qualify those device/rendering paths.

## Explicit pointer probes

After building the matching source in an idle interactive VM, run the two explicit tests from the repository root:

```powershell
$pointerTests = 'tests/SubZeroDev.WindowManager.EndToEnd.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/SubZeroDev.WindowManager.EndToEnd.Tests.exe'
& $pointerTests -explicit only -xml artifacts/native-pointer.xml
```

These exercise ten consecutive Win+X selections and a hover preview's open/update/close path with synthetic application windows. The development desktop produced both passing and failing runs; the pointer gates remain open. The normal suite leaves these explicit tests unrun. Keep the VM idle during the probes, preserve failed results, and then test real user input and real app windows separately. Running the tests with the native hook is not a real Explorer-free sign-in.

## Evidence format

For each row record: commit/artifact hash; OS build and display configuration; exact inputs; expected and observed behavior; relevant log timestamps; pass/fail/inconclusive; restoration result. Preserve failed attempts too. A missing device or unavailable observer is inconclusive, not a pass. Link the record from the owning issue/PR when that publication is requested.

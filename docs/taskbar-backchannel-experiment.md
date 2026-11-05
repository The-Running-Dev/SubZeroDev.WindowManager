# S29 native taskbar experiment

Status: **READY_FOR_EXECUTION**. No native desktop results have been collected for this change. Real application progress and overlays remain unsupported/unverified; `TaskbarDecoder.Unverified` still returns `null`. Injected tests are model/presentation evidence only. Hover previews are a separate pending desktop check and this experiment does not qualify them.

## What the source establishes

Inspected baseline: `83493a78b9ac0c4e8333397e060af266ecfd0115`, including merged [S29 PR #42](https://github.com/The-Running-Dev/SubZeroDev.WindowManager/pull/42).

- Replacement mode starts `ShellRoleService` and its distinct desktop/shell-hook windows. `TrayHost` separately creates a top-level `Shell_TrayWnd` and a `TrayNotifyWnd` child on its tray thread. It refuses a competing owner's `Shell_TrayWnd` (`TrayTaken`). Coexistence mode does not start this tray host.
- The tray handles `WM_COPYDATA` kind 1 as notification icons and kind 0 as app bars. Unknown kinds go to `Unhandled`, with **kind substituted for the original lParam and no payload**. Registered messages (`>= 0xC000`) also go there; `WM_USER`/`WM_APP` messages do not. The distinct shell-hook window handles `SHELLHOOK` events, not the taskbar decoder.
- `TaskbarBackchannel.Start` attaches after tray creation. Its default decoder produces no updates; the ordinary log records at most 64 different message numbers, once each. This log cannot reveal changing arguments or prove their sender. `Inject` bypasses transport and decoding completely.
- `TaskbarButtonCreated` is broadcast once after layout and before startup programs run. There is no per-new-window readiness notification in this implementation. **Inference:** a conforming client launched later may wait forever, independently of whether its native API calls could reach the tray.

Microsoft documents [per-window readiness before ITaskbarList3 use](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nn-shobjidl_core-itaskbarlist3), the [progress lifecycle](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist3-setprogressvalue) and [overlay ownership/lifetime](https://learn.microsoft.com/en-us/windows/win32/api/shobjidl_core/nf-shobjidl_core-itaskbarlist3-setoverlayicon). These document API behavior, **not an incoming shell message format**. They do not establish that implementing a window of the expected class is sufficient. This experiment does not replace/register the system COM class.

## What is added

`tools/TaskbarProbe/probe.cpp` is a separate x64 Win32 application using the Windows SDK's actual `ITaskbarList3` definition. It registers `TaskbarButtonCreated` before showing its own unowned `WS_EX_APPWINDOW` window; calls `CoCreateInstance` and `HrInit`; and logs readiness, window handles, tray owner PID/thread, UTC, QPC and each API HRESULT. A conforming sequence starts after readiness. **F5** repeats it. **F6** explicitly runs without readiness if none arrived; those native calls are labeled `FORCED-WITHOUT-READINESS`, not conforming client evidence. No readiness or badge messages are fabricated. It closes after 120 seconds, or on Esc, and clears its own progress/overlay on normal close. Shared system icons are not destroyed. It changes no shell registration or policy.

One call every two seconds: clear progress; indeterminate; 17/100; 61/100; paused; error; warning overlay; information overlay; clear overlay; clear progress. Capture 10 seconds of idle before and after a repeat. HRESULT success alone does not establish visible state or message delivery.

Set `SUBZERODEV_WM_TASKBAR_TRACE` to an **absolute new file path** before starting the guardian to opt into host capture. The host records incoming messages before routing on its tray **and notification child**, including `WM_COPYDATA`, `WM_USER`, `WM_APP`, and registered messages. A window record identifies each captured receiver. It preserves repeated messages, raw scalar parameters, copy-data kind/declared size and at most 256 payload bytes copied during the call. No other lParam is dereferenced and no foreign-process pointers or handles are followed. Payloads are uninterpreted hex. The existing router, replies, filters and default decoder stay as they were. Injection never calls this observer.

The trace holds at most 2,048 message records, queues file IO off the tray thread, and reports eligible/captured/dropped counts at orderly disposal. It refuses overwriting an existing file; failure logs `TaskbarTraceFailed` and leaves the shell running. A missing `end` record, `dropped > 0`, or any trace failure makes a negative result inconclusive. Shutdown waits at most two seconds for file IO. The bounded file can include unrelated applications' payloads; run in a quiet disposable desktop and inspect before sharing.

`source=wndproc-observation` means the window procedure was observed; it does not authenticate a sender. `hookScoped=true` identifies a test-isolated tray. Unit/integration messages deliberately sent by tests are **simulated stimuli**, even when user32 delivered them. Neither they nor timing correlation alone establish real application transport.

## Prepare and build

Use a disposable Windows 11 VM/snapshot first, same user/session/integrity for shell and probe, with Visual Studio C++ x64 tools, Windows SDK, and the repository's .NET SDK. Preserve unsaved work. Record the Windows build and the exact tested Git commit/host binary; keep the same probe binary through the comparison. Do not run as administrator to hide a possible integrity mismatch. Do not change UIPI filters for the experiment.

From the repository root in an **x64 Native Tools Command Prompt**:

```bat
dotnet build SubZeroDev.WindowManager.slnx -c Release
tools\TaskbarProbe\build.cmd
dotnet test tests\SubZeroDev.WindowManager.Host.Core.Tests -c Release --no-build --filter FullyQualifiedName~TaskbarTrace
```

CI compiles the native probe with `/W4 /WX` and runs its usage-only entry point (expected exit 2). CI instrumentation tests prove capture boundaries, not live taskbar integration. Desktop sequences remain manual. A hung COM call can prevent the probe's GUI timer from firing; close this exact probe process via Task Manager if necessary, preserving the incomplete log.

Use **64-bit Spy++** for the Explorer baseline because this code cannot observe Explorer's window procedure from its own process. [Messages view](https://learn.microsoft.com/en-us/visualstudio/debugger/how-to-use-messages-view) supports a window, thread, or process. Refresh the tree after new windows appear. Select the recorded `Shell_TrayWnd` and children, then the owner's thread/process if necessary; enable logging to file and all message types, including unknown/private/registered messages and sent/posted traffic. Capture only the short controlled run. If this observer is unavailable, the visual/client baseline is useful but the Explorer transport comparison is **not completed**.

## Comparison and restoration

The test uses **session-only takeover**. Do not use `--shell on`, change policy, or stop Explorer with `Stop-Process`. Begin with Explorer as the configured sign-in shell, no competing WindowManager guardian, and a reachable Task Manager (Ctrl+Shift+Esc). Check `--shell status` and save its JSON before starting. If another shell is configured, use a disposable account/VM instead of changing that configuration.

In PowerShell at the root, stage a complete app using the existing release builder in a new output directory. This places the guardian at the root and its matching host/content under `host`. Use that same build for the entire run:

```powershell
$experimentBuild = Join-Path $PWD ('artifacts\taskbar-shell-' + [guid]::NewGuid().ToString('N'))
./build/release.ps1 -Output $experimentBuild
$guardian = (Resolve-Path (Join-Path $experimentBuild 'release\SubZeroDev.WindowManager.exe')).Path
& $guardian --shell status
Get-FileHash $guardian
Get-FileHash (Join-Path (Split-Path $guardian) 'host\SubZeroDev.WindowManager.Host.exe')
```

1. **Explorer A (positive control):** start Spy++ logging on Explorer's actual tray and descendants/owning thread before launching the client. In another PowerShell: `./tools/TaskbarProbe/run.ps1 explorer-a`. Verify the logged tray PID belongs to Explorer. Observe readiness and the full sequence, repeat with F5, and save screenshots of 17%, 61%, paused, error, both overlays, and clears. Save the Spy++ log and selection/filter settings. If the visible control fails, stop and resolve that environment/client issue before interpreting replacement results.

2. **Already-existing client across takeover:** start a fresh `./tools/TaskbarProbe/run.ps1 replacement-existing` while Explorer runs. After its baseline sequence, keep its window open. In the prepared controller PowerShell, choose a new trace path and start the guardian:

   ```powershell
   if ($env:SUBZERODEV_WM_TEST_HOOK) { throw 'Unset test hook before the real experiment.' }
   $env:SUBZERODEV_WM_TASKBAR_TRACE = Join-Path $PWD ("artifacts\taskbar-probe\host-{0}.jsonl" -f [guid]::NewGuid().ToString('N'))
   $wm = Start-Process $guardian -ArgumentList '--mode','replacement','--take-over' -PassThru
   Remove-Item Env:SUBZERODEV_WM_TASKBAR_TRACE
   ```

   The guardian inherits the opt-in variable; removing it only affects later processes started by this controller. Confirm replacement ownership in a new probe snapshot (F5 logs it), both real shell roles held, and no `TrayTaken`/`ShellRoleTaken` log. Use Spy++ on the replacement tray too if available. Run F5 after takeover and keep within the client's 120-second lifetime; repeat the phase if it timed out. Record any new readiness receipt rather than assuming the old Explorer notification applies to the new shell.

3. **Late client, conforming path:** close the existing probe; launch `./tools/TaskbarProbe/run.ps1 replacement-late`. Wait at least 10 seconds. Does it receive `TaskbarButtonCreated`? If yes, repeat/observe as above. If not, save that result and press Esc: **no calls were made**. This diagnoses readiness, not missing progress transport.

4. **Late client, exploratory calls:** launch `./tools/TaskbarProbe/run.ps1 replacement-forced`, wait 10 seconds, then F6 if readiness never arrives. Preserve the forced label. Repeat twice with idle intervals using F6. Correlate each API call's UTC/QPC with host samples. Successful delivery here can motivate a separate per-window readiness change; it does not qualify the conforming late-client scenario. Never feed these observations to `Inject` or claim a decoded badge.

5. **Restore Explorer:** use the host's normal **Exit** action (guardian restores Explorer on a normal replacement exit), or invoke its existing `host.shell.restoreExplorer` command from a configured launcher/hotkey. Do not kill the guardian as the ordinary exit path. Confirm Explorer owns both the shell and `Shell_TrayWnd`, the host/guardian exit, and the trace ends with counts. Remove the environment variable if still set. Run `./tools/TaskbarProbe/run.ps1 explorer-restored`, F5 and the same screenshots/Spy++ capture to check the positive control again. Compare `--shell status` with the saved value; registration should be unchanged.

6. **If the UI is unresponsive:** Ctrl+Alt+Delete → Sign out, then sign back in. Because this procedure never changes the sign-in registration, the next session starts the original Explorer shell. Task Manager → Run new task → `explorer.exe` is an immediate session fallback if no replacement shell remains. If Explorer only opens a folder, use sign-out rather than registry edits. After sign-in, use `& $guardian --recover-only` for stale journal recovery if needed, then verify work area/wallpaper and registration. A VM snapshot is the final restoration path. If already testing an independently registered replacement shell, follow [replacement-mode.md](replacement-mode.md)'s `--shell off`/Shift escape procedure instead; that is outside this session-only comparison.

## Evidence and decision rules

Record one row per run/sequence. Keep the raw logs, metadata, screenshots, selected HWNDs/PIDs, Windows build, binary hashes, readiness times, HRESULTs, dropped count and observation errors together. Registered message **numbers are session-local**: compare their timing/arguments within a session, not an assumed fixed ID across boots. `wParam` is only a sender HWND where the documented message contract says so (e.g. `WM_COPYDATA`); never attribute arbitrary registered messages to the probe from it. QPC frequency appears in both logs and UTC aligns independent observers.

| Condition | What it supports | What follows |
| --- | --- | --- |
| Visible Explorer sequence + repeatable tray/child traffic tied to distinct calls/values, same traffic reaches host under replacement | Candidate viability of tray transport on the tested build | Capture full required data/lifetimes, identify receiver and return semantics; separately implement/test a decoder using real fixtures and end-to-end visible state. This probe itself implements none. |
| Explorer traffic uses `WM_USER`/`WM_APP`, or payloads lost by `Unhandled` | Current production routing/decoder boundary is inadequate | Preserve observations; change the specific route/envelope in a later patch, backed by captures. Trace visibility is not production delivery. |
| Existing client works but late client never receives readiness; forced native calls reach host | Lifecycle gap independent of transport | Add per-window readiness on admitted button creation, then rerun conforming late-client and restart cases. No automatic broadcast workaround in this patch. |
| Explorer uses another child/window/thread, shared object or COM service; host tray gets no matching calls after readiness | Expected tray-only architecture lacks evidence or is insufficient | Observe the actual endpoint with Spy++/debugger; determine whether a supported integration exists. A different shell integration is a design change; do not hijack the COM class. |
| Explorer visual control works but neither observer sees traffic, or capture is partial/dropped | Observer coverage insufficient | Broaden observation to the tray owner's process/all its windows and inspect client-side COM call stacks using a native debugger. Do not infer IPC/RPC solely from silence at one HWND. |
| API fails, no readiness, mismatched owner/session/integrity, cap hit, or Explorer control fails | Experiment is inconclusive for transport | Resolve the named condition and rerun. Never equate silence with architectural impossibility. |
| No verified route and complete semantics/lifetimes | Native badge support remains unsupported | Keep the decoder empty and do not ship a native-progress claim. |

Even correlated messages with a success HRESULT do not establish enough data to implement badges. Require discriminating 17/100 vs 61/100, all progress states and clear; warning vs information overlay pixels/accessibility text and clear; window identity; and ownership/lifetime after return. A truncated buffer or scalar that is a foreign pointer/handle does not meet that bar. If those require another endpoint/integration, document it before decoding anything.

## Results still required

| Experiment | Native observation in this change |
| --- | --- |
| Explorer positive control + x64 Spy++ route capture | Not run |
| Existing client across session-only takeover | Not run |
| Late conforming client readiness and calls | Not run |
| Separate forced native calls when readiness is absent | Not run |
| Repeated values/states/overlay/clear and required payload lifetimes | Not run |
| Normal exit, restored Explorer control, fallback sign-in and cleanup | Not run |

Development host: Linux, no Windows desktop and no local .NET SDK (`dotnet --info`: `/bin/bash: line 1: dotnet: command not found`). Automated results belong to the PR checks; none of these native experiments may be inferred from them. **Current assessment: the tray-window approach is an unverified hypothesis, not disproved; native badges stay unsupported pending the above evidence.**

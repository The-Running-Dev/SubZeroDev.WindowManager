# S24.8 idle CPU investigation

Status: **the fake-provider correction is implemented and code head `8b6ddca` passes Windows CI; reference-desktop and production-provider acceptance remain open**. The <2% of one core limit is unchanged. Measurements target ten seconds and retain their actual elapsed duration; they do not enforce a strict ten-second minimum. A subsequent pass does not erase a failure.

## What the code establishes

- Before this change, `FakeHistory` polled file metadata every 100 ms. It reparsed the JSON and raised a refresh only after the file stamp changed, or a command changed the history. Polling an unchanged file did not itself publish feeds. The correction replaces that timer with `FileSystemWatcher`, following atomic replacements, creation, deletion and writes. Duplicate stamps are suppressed under the existing lock; overflow invalidates the snapshot and requests a read.
- `NotificationsPlugin` coalesces refresh requests and serializes processing. The startup refresh seeds the known history without raising a toast. A failed read schedules a five-second retry.
- `RealHistory` uses `UserNotificationListener.NotificationChanged` plus a **60-second resync**, including configurations where the native event is unavailable. It does not use the fake's file polling. A ten-second quiet window alone cannot qualify the resync cost.
- The old S24.8 sample covered one worker process after readiness and two seconds of settling. Its CPU numerator already excluded the host/test-runner processes, but included worker runtime/JIT/GC, SDK traffic and fake-provider activity. Host status polling can still affect scheduling and worker traffic indirectly.
- The historical full-suite failure and isolated **3.12%** failure remain failures. No trace from those invocations exists here to attribute their CPU cost. Neither source inspection nor a portable pass determines their cause.

## Evidence added

Set `SUBZERODEV_WM_NOTIFICATION_DIAGNOSTICS=1` with the existing test hook to collect a bounded in-memory activity trace. The worker writes `notification-diagnostics.json` only when it stops. It records monotonic start/end stamps for `worker.start`, `worker.refresh`, `fake.read` and `fake.observe` (the baseline trace used `fake.poll`), with OS/runtime and dropped-event count. It contains no notification text, identifiers or paths. Timings are **elapsed durations, not CPU samples**; async refresh time can include waiting, and nested read/refresh durations must not be added together as CPU attribution. Runtime/JIT/GC and SDK activity remain unattributed by this trace.

S24.8 now launches three fresh workers. Each keeps the original two-second settling period, then measures **three consecutive windows targeting ten seconds each**. Every window must remain below 2%; no median, successful retry or later quiet window replaces a failure. One-second process CPU slices locate bursts within each window. Startup/settling CPU is reported separately. The trace and all nine windows share monotonic timestamps, so activity can be correlated without writing trace files during measurement. JSON is saved after each window and on failure; CI uploads it with `if: always()`.

### Timing precision

The Windows guard schedules one-second sample targets through ten seconds; the portable probe uses `Task.Delay(TimeSpan.FromSeconds(10))`. Timer granularity means neither guarantees an elapsed duration of at least 10,000 ms. The current isolated Windows run includes a 9,999.2854 ms window; the full-suite windows range from 10,000.2573 to 10,010.2126 ms. Every CPU percentage uses the recorded actual elapsed duration, without division by logical processor count. These results satisfy the existing nominal ten-second guard; they do not establish a literal minimum-ten-second requirement. Such a requirement would need a separate timing correction and validation, not a reinterpretation of these samples.

For a Release build on an interactive Windows desktop:

```powershell
$env:SUBZERODEV_WM_NOTIFICATION_OUT = "$PWD/artifacts/notification-idle-windows"
& tests/SubZeroDev.WindowManager.EndToEnd.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/SubZeroDev.WindowManager.EndToEnd.Tests.exe -method SubZeroDev.WindowManager.EndToEnd.Tests.NotificationsTests.S24_8_an_idle_worker_costs_next_to_no_processor_time -xml "$env:SUBZERODEV_WM_NOTIFICATION_OUT/results.xml"
```

Create the output directory first when invoking the executable directly with `-xml`. CI runs this isolated check before the full suite and retains its result separately under `isolated/`; the suite writes under `full-suite/`. It uploads `notification-idle-isolated-windows` immediately and `notification-idle-windows` afterward. The isolated step may continue solely to collect full-suite evidence; an unconditional outcome check fails the job if that isolated step failed, even if the suite later passes. It still uses the simulated history; native Windows execution does not turn it into production-provider evidence.

## Portable controlled comparison

`tools/NotificationIdleProbe` compiles the actual fake/provider-independent notification processing and SDK/protocol sources. Only `RealHistory` is replaced with a stub that throws if selected. The host transport writes to `Stream.Null`; there is no host IPC reader, heartbeat, native bell or UI. The framework reference supplies logging abstractions without adding production dependencies. This is a **portable simulation**, not the deployed Windows worker.

```sh
dotnet build tools/NotificationIdleProbe -c Release
dotnet run --project tools/NotificationIdleProbe -c Release --no-build -- --self-test
dotnet run --project tools/NotificationIdleProbe -c Release --no-build -- --measure artifacts/notification-idle-portable
```

The entry point runs three fresh processes for each of five modes in rotating order: waiting control, fake only, traced fake, worker with fake, traced worker with fake. Each process settles for two seconds and retains three CPU windows targeting ten seconds each, startup CPU and any trace. All cases run even when a budget window fails; the command returns nonzero if any exceeds the same strict budget. This compares tracing overhead as well as fake/processing activity. Do not run builds or other probes concurrently with the comparison.

The self-test verifies that unchanged history performs no observation/read/refresh work, atomic replacement triggers processing, deletion and recreation are followed, and diagnostics are bounded and content-free. Restoring the old polling implementation makes it fail with `The fake still wakes on an idle polling timer.`; restoring the correction passes. It does not qualify CPU performance.

## Repeated portable results (8 November 2026)

.NET SDK 10.0.300, .NET runtime 10.0.8, Ubuntu 24.04.3 LTS, eight reported logical processors. Percentages use one core without division by processor count. Each mode has three fresh processes and three nominal ten-second windows per process, with the same two-second settling period. Modes rotate between repetitions. Shared-host load is not controlled.

The baseline is the instrumented polling implementation at `94dbdec`, not the historical Windows binary. The corrected portable processing source is `5b511a7`; later registry, workflow and documentation changes do not alter the portable probe sources. The baseline command returned 1, preserving every case; the corrected command returned 0. No successful rerun overwrote a failure.

| Mode | Baseline range (% of one core) | Failures / 9 | Corrected range (% of one core) | Failures / 9 |
| --- | ---: | ---: | ---: | ---: |
| Waiting control | 0.018–0.050 | 0 | 0.014–0.048 | 0 |
| Fake only | 0.471–2.700 | 1 | 0.017–0.054 | 0 |
| Fake only, traced | 0.441–2.418 | 2 | 0.014–0.055 | 0 |
| Worker + fake | 0.377–1.703 | 0 | 0.015–0.043 | 0 |
| Worker + fake, traced | 0.429–4.666 | 1 | 0.017–0.049 | 0 |

The four failing baseline windows are: fake-only run 1/window 1 **2.700373%**; traced fake run 2/windows 1 and 2 **2.418422%**, **2.241951%**; traced worker run 3/window 1 **4.665545%**. All traced idle windows have zero history reads or refreshes. The last failing traced worker window contains 100 polling callbacks totalling 175.056 ms of elapsed body time, versus approximately 466.6 ms of process CPU across ten seconds. These are different quantities; they do not identify a specific JIT, GC or scheduler cause.

With file-change observation, all traced idle windows have **zero provider observation, history read and worker refresh events**. The before/after comparison implicates unnecessary periodic fake-provider/runtime activity in this simulation. It does not prove that the historical 3.12% Windows failure had the same cause.

Every startup/CPU window, including failures, is in [measurements.json](notification-idle/measurements.json). Complete reports and activity traces are retained in [baseline.json.gz](notification-idle/baseline.json.gz) and [corrected.json.gz](notification-idle/corrected.json.gz), with source commits and archive SHA-256 values in the summary. [validation.txt](notification-idle/validation.txt) retains the expected failing regression and the local Windows build error. The self-test fails with the old timer implementation and passes with the correction. Local Windows tests did not run: the native project lacks restored Windows dependencies (`NETSDK1004`); a passing portable build is a separate result.

## Initial Windows isolated result (8 November 2026)

The isolated S24.8 invocation at code/workflow head `fc35d76` on [CI run 42525703609](https://github.com/The-Running-Dev/SubZeroDev.WindowManager/actions/runs/42525703609) passed **one test, zero failed/skipped**, in 101.589 s. It retained all nine nominal ten-second CPU windows over three fresh workers. This is an actual interactive Windows CI desktop running the **simulated provider**, not production-provider evidence or acceptance on the reference physical machine.

| Fresh worker | Window 1 CPU % | Window 2 CPU % | Window 3 CPU % |
| --- | ---: | ---: | ---: |
| 1 | 0.468707 | 0.312489 | 0.624960 |
| 2 | 0.312494 | 0.468766 | 0.312149 |
| 3 | 0.624970 | 0.000000 | 0.156153 |

Runtime: .NET 10.0.14. All three activity traces have zero dropped events and zero events during the measured idle windows. Startup/settling values, one-second CPU slices, OS metadata, trace and original XML are in [windows-isolated.json.gz](notification-idle/windows-isolated.json.gz). The downloaded Actions artifact SHA-256 matched `5b3a145ff44623c79638a663050975e0e17eea4dbd092095a0d083e584b1cb0b`. At the time this artifact was recorded, the full suite and broader budgets were pending. Their completed results and the subsequent CI correction are recorded below; this historical isolated pass does not replace those outcomes.

## Windows CI completion and registry correction (9 November 2026)

[CI run 42609506640](https://github.com/The-Running-Dev/SubZeroDev.WindowManager/actions/runs/42609506640) and [Release run 42609506683](https://github.com/The-Running-Dev/SubZeroDev.WindowManager/actions/runs/42609506683) both passed on code head `8b6ddca64ae7f54d67ffba257f4aa35c777db0f1`.

| Check | Verified result |
| --- | --- |
| Core suite | 441 passed, zero failed, including the new startup-marker regression |
| Desktop suite | 157 passed, zero failed, eight existing skips |
| Isolated S24.8 | One test passed, zero failed/skipped; all nine CPU windows passed; maximum 0.781306% of one core |
| S24.8 within the full suite | Passed; all nine CPU windows passed; maximum 0.937476% of one core |
| Other CI jobs | Performance budgets, portable notification probe and hygiene passed |
| Release workflow | Passed |

The [notification-idle-windows artifact](https://github.com/The-Running-Dev/SubZeroDev.WindowManager/actions/runs/42609506640/artifacts/13670526855) retains separate `isolated/` and `full-suite/` reports: three fresh worker PIDs and nine CPU windows per invocation, ten CPU slices per window, startup/settling values and activity traces. All six traces have zero dropped events and no activity intervals overlapping the measured idle windows. Its ZIP SHA-256 was verified as `c12cea3cbb8d57c03eb7923eb5a9af9e135c9b4bb29b0e6cf973bcfa1eaf149d`. These are Windows CI desktop measurements of the **simulated provider**. The runs qualify the named code head; a later documentation commit is not itself a new runtime measurement.

The preceding [CI run 42526222318](https://github.com/The-Running-Dev/SubZeroDev.WindowManager/actions/runs/42526222318) on `ae6252e` passed S24.8 both isolated and within the suite, and passed the separate budgets job, but still failed nine core tests and nine registry-backed desktop tests. Those failures are retained: `StartupRunner` created the marker's entire missing path as volatile, preventing later creation of persistent subkeys beneath those ancestors. This produced `Cannot create a stable subkey under a volatile parent key.` and subsequent registration failures.

The follow-up production-code change in `8b6ddca` creates persistent ancestors first and makes only the session-marker leaf volatile. Its new Windows regression starts with the entire path missing, verifies a persistent sibling can be created, verifies that the marker still rejects a persistent child with `ERROR_CHILD_MUST_BE_VOLATILE` (1021), and verifies that a second startup pass is skipped. This is an additional CI-enabling fix in the PR, separate from fake-history observation; it does not change `RealHistory`. No test was disabled or retried to hide the registry failures.

## Decision and remaining evidence

The portable baseline reproduces excessive process CPU without native UI and, in a fake-only case, without notification worker processing. It retains four failing windows out of 45 (maximum 4.6655%). All traced idle windows have zero history reads and worker refreshes. The polling body itself accounts for only part of process CPU; runtime and scheduling costs are not individually attributed. The justified correction removes unnecessary periodic fake-provider work. It does not change `RealHistory` or disable normal runtime behavior, and does **not** claim to have explained the historical Windows failures. The corrected comparison passes all 45 windows: 36 provider/worker windows plus nine waiting controls. Provider/worker CPU ranges from 0.014419% to 0.054584% of one core; the highest worker-plus-fake window is 0.048676%.

Repeated simulated-provider Windows checks now pass both isolated and within full-suite execution, with every window retained. Reference-desktop acceptance and production-provider qualification remain open. The historical 3.12% failure is still not attributed to a specific runtime or scheduling cause, and the nominal timing implementation does not prove a strict ten-second minimum. If process CPU spikes while the activity trace is quiet, capture a runtime/CPU profile for JIT/GC, SDK/transport and scheduler attribution; elapsed trace durations alone do not prove the cause. Production qualification additionally needs identity/consent, real notification events and windows spanning the 60-second resync, recorded separately from fake-provider acceptance.

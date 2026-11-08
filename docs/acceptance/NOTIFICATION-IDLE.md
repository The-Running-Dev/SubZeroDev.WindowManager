# S24.8 idle CPU investigation

Status: **unresolved; native acceptance remains open**. The <2% of one core limit and ten-second measurement window are unchanged. A subsequent pass does not erase a failure.

## What the code establishes

- `FakeHistory` polls file metadata every 100 ms. It reparses the JSON and raises a refresh only after the file stamp changes, or a command changes the history. Polling an unchanged file does not itself publish feeds.
- `NotificationsPlugin` coalesces refresh requests and serializes processing. The startup refresh seeds the known history without raising a toast. A failed read schedules a five-second retry.
- `RealHistory` uses `UserNotificationListener.NotificationChanged` plus a **60-second resync**, including configurations where the native event is unavailable. It does not use the fake's file polling. A ten-second quiet window alone cannot qualify the resync cost.
- The old S24.8 sample covered one worker process after readiness and two seconds of settling. Its CPU numerator already excluded the host/test-runner processes, but included worker runtime/JIT/GC, SDK traffic and fake-provider activity. Host status polling can still affect scheduling and worker traffic indirectly.
- The historical full-suite failure and isolated **3.12%** failure remain failures. No trace from those invocations exists here to attribute their CPU cost. Neither source inspection nor a portable pass determines their cause.

## Evidence added

Set `SUBZERODEV_WM_NOTIFICATION_DIAGNOSTICS=1` with the existing test hook to collect a bounded in-memory activity trace. The worker writes `notification-diagnostics.json` only when it stops. It records monotonic start/end stamps for `worker.start`, `worker.refresh`, `fake.read` and `fake.poll`, with OS/runtime and dropped-event count. It contains no notification text, identifiers or paths. Timings are **elapsed durations, not CPU samples**; async refresh time can include waiting, and nested read/refresh durations must not be added together as CPU attribution. Runtime/JIT/GC and SDK activity remain unattributed by this trace.

S24.8 now launches three fresh workers. Each keeps the original two-second settling period, then measures **three consecutive ten-second windows**. Every window must remain below 2%; no median, successful retry or later quiet window replaces a failure. One-second process CPU slices locate bursts within each window. Startup/settling CPU is reported separately. The trace and all nine windows share monotonic timestamps, so activity can be correlated without writing trace files during measurement. JSON is saved after each window and on failure; CI uploads it with `if: always()`.

For a Release build on an interactive Windows desktop:

```powershell
$env:SUBZERODEV_WM_NOTIFICATION_OUT = "$PWD/artifacts/notification-idle-windows"
& tests/SubZeroDev.WindowManager.EndToEnd.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/SubZeroDev.WindowManager.EndToEnd.Tests.exe -method SubZeroDev.WindowManager.EndToEnd.Tests.NotificationsTests.S24_8_an_idle_worker_costs_next_to_no_processor_time -xml "$env:SUBZERODEV_WM_NOTIFICATION_OUT/results.xml"
```

Create the output directory first when invoking the executable directly with `-xml`. The normal CI suite configures the path and uploads `notification-idle-windows`. It still uses the simulated history; native Windows execution does not turn it into production-provider evidence.

## Portable controlled comparison

`tools/NotificationIdleProbe` compiles the actual fake/provider-independent notification processing and SDK/protocol sources. Only `RealHistory` is replaced with a stub that throws if selected. The host transport writes to `Stream.Null`; there is no host IPC reader, heartbeat, native bell or UI. The framework reference supplies logging abstractions without adding production dependencies. This is a **portable simulation**, not the deployed Windows worker.

```sh
dotnet build tools/NotificationIdleProbe -c Release
dotnet run --project tools/NotificationIdleProbe -c Release --no-build -- --self-test
dotnet run --project tools/NotificationIdleProbe -c Release --no-build -- --measure artifacts/notification-idle-portable
```

The entry point runs three fresh processes for each of five modes in rotating order: waiting control, fake only, traced fake, worker with fake, traced worker with fake. Each process settles for two seconds and retains three ten-second CPU windows, startup CPU and any trace. All cases run even when a budget window fails; the command returns nonzero if any exceeds the same strict budget. This compares tracing overhead as well as fake/processing activity. Do not run builds or other probes concurrently with the comparison.

The self-test verifies that idle polling performs no extra reads/refreshes, a file change triggers processing, and diagnostics are bounded and content-free. It does not qualify CPU performance.

## Decision and remaining evidence

No production optimization is justified until the excessive activity is reproduced and attributed. This change corrects the measurement/reporting gap and supplies a repeatable diagnostic path; it does **not** claim to fix the intermittent native CPU failure.

Native S24.8 remains open until repeated Windows desktop runs, both isolated and following the full suite, retain all windows and explain any failures. If process CPU spikes while the activity trace is quiet, capture a runtime/CPU profile for JIT/GC, SDK/transport and scheduler attribution; elapsed trace durations alone do not prove the cause. Production qualification additionally needs identity/consent, real notification events and windows spanning the 60-second resync, recorded separately from fake-provider acceptance.

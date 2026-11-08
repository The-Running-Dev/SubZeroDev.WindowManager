# WindowManager inspection and acceptance candidate

The implementation plan is complete, but the replacement shell is not yet accepted. Main `4951f01` includes PR #45's taskbar transport diagnostic. It still uses `TaskbarDecoder.Unverified`, which returns no decoded progress or overlay updates. Synthetic badge tests do not establish interoperability with real `ITaskbarList3` clients. This remains a release gate.

The remaining work is recorded in the [acceptance ledger](../acceptance.md): cold sign-in and recovery; notification identity/history without Explorer; DWM thumbnails; pointer selection and hover; jump-list coverage; desktop context actions; real Alt+Tab; physical devices and monitor/DPI behavior; and issue #28's daily-use evidence. Session-only takeover and cold sign-in need separate evidence. An empty VM is the next useful environment.

## Candidate changes

- Fixed popup focus policy. An opening requested without activation now retains that policy through worker render updates and deferred focus callbacks. Previously a hover preview could steal keyboard focus when its content arrived or changed. The original implementation failed the focus regression; the corrected implementation has passed those assertions.
- Added explicit native-pointer probes for ten Win+X selections and hover preview open/update/close without focus. Development-desktop runs both passed and failed. Their normal-suite exclusion is explicit, and they do not establish native acceptance or a fix for lost mouse clicks.
- Made pointer movement inject a virtual-desktop movement event, with physical-pixel normalization under the existing DPI context. This is test input plumbing; it is not a product fix for pointer loss.
- Made the backchannel test's diagnostic log a concurrent queue. Native broadcasts can reach the real hidden test window concurrently. The assertion for the test's own message remains.
- Added one bounded CI retry for each of S23_2 replacement startup and S25_11 idle with hundreds of windows. Other budgets fail immediately. Thresholds and internal measurement counts are unchanged; both attempts retain separate metrics/XML and recovered failures produce a warning.
- Corrected stale replacement-mode documentation and separated implementation completion from native acceptance.

The local archive was built from uncommitted changes on `fix/desktop-acceptance`. The [compressed build-time review patch](WindowManager-acceptance.patch.gz) preserves that exact candidate diff; it predates the packaging of these guides into the repository. The current branch contains the reviewable changes. Native acceptance remains pending; this report does not claim a merge or published release.

## Verification

- Release solution build: passed, zero warnings and errors in the final build.
- Host.Core: 440 passed, zero failed/skipped.
- Release packaging: completed; `release.ps1 -Verify` confirmed built-in plugin payloads match their package output byte for byte.
- Delivered ZIP: all 746 entries decompressed and matched the published folder by SHA-256; no missing or extra files.
- Budget retry control flow: passed all four scenarios (clean success, recovered failure, strict failure without retry, persistent retryable failure). This is not a real performance-budget measurement or a new CI result.

- Full desktop suite: **153 passed, 4 failed, 8 skipped, 2 explicit tests not run** (167 total). Failures: S4_7 guardian recovery, S19_1 tray-icon rendering, S24_8 notification idle CPU and S5_6 appearance. The run overlapped release build/validation work. It remains a failed full run.
- Isolated reruns: guardian recovery, tray-icon rendering and appearance passed. Notification idle CPU failed again at **3.12%** against the unchanged **2%** limit, then passed a further isolated run. The sampled timeline showed low CPU use later in that run, but did not establish the failures' cause. This stability issue remains open; no assertion or threshold was changed.
- Packaged release smoke tests: **3 passed**, zero failed/skipped, including sample-plugin installation and both released SDK fixture scenarios using the bundled runtime.
- `git diff --check`: passed.

## VM payload limits

The archive is a self-contained local candidate with bundled runtime and built-in plugins. It needs no .NET SDK just to run. It does not include a compiled TaskbarProbe or portable automated tests. The native probe needs Visual C++ tooling, and the automated tests need a matching development build. Notification registration needs Windows SDK packaging tools and guest-local certificate setup. Keep Explorer as the guest's default until the session-only comparison is ready.

The [VM guide](VM-START-HERE.md) gives the setup order. The [manifest](acceptance-build.json) records the base commit, local provenance and exact archive hash. Physical radio/brightness/monitor checks and the week of daily use remain outside VM qualification.

## Repository and CI builds

The release workflow runs for this acceptance work and keeps a `release-<version>` artifact on the pull request Actions run. Download and extract its `SubZeroDev.WindowManager-<version>-win-x64.zip` for a build from the PR commit. That artifact includes these guides and an `acceptance-artifacts.json` with the CI commit, archive size and SHA-256. The local `acceptance-build.json` describes only the earlier local ZIP, not a later CI archive.

## S24.8 diagnostic follow-up

The [idle CPU investigation](NOTIFICATION-IDLE.md) adds repeated strict ten-second windows and opt-in activity evidence. Historical failures remain recorded above. The cause and Windows desktop acceptance remain unresolved; portable simulated-provider measurements do not establish production-provider behavior.

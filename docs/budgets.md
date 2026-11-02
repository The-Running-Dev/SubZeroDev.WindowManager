# Performance budgets

Each budget is the 90th percentile of ten measured runs plus 20%. The reference column comes from the reference machine; the CI column comes from the `budgets` job on a GitHub `windows-latest` runner, which is slower and noisier, so it has budgets of its own.

| Metric | What is measured | Reference | CI |
| --- | --- | --- | --- |
| `startupMs` | Guardian start until all three built-in plugins show a live view (ms) | — | — |
| `hostPrivateMb` | Host private bytes after 60 s idle (MiB) | — | — |
| `clockPrivateMb` | Clock worker private bytes after 60 s idle (MiB) | — | — |
| `runningWindowsPrivateMb` | Running-windows worker private bytes after 60 s idle (MiB) | — | — |
| `launcherPrivateMb` | Launcher worker private bytes after 60 s idle (MiB) | — | — |
| `hostIdleCpuPercent` | Host CPU over 60 s idle (% of one core) | — | — |
| `hotkeyToPopupMs` | Launcher hotkey until the popup is shown with the query focused, second open (ms) | — | — |
| `clickToForegroundMs` | Click on a minimised window's item until it is the foreground window (ms) | — | — |

## How they are measured

`BudgetTests` in the end-to-end tests does the measuring. Each run starts the guardian with a fresh data folder and a layout holding the clock, the running-windows strip and the launcher, then:

1. Startup is the time from starting the guardian to the latest moment one of the three views went live, stamped by the host.
2. Idle waits 60 s, then reads the host's CPU time over that minute and the private bytes of the host and each worker.
3. The hotkey opens the launcher once, closes it, then times the second open, as S8.1 does.
4. A test window is minimised and its item clicked; the test polls for it to become the foreground window.

The test hook that reports status writes `status.json` every 250 ms, so the idle CPU includes that work. A released build without the hook does less.

## Measuring

On the reference machine, from a Release build:

```bash
SUBZERODEV_WM_BUDGETS=measure SUBZERODEV_WM_BUDGET_OUT=artifacts/budgets tests/SubZeroDev.WindowManager.EndToEnd.Tests/bin/Release/net10.0-windows10.0.26100.0/win-x64/SubZeroDev.WindowManager.EndToEnd.Tests.exe -class SubZeroDev.WindowManager.EndToEnd.Tests.BudgetTests
```

`measurements.json` holds every run, the p50, p90 and maximum, and the suggested budget for each metric. Copy the suggested budgets into the table and record why in `design/90-decisions.md`.

## Checking

The `budgets` job in `.github/workflows/ci.yml` runs startup and idle three times on every pull request with `SUBZERODEV_WM_BUDGETS=ci` and fails when a median is more than 10% over its CI budget. `SUBZERODEV_WM_BUDGETS=reference` checks against the reference column instead. `SUBZERODEV_WM_BUDGET_RUNS` changes the number of runs.

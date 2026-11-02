# Performance budgets

Each budget is the 90th percentile of ten measured runs plus 20%, rounded down. The reference column comes from the reference machine (16 logical processors, Windows 11 26200, one 3440×1440 monitor at 125%), measured on 2 November 2026. The CI column comes from the `budgets` job on a GitHub `windows-latest` runner on the same day. The runner's numbers differ enough from the reference machine's that it has budgets of its own: memory is lower at 100% scale and startup is noisier.

| Metric | What is measured | Reference | CI |
| --- | --- | --- | --- |
| `startupMs` | Guardian start until all three built-in plugins show a live view (ms) | 2300 | 2400 |
| `hostPrivateMb` | Host private bytes after 60 s idle (MiB) | 114 | 73 |
| `clockPrivateMb` | Clock worker private bytes after 60 s idle (MiB) | 13.3 | 11.3 |
| `runningWindowsPrivateMb` | Running-windows worker private bytes after 60 s idle (MiB) | 14.5 | 11.9 |
| `launcherPrivateMb` | Launcher worker private bytes after 60 s idle (MiB) | 19 | 16.6 |
| `hostIdleCpuPercent` | Host CPU over 60 s idle (% of one core) | 8.1 | 6.5 |
| `hotkeyToPopupMs` | Launcher hotkey until the popup is shown with the query focused, second open (ms) | 26 | 27 |
| `clickToForegroundMs` | Click on a minimised window's item until it is the foreground window (ms) | 92 | 92 |

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

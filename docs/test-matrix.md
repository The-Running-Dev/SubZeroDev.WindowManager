# Monitor, DPI and full-screen test matrix

The automated rows run in `MonitorTests` on every build. Extra monitors there are simulated through the test hook (`<hook>\monitors`), so the real display configuration is never touched; a simulated monitor has no taskbar and the shell refuses app bars on it, so its panels show as overlay. The manual rows need real hardware or a disruptive desktop action and are run by hand.

Hardware available to the automated runs: one monitor, 3440 × 1440 at 125 % (120 DPI).

| Scenario | Acceptance | How | Result |
| --- | --- | --- | --- |
| Panel on `all` monitors, a monitor added, removed and added again | S12.1 | Automated, simulated second monitor at 100 % | Pass |
| Thickness 40 DIP on a 100 % monitor (40 px) and a 150 % monitor (60 px) | S12.2 | Automated, simulated monitors at 96 and 144 DPI | Pass |
| Full-screen window on a monitor drops its panels below it and back | S12.3 | Automated, full-screen fixture on a simulated monitor | Pass |
| Reserve panel registers again after the shell restarts | S12.4 | Automated: the app bar is removed and `TaskbarCreated` is sent to the host | Pass |
| `autoHide` collapses to 2 px and expands within 200 ms | S12.5 | Automated, cursor moved (no clicks) on the primary monitor | Pass |
| `desktop` panel stays below a normal window when activated | S12.6 | Automated, panel activated through the test hook | Pass |
| A real second monitor plugged in and out | S12.1 | Manual | Not run |
| A real display at 150 % (change the scale in Settings, then back) | S12.2 | Manual | Not run |
| A full-screen game or presentation on the primary monitor | S12.3 | Manual | Not run |
| `explorer.exe` killed and restarted | S12.4 | Manual | Not run |
| Win+D and a click on the desktop with a `desktop` panel shown | S12.6 | Manual | Not run |

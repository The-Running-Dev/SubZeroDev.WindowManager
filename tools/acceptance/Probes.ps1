# Per-gate probes for the scripted VM acceptance suite. Dot-source after Common.ps1 and call
# Initialize-Acceptance. Each probe drives the REAL desktop with SendInput and observes it
# through window enumeration, UI Automation, the host log and screenshots. A probe records
# Pass only for something it observed; anything it could not see is Inconclusive with evidence.
# Windows PowerShell 5.1 compatible.

function Get-HostWindows { param([switch] $IncludeHidden)
    $ids = Get-HostProcessIds
    if (-not $ids.Count) { return @() }
    return @(Get-TopWindows -ProcessId $ids -IncludeHidden:$IncludeHidden)
}

function Get-PopupWindows { @(Get-HostWindows | Where-Object { $_.Title -like 'SubZeroDev.WindowManager popup*' }) }

function Wait-PopupOpen { param([int] $TimeoutMs = 3000) Wait-Until -TimeoutMs $TimeoutMs -PollMs 40 -Condition { Get-PopupWindows | Select-Object -First 1 } }
function Wait-PopupClosed { param([int] $TimeoutMs = 3000) [bool](Wait-Until -TimeoutMs $TimeoutMs -PollMs 40 -Condition { -not (Get-PopupWindows).Count }) }

function Close-OpenPopups {
    for ($i = 0; $i -lt 3 -and (Get-PopupWindows).Count; $i++) { Send-KeyTap ESC; Start-Sleep -Milliseconds 250 }
}

# ------------------------------------------------------------------------------ Coexistence

function Invoke-ProbeCoexistence {
    param([int] $SoakSeconds = 30)
    $gate = 'Coexistence'
    $start = Start-WindowManager
    if (-not $start.Ready) { Add-Observation $gate 'starts-beside-explorer' Fail $start.Reason; return $false }
    Add-Observation $gate 'starts-beside-explorer' Pass 'guardian and host running, a host window is visible'
    $explorer = @(Get-Process -Name explorer -ErrorAction SilentlyContinue).Count
    Add-Observation $gate 'explorer-still-running' $(if ($explorer) { 'Pass' } else { 'Fail' }) ("explorer.exe processes: $explorer")
    $names = @(Get-HostWindows | ForEach-Object { $_.Title } | Sort-Object -Unique)
    $shot = Save-Screen 'coexistence'
    $dump = Save-WindowDump 'coexistence'
    Add-Observation $gate 'panels-visible' $(if ($names.Count) { 'Inconclusive' } else { 'Fail' }) ('host windows: ' + ($names -join ' | ') + '. Whether the panels look right needs a person; see the screenshot.') @($shot, $dump)
    $before = @(Get-HostProcessIds)
    Start-Sleep -Seconds $SoakSeconds
    $after = @(Get-HostProcessIds)
    $stable = ($before.Count -gt 0) -and (($before -join ',') -eq ($after -join ','))
    Add-Observation $gate "stable-for-$SoakSeconds-seconds" $(if ($stable) { 'Pass' } else { 'Fail' }) ('host pids before ' + ($before -join ',') + ', after ' + ($after -join ','))
    $problems = @(Get-HostProblems -Since $start.Since)
    Add-Observation $gate 'host-log-warnings-and-errors' $(if (-not $problems.Count) { 'Pass' } else { 'Inconclusive' }) ("$($problems.Count) warning/error lines since start" + $(if ($problems.Count) { ': ' + (($problems | Select-Object -First 5 | ForEach-Object { '[' + $_.Area + '] ' + $_.Message }) -join ' || ') } else { '' }))
    return $true
}

# ------------------------------------------------------------------------------ S28 Win+X, real mouse

function Invoke-ProbeWinX {
    param([int] $Attempts = 20, [string] $Entry = 'Run', [string] $LaunchedTitle = 'Run', [string] $LaunchedClass = '#32770')
    $gate = 'S28'
    $since = [DateTimeOffset]::UtcNow
    $outcomes = @{}
    $unobservable = 0
    $evidence = @()
    for ($i = 1; $i -le $Attempts; $i++) {
        Close-OpenPopups
        Start-Sleep -Milliseconds 300
        Send-Chord @('WIN') 'X'
        $popup = Wait-PopupOpen 3000
        if (-not $popup) { $outcomes['popup-not-opened'] = 1 + [int]$outcomes['popup-not-opened']; continue }
        Start-Sleep -Milliseconds 250
        $item = Find-UiaItem -Handle $popup.Handle -NameLike $Entry
        if (-not $item) {
            $unobservable++
            $outcomes['entry-not-found'] = 1 + [int]$outcomes['entry-not-found']
            if ($evidence.Count -lt 2) { $evidence += (Save-UiaDump $popup.Handle "winx-attempt$i"); $evidence += (Save-Screen "winx-no-entry-$i") }
            Close-OpenPopups
            continue
        }
        $existing = @(Get-TopWindows -TitleLike $LaunchedTitle -ClassName $LaunchedClass | ForEach-Object { $_.Handle })
        $point = Get-Center $item
        Click-At -X $point.X -Y $point.Y
        $closed = Wait-PopupClosed 2500
        $launched = Wait-Until -TimeoutMs 4000 -PollMs 60 -Condition { Get-TopWindows -TitleLike $LaunchedTitle -ClassName $LaunchedClass | Where-Object { $existing -notcontains $_.Handle } | Select-Object -First 1 }
        if (-not $closed) { $key = 'click-lost-popup-still-open' }
        elseif (-not $launched) { $key = 'popup-closed-but-nothing-launched' }
        else {
            $focused = Wait-Until -TimeoutMs 2500 -PollMs 50 -Condition { [WmAcc]::Foreground() -eq $launched.Handle }
            $key = if ($focused) { 'ok' } else { 'launched-without-focus' }
        }
        $outcomes[$key] = 1 + [int]$outcomes[$key]
        if ($key -ne 'ok' -and $evidence.Count -lt 6) { $evidence += (Save-Screen "winx-$key-$i") }
        if ($launched) { [WmAcc]::Close($launched.Handle); Start-Sleep -Milliseconds 300 }
        Close-OpenPopups
    }
    Close-OpenPopups
    $ok = [int]$outcomes['ok']
    $summary = ($outcomes.Keys | Sort-Object | ForEach-Object { '{0}={1}' -f $_, $outcomes[$_] }) -join ', '
    $verdict = Get-Verdict -Attempts $Attempts -Successes $ok -Unobservable $unobservable
    Add-Observation $gate "winx-real-mouse-x$Attempts" $verdict "$ok of $Attempts clicks on '$Entry' launched $LaunchedTitle with focus ($summary)" $evidence
    $problems = @(Get-HostProblems -Since $since)
    Add-Observation $gate 'host-log-during-winx' $(if (-not $problems.Count) { 'Pass' } else { 'Inconclusive' }) ("$($problems.Count) warning/error lines" + $(if ($problems.Count) { ': ' + (($problems | Select-Object -First 5 | ForEach-Object { '[' + $_.Area + '] ' + $_.Message }) -join ' || ') } else { '' }))
}

# ------------------------------------------------------------------------------ S31 Alt+Tab, real chords

function Test-InMenuMode { param($Window) ([WmAcc]::GuiFlags([uint32]$Window.ThreadId) -band 0x1C) -ne 0 }

function Invoke-ProbeAltTab {
    param([int] $Commits = 10, [int] $Cancels = 5)
    $gate = 'S31'
    $since = [DateTimeOffset]::UtcNow
    $a = Start-FixtureWindow 'WM-ACC-A' 'LightSalmon' 80 100
    $b = Start-FixtureWindow 'WM-ACC-B' 'LightGreen' 260 160
    $c = Start-FixtureWindow 'WM-ACC-C' 'Khaki' 440 220
    $mru = New-Object Collections.ArrayList
    foreach ($w in @($c, $a, $b)) {
        if (-not (Set-ForegroundByClick $w)) { Add-Observation $gate 'fixture-setup' Inconclusive "could not bring $($w.Title) to the foreground by a click"; Stop-Fixtures; return }
        $mru.Insert(0, $w)
    }
    $byHandle = @{ }; foreach ($w in @($a, $b, $c)) { $byHandle[$w.Handle] = $w }

    # Control: a lone Alt tap must put a native-menu app into menu mode, or the later "menu stays closed" checks mean nothing.
    Send-KeyTap ALT 60
    $control = [bool](Wait-Until -TimeoutMs 1200 -PollMs 40 -Condition { Test-InMenuMode $mru[0] })
    Send-KeyTap ESC
    Start-Sleep -Milliseconds 300
    Add-Observation $gate 'menu-mode-detector-works' $(if ($control) { 'Pass' } else { 'Inconclusive' }) $(if ($control) { 'a lone Alt tap put the fixture into menu mode, so the detector can see it' } else { 'a lone Alt tap did not reach menu mode; the menu-closed checks below are not trustworthy' })

    $okFocus = 0; $okMenu = 0; $overlaySeen = 0; $orderMismatch = @(); $menuOpened = 0; $evidence = @(); $overlayColours = @()
    for ($i = 1; $i -le $Commits; $i++) {
        $steps = 1 + ($i % 2)           # one Tab, then two Tabs, alternating
        $expected = $mru[$steps]
        $beforeHost = @(Get-HostWindows | ForEach-Object { $_.Handle })
        Send-KeyDown ALT; Start-Sleep -Milliseconds 60
        for ($s = 0; $s -lt $steps; $s++) { Send-KeyTap TAB 25; Start-Sleep -Milliseconds 120 }
        $overlay = Wait-Until -TimeoutMs 1500 -PollMs 40 -Condition { Get-HostWindows | Where-Object { $beforeHost -notcontains $_.Handle -and ($_.Right - $_.Left) -gt 100 } | Select-Object -First 1 }
        if ($overlay) {
            $overlaySeen++
            if ($i -le 2) {
                Start-Sleep -Milliseconds 400
                $evidence += (Save-Screen "alttab-overlay-$i")
                $overlayColours += (Get-RegionColourCount $overlay.Left $overlay.Top $overlay.Right $overlay.Bottom)
            }
        }
        Send-KeyUp ALT
        $landed = Wait-Until -TimeoutMs 2500 -PollMs 50 -Condition { [WmAcc]::Foreground() -eq $expected.Handle }
        if ($landed) { $okFocus++ } else {
            $actual = Get-ForegroundInfo
            $orderMismatch += ('iteration {0}: expected {1}, foreground was "{2}"' -f $i, $expected.Title, $actual.Title)
            if ($evidence.Count -lt 6) { $evidence += (Save-Screen "alttab-wrong-target-$i") }
        }
        Start-Sleep -Milliseconds 350
        $target = if ($byHandle.ContainsKey([WmAcc]::Foreground())) { $byHandle[[WmAcc]::Foreground()] } else { $expected }
        if (Test-InMenuMode $target) { $menuOpened++; Send-KeyTap ESC; Start-Sleep -Milliseconds 200 } else { $okMenu++ }
        # MRU follows what the foreground really was, so one wrong step does not poison the rest.
        $now = $byHandle[[WmAcc]::Foreground()]
        if ($now) { $mru.Remove($now); $mru.Insert(0, $now) }
    }
    Add-Observation $gate "commit-lands-on-expected-window-x$Commits" (Get-Verdict -Attempts $Commits -Successes $okFocus) ("$okFocus of $Commits Alt+Tab commits ended on the expected window" + $(if ($orderMismatch.Count) { '. ' + ($orderMismatch -join '; ') } else { '' })) $evidence
    Add-Observation $gate 'overlay-appears' (Get-Verdict -Attempts $Commits -Successes $overlaySeen) "a new host window was visible during $overlaySeen of $Commits sequences"
    $menuVerdict = if (-not $control) { 'Inconclusive' } else { Get-Verdict -Attempts $Commits -Successes $okMenu }
    Add-Observation $gate 'alt-release-leaves-menu-bar-closed' $menuVerdict "foreground app was in menu mode after $menuOpened of $Commits Alt releases (0xE8 mask)"
    $colourText = if ($overlayColours.Count) { ' distinct colours sampled in the overlay: ' + ($overlayColours -join ', ') } else { ' no overlay was captured' }
    Add-Observation $gate 'live-thumbnails' Inconclusive ("a person must confirm the thumbnails in the screenshots show the windows' contents;" + $colourText) $evidence

    # Cancel with Esc: the foreground window must not change.
    $okCancel = 0
    for ($i = 1; $i -le $Cancels; $i++) {
        $held = Get-ForegroundInfo
        Send-KeyDown ALT; Start-Sleep -Milliseconds 60
        Send-KeyTap TAB 25; Start-Sleep -Milliseconds 150
        Send-KeyTap TAB 25; Start-Sleep -Milliseconds 150
        Send-Chord @('SHIFT') 'TAB'; Start-Sleep -Milliseconds 150
        Send-KeyTap ESC; Start-Sleep -Milliseconds 200
        Send-KeyUp ALT
        Start-Sleep -Milliseconds 500
        if ([WmAcc]::Foreground() -eq $held.Handle -and -not (Test-InMenuMode $held)) { $okCancel++ } else { if ($held) { Send-KeyTap ESC } }
    }
    Add-Observation $gate "esc-cancels-x$Cancels" (Get-Verdict -Attempts $Cancels -Successes $okCancel) "$okCancel of $Cancels cancelled sequences left the same window in the foreground, menu bar closed"
    Add-Observation $gate 'fallback-when-switcher-unavailable' Skipped 'not scripted: needs the switcher plugin disabled and a restart; check by hand'
    $problems = @(Get-HostProblems -Since $since)
    Add-Observation $gate 'host-log-during-alttab' $(if (-not $problems.Count) { 'Pass' } else { 'Inconclusive' }) ("$($problems.Count) warning/error lines" + $(if ($problems.Count) { ': ' + (($problems | Select-Object -First 5 | ForEach-Object { '[' + $_.Area + '] ' + $_.Message }) -join ' || ') } else { '' }))
    Stop-Fixtures
}

# ------------------------------------------------------------------------------ S29.2 hover preview

function Invoke-ProbeHover {
    $gate = 'S29.2'
    $a = Start-FixtureWindow 'WM-ACC-HOVER' 'Plum' 160 140
    Start-Sleep -Seconds 2
    $panels = @(Get-HostWindows | Where-Object { $_.Title -like 'SubZeroDev.WindowManager panel*' })
    $button = $null; $panel = $null
    foreach ($p in $panels) {
        $found = Find-UiaItem -Handle $p.Handle -NameLike '*WM-ACC-HOVER*'
        if ($found) { $button = $found; $panel = $p; break }
    }
    if (-not $button) {
        $evidence = @(Save-Screen 'hover-no-button')
        foreach ($p in $panels) { $evidence += (Save-UiaDump $p.Handle 'hover-panel') }
        Add-Observation $gate 'preview-opens-updates-closes' Inconclusive ("no running-windows button named for the fixture was found through UI Automation in $($panels.Count) panel(s); the layout may not place the plugin, or its buttons expose no name") $evidence
        Stop-Fixtures
        return
    }
    $point = Get-Center $button
    $before = @(Get-HostWindows | ForEach-Object { $_.Handle })
    Move-MouseTo ($point.X + 4) ($point.Y + 2)
    Move-MouseTo $point.X $point.Y
    $preview = Wait-Until -TimeoutMs 4000 -PollMs 60 -Condition { Get-HostWindows | Where-Object { $before -notcontains $_.Handle } | Select-Object -First 1 }
    $shot = Save-Screen 'hover-open'
    if (-not $preview) {
        Add-Observation $gate 'preview-opens' Fail 'no new host window appeared within 4 s of hovering the running-windows button' @($shot)
        Stop-Fixtures; return
    }
    Add-Observation $gate 'preview-opens' Pass ('a host window appeared: "' + $preview.Title + '"') @($shot)
    $colours = Get-RegionColourCount $preview.Left $preview.Top $preview.Right $preview.Bottom
    Add-Observation $gate 'preview-shows-live-picture' Inconclusive "sampled $colours distinct colours in the preview; a person must confirm it shows the window" @($shot)
    # Update path: change the fixture's title and expect the preview or button to follow.
    Move-MouseTo ($point.X + 10) ($point.Y)
    Start-Sleep -Milliseconds 400
    Move-MouseTo ([Math]::Max(5, $panel.Left + 5)) ([Math]::Max(5, $panel.Top - 200))
    $closed = Wait-Until -TimeoutMs 4000 -PollMs 60 -Condition { -not (@(Get-HostWindows | Where-Object { $_.Handle -eq $preview.Handle }).Count) }
    Add-Observation $gate 'preview-closes' $(if ($closed) { 'Pass' } else { 'Fail' }) $(if ($closed) { 'the preview window closed after the pointer left' } else { 'the preview was still visible 4 s after the pointer left' }) @((Save-Screen 'hover-after-leave'))
    Add-Observation $gate 'preview-updates-while-open' Skipped 'not scripted: needs a window that repaints on cue; check by hand'
    Stop-Fixtures
}

# ------------------------------------------------------------------------------ S30 desktop

function Get-RecycleBinItem { param([string] $Name)
    $shell = New-Object -ComObject Shell.Application
    $bin = $shell.NameSpace(10)
    foreach ($item in $bin.Items()) { if ($item.Name -eq $Name -or $item.Name -eq [IO.Path]::GetFileNameWithoutExtension($Name)) { return $item } }
    return $null
}

function Invoke-ProbeDesktop {
    $gate = 'S30'
    $desktopDir = [Environment]::GetFolderPath('Desktop')
    $name = 'WM-ACC-' + [guid]::NewGuid().ToString('N').Substring(0, 8) + '.txt'
    $renamed = $name.Replace('.txt', '-renamed.txt')
    $path = Join-Path $desktopDir $name
    [IO.File]::WriteAllText($path, 'scripted acceptance file; safe to delete')
    try {
        Start-Sleep -Seconds 3
        $windows = @(Get-HostWindows | Where-Object { $_.Title -like 'SubZeroDev.WindowManager desktop*' })
        $icon = $null; $host_ = $null
        foreach ($w in $windows) { $f = Find-UiaItem -Handle $w.Handle -NameLike ($name.Replace('.txt', '') + '*'); if ($f) { $icon = $f; $host_ = $w; break } }
        if (-not $icon) {
            $evidence = @(Save-Screen 'desktop-no-icon')
            foreach ($w in $windows) { $evidence += (Save-UiaDump $w.Handle 'desktop-window') }
            Add-Observation $gate 'desktop-lists-new-file' Inconclusive ("the new Desktop file was not found through UI Automation in $($windows.Count) desktop window(s); the desktop-icons gadget may not be in this layout, or its icons expose no name") $evidence
            return
        }
        Add-Observation $gate 'desktop-lists-new-file' Pass "the desktop gadget lists $name"
        $before = @(Get-PopupWindows | ForEach-Object { $_.Handle })
        $p = Get-Center $icon
        Click-At -X $p.X -Y $p.Y -Right
        $menu = Wait-Until -TimeoutMs 3000 -PollMs 50 -Condition { Get-PopupWindows | Where-Object { $before -notcontains $_.Handle } | Select-Object -First 1 }
        $shot = Save-Screen 'desktop-icon-menu'
        if (-not $menu) { Add-Observation $gate 'right-click-icon-opens-menu' Fail 'no popup within 3 s of a real right click on the icon' @($shot); return }
        $entries = @(Get-UiaItems -Handle $menu.Handle | ForEach-Object { $_.Name } | Where-Object { $_ })
        Add-Observation $gate 'right-click-icon-opens-menu' Pass ('menu entries: ' + ($entries -join ', ')) @($shot)

        # Rename through the menu: the typed name must reach the file system.
        $rename = Find-UiaItem -Handle $menu.Handle -NameLike 'Rename*'
        if ($rename) {
            $rp = Get-Center $rename; Click-At -X $rp.X -Y $rp.Y
            Start-Sleep -Milliseconds 600
            Send-Chord @('CTRL') 'A'
            Send-Text ($renamed)
            Send-KeyTap ENTER
            $done = Wait-Until -TimeoutMs 4000 -PollMs 100 -Condition { Test-Path -LiteralPath (Join-Path $desktopDir $renamed) }
            Add-Observation $gate 'rename-takes-keyboard-focus-and-applies' $(if ($done) { 'Pass' } else { 'Fail' }) $(if ($done) { 'typing the new name and Enter renamed the file' } else { 'the file was not renamed after the menu, typing and Enter' }) @((Save-Screen 'desktop-after-rename'))
            if ($done) { $path = Join-Path $desktopDir $renamed; $name = $renamed }
        }
        else { Add-Observation $gate 'rename-takes-keyboard-focus-and-applies' Inconclusive ('no Rename entry found; entries were: ' + ($entries -join ', ')) }
        Close-OpenPopups

        # Delete goes to the Recycle Bin and comes back.
        Start-Sleep -Seconds 2
        $icon = $null
        foreach ($w in @(Get-HostWindows | Where-Object { $_.Title -like 'SubZeroDev.WindowManager desktop*' })) { $f = Find-UiaItem -Handle $w.Handle -NameLike ($name.Replace('.txt', '') + '*'); if ($f) { $icon = $f; break } }
        if ($icon) {
            $p = Get-Center $icon
            $before = @(Get-PopupWindows | ForEach-Object { $_.Handle })
            Click-At -X $p.X -Y $p.Y -Right
            $menu = Wait-Until -TimeoutMs 3000 -PollMs 50 -Condition { Get-PopupWindows | Where-Object { $before -notcontains $_.Handle } | Select-Object -First 1 }
            $delete = if ($menu) { Find-UiaItem -Handle $menu.Handle -NameLike 'Delete*' } else { $null }
            if ($delete) {
                $dp = Get-Center $delete; Click-At -X $dp.X -Y $dp.Y
                Start-Sleep -Milliseconds 800
                if ((Get-TopWindows -ClassName '#32770' | Where-Object { $_.Title -match 'Delete|Confirm' } | Select-Object -First 1)) { Send-KeyTap ENTER }
                $gone = Wait-Until -TimeoutMs 5000 -PollMs 100 -Condition { -not (Test-Path -LiteralPath $path) }
                $binItem = if ($gone) { Get-RecycleBinItem $name } else { $null }
                Add-Observation $gate 'delete-goes-to-recycle-bin' $(if ($gone -and $binItem) { 'Pass' } elseif ($gone) { 'Fail' } else { 'Fail' }) $(if ($gone -and $binItem) { 'the file left the Desktop and is in the Recycle Bin' } elseif ($gone) { 'the file left the Desktop but is not in the Recycle Bin' } else { 'the file was still on the Desktop after Delete' }) @((Save-Screen 'desktop-after-delete'))
                if ($binItem) {
                    $binItem.InvokeVerb('undelete')
                    $back = Wait-Until -TimeoutMs 6000 -PollMs 150 -Condition { Test-Path -LiteralPath $path }
                    Add-Observation $gate 'restore-from-recycle-bin' $(if ($back) { 'Pass' } else { 'Fail' }) $(if ($back) { 'restoring the item put the file back on the Desktop' } else { 'the file did not come back after the undelete verb' })
                }
            }
            else { Add-Observation $gate 'delete-goes-to-recycle-bin' Inconclusive 'no Delete entry found in the icon menu'; Close-OpenPopups }
        }
        # Empty space.
        $before = @(Get-PopupWindows | ForEach-Object { $_.Handle })
        $screen = [WmAcc]::Screen()
        Click-At -X ($screen[0] + [int]($screen[2] * 0.62)) -Y ($screen[1] + [int]($screen[3] * 0.42)) -Right
        $menu = Wait-Until -TimeoutMs 3000 -PollMs 50 -Condition { Get-PopupWindows | Where-Object { $before -notcontains $_.Handle } | Select-Object -First 1 }
        $shot = Save-Screen 'desktop-empty-menu'
        Add-Observation $gate 'right-click-empty-space-opens-menu' $(if ($menu) { 'Pass' } else { 'Fail' }) $(if ($menu) { 'menu entries: ' + ((Get-UiaItems -Handle $menu.Handle | ForEach-Object { $_.Name } | Where-Object { $_ }) -join ', ') } else { 'no popup within 3 s of a real right click on empty desktop' }) @($shot)
        Close-OpenPopups
    }
    finally {
        Close-OpenPopups
        foreach ($leftover in @($path, (Join-Path $desktopDir $name), (Join-Path $desktopDir $renamed))) {
            if ((Split-Path $leftover -Leaf) -like 'WM-ACC-*' -and (Test-Path -LiteralPath $leftover)) { Remove-Item -LiteralPath $leftover -Force -ErrorAction SilentlyContinue }
        }
    }
}

# ------------------------------------------------------------------------------ Explorer restart (coexistence)

function Invoke-ProbeExplorerRestart {
    $gate = 'ExplorerRestore'
    $hostBefore = @(Get-HostProcessIds)
    if (-not $hostBefore.Count) { Add-Observation $gate 'explorer-restart' Inconclusive 'the host is not running'; return }
    $since = [DateTimeOffset]::UtcNow
    $old = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | ForEach-Object { $_.Id })
    Stop-Process -Name explorer -Force -ErrorAction SilentlyContinue
    $back = Wait-Until -TimeoutMs 30000 -PollMs 300 -Condition { $now = @(Get-Process -Name explorer -ErrorAction SilentlyContinue | ForEach-Object { $_.Id }); if ($now.Count -and (@($now | Where-Object { $old -notcontains $_ }).Count)) { $now } }
    if (-not $back) {
        Add-Observation $gate 'explorer-respawns' Inconclusive 'Windows did not restart explorer.exe within 30 s; starting it by hand'
        Start-Process explorer.exe
        Start-Sleep -Seconds 5
    }
    else { Add-Observation $gate 'explorer-respawns' Pass 'Windows restarted explorer.exe' }
    Start-Sleep -Seconds 8
    $hostAfter = @(Get-HostProcessIds)
    Add-Observation $gate 'host-survives-explorer-restart' $(if (($hostBefore -join ',') -eq ($hostAfter -join ',')) { 'Pass' } else { 'Fail' }) ('host pids before ' + ($hostBefore -join ',') + ', after ' + ($hostAfter -join ','))
    $tray = @(Get-TopWindows -ClassName 'Shell_TrayWnd')
    Add-Observation $gate 'explorer-taskbar-back' $(if ($tray.Count) { 'Pass' } else { 'Inconclusive' }) ("Shell_TrayWnd windows: $($tray.Count)") @((Save-Screen 'explorer-restart'))
    $problems = @(Get-HostProblems -Since $since)
    Add-Observation $gate 'host-log-after-explorer-restart' $(if (-not $problems.Count) { 'Pass' } else { 'Inconclusive' }) ("$($problems.Count) warning/error lines" + $(if ($problems.Count) { ': ' + (($problems | Select-Object -First 5 | ForEach-Object { '[' + $_.Area + '] ' + $_.Message }) -join ' || ') } else { '' }))
}

# ------------------------------------------------------------------------------ replacement shell, after sign-in

function Invoke-ProbeReplacementState {
    param([datetime] $LogonUtc)
    $gate = 'Replacement'
    $registration = Get-ShellRegistration
    $explorer = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
    Add-Observation $gate 'explorer-is-not-the-shell' $(if (-not $explorer.Count) { 'Pass' } else { 'Fail' }) ("shell registration: '$registration'; explorer.exe processes: $($explorer.Count)")
    $guardian = @(Get-Process -Name 'SubZeroDev.WindowManager' -ErrorAction SilentlyContinue)
    $guardian = @(Wait-Until -TimeoutMs 45000 -PollMs 500 -Condition { Get-Process -Name 'SubZeroDev.WindowManager' -ErrorAction SilentlyContinue } | Where-Object { $_ })
    Add-Observation $gate 'guardian-started-at-sign-in' $(if ($guardian.Count) { 'Pass' } else { 'Fail' }) ("guardian processes: $($guardian.Count)")
    $ready = Start-WindowManager -ReadyTimeoutMs 120000
    Add-Observation $gate 'usable-desktop-appears' $(if ($ready.Ready) { 'Pass' } else { 'Fail' }) $(if ($ready.Ready) { 'a host window is visible' } else { $ready.Reason })
    if (-not $ready.Ready) { return $false }
    if ($LogonUtc) {
        $seconds = [int]([DateTime]::UtcNow - $LogonUtc).TotalSeconds
        Add-Observation $gate 'sign-in-to-desktop-time' Inconclusive "about $seconds s from logon to the first observed host window (includes this script's own startup; budget is replacementStartupMs in docs/budgets.md)"
    }
    Start-Sleep -Seconds 10
    $hostIds = @(Get-HostProcessIds)
    $tray = @(Get-TopWindows -ClassName 'Shell_TrayWnd' -IncludeHidden)
    $owned = @($tray | Where-Object { $hostIds -contains $_.ProcessId })
    Add-Observation $gate 'host-owns-shell-tray-window' $(if ($owned.Count) { 'Pass' } else { 'Fail' }) ("Shell_TrayWnd windows: $($tray.Count), owned by the host: $($owned.Count)")
    $desktops = @(Get-HostWindows | Where-Object { $_.Title -like 'SubZeroDev.WindowManager desktop*' })
    Add-Observation $gate 'desktop-window-per-monitor' $(if ($desktops.Count) { 'Pass' } else { 'Fail' }) ("desktop windows: $($desktops.Count)")
    $taken = @(Get-HostLogLines | Where-Object { $_.Message -match 'ShellRoleTaken|TrayTaken' })
    Add-Observation $gate 'no-ShellRoleTaken-or-TrayTaken' $(if (-not $taken.Count) { 'Pass' } else { 'Fail' }) ("matching log lines: $($taken.Count)")
    Send-KeyTap WIN
    $launcher = Wait-PopupOpen 3000
    $shot = Save-Screen 'replacement-launcher'
    Add-Observation $gate 'windows-key-opens-launcher' $(if ($launcher) { 'Pass' } else { 'Fail' }) $(if ($launcher) { 'a lone real Windows key press opened a host popup' } else { 'no host popup after a lone Windows key press (the Start menu should stay closed)' }) @($shot)
    Close-OpenPopups
    $problems = @(Get-HostProblems -Since ([DateTimeOffset]::UtcNow.AddMinutes(-15)))
    Add-Observation $gate 'host-log-since-sign-in' $(if (-not $problems.Count) { 'Pass' } else { 'Inconclusive' }) ("$($problems.Count) warning/error lines" + $(if ($problems.Count) { ': ' + (($problems | Select-Object -First 8 | ForEach-Object { '[' + $_.Area + '] ' + $_.Message }) -join ' || ') } else { '' }))
    return $true
}

function Invoke-ProbeBackToExplorer {
    param([string] $ExpectedRegistration)
    $gate = 'WaysBack'
    $registration = Get-ShellRegistration
    $explorer = @(Get-Process -Name explorer -ErrorAction SilentlyContinue)
    $same = ([string]$registration -eq [string]$ExpectedRegistration)
    Add-Observation $gate 'registration-restored-by-shell-off' $(if ($same) { 'Pass' } else { 'Fail' }) ("registration now '$registration', recorded before '$ExpectedRegistration'")
    $tray = @(Get-TopWindows -ClassName 'Shell_TrayWnd' -IncludeHidden | Where-Object { $explorer.Id -contains $_.ProcessId })
    Add-Observation $gate 'explorer-is-the-shell-again' $(if ($explorer.Count -and $tray.Count) { 'Pass' } else { 'Fail' }) ("explorer.exe processes: $($explorer.Count); taskbar windows owned by Explorer: $($tray.Count)") @((Save-Screen 'back-to-explorer'))
    $guardian = @(Get-Process -Name 'SubZeroDev.WindowManager' -ErrorAction SilentlyContinue)
    Add-Observation $gate 'guardian-not-started-after-shell-off' $(if (-not $guardian.Count) { 'Pass' } else { 'Fail' }) ("guardian processes: $($guardian.Count)")
}

# ------------------------------------------------------------------------------ evidence-only and not-scripted rows

function Invoke-ProbeJumpListEvidence {
    $gate = 'S29.4'
    $directory = Join-Path $env:APPDATA 'Microsoft\Windows\Recent\AutomaticDestinations'
    $count = if (Test-Path -LiteralPath $directory) { @(Get-ChildItem -LiteralPath $directory -File).Count } else { 0 }
    Add-Observation $gate 'jump-list-sources-present' Skipped "the guest has $count automatic-destination files; resolving them is only observable in the host's own unit tests, so a person checks real jump lists on the taskbar"
}

function Add-NotScriptedRows {
    foreach ($row in (Get-StillManual)) { Add-Observation 'NotScripted' $row.Substring(0, [Math]::Min(48, $row.Length)) Skipped $row }
}

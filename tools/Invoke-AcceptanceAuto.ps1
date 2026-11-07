<#
.SYNOPSIS
Runs as much of docs/acceptance.md as a script can, inside the disposable acceptance VM.
.DESCRIPTION
Drives the real desktop with SendInput and observes it through window enumeration, UI
Automation, the host log and screenshots. It never sets the test hook, never marks anything
a person must judge as Pass, and writes everything to <install>\acceptance-results:
auto-observations.csv, auto-report.md, screenshots, UI dumps and the evidence ZIP.

Stages (each runs once, in order):
  coexistence  start beside Explorer, soak, hover preview, desktop menu, Win+X real-mouse
               probe, Alt+Tab, the native taskbar client (the Explorer baseline), Win+D with
               a desktop panel, a full-screen window, the two sample presets (screenshots),
               Explorer restart.
  replacement  only with -IncludeReplacement: registers the guest shell (and, if the bundle
               carries TaskbarProbe.exe, turns on the host's taskbar trace), signs out, resumes
               by itself after sign-in (a scheduled sign-in task), probes the replacement
               shell with the same probes, restores Explorer, signs out again and checks
               Explorer is back.

Guards: refuses to run unless the machine reports a hypervisor AND a VM manufacturer/model,
and unless you pass -ConfirmDisposableVm. The replacement stage also needs -SnapshotTaken
and -AllowSignOut. Sign back in yourself when the guest signs out (no password is handled).
-Abort restores the previous shell registration and removes the sign-in task.
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = $PSScriptRoot,
    [switch] $ConfirmDisposableVm,
    [switch] $IncludeReplacement,
    [switch] $SnapshotTaken,
    [switch] $AllowSignOut,
    [ValidateSet('Coexistence', 'WinX', 'AltTab', 'Hover', 'Desktop', 'ExplorerRestart', 'TaskbarTransport', 'ShowDesktop', 'FullScreen', 'Samples')]
    [string[]] $Only,
    [ValidateRange(1, 200)]
    [int] $WinXAttempts = 20,
    [switch] $Resume,
    [switch] $Abort,
    [switch] $ReportOnly
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this script inside the Windows acceptance VM.' }
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$exe = Join-Path $install 'SubZeroDev.WindowManager.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Launcher missing: $exe. Run the bootstrap first, or pass -InstallDirectory." }
$results = Join-Path $install 'acceptance-results'
[IO.Directory]::CreateDirectory($results) | Out-Null
$stateFile = Join-Path $results 'auto-state.json'
$taskName = 'WindowManagerAcceptanceResume'
$modules = Join-Path $PSScriptRoot 'acceptance'
. (Join-Path $modules 'Common.ps1')
. (Join-Path $modules 'Probes.ps1')

# ---------------------------------------------------------------- state and helpers

function Read-State {
    if (Test-Path -LiteralPath $stateFile) { return (Get-Content -LiteralPath $stateFile -Raw | ConvertFrom-Json) }
    return $null
}
function Save-State($State) { [IO.File]::WriteAllText($stateFile, ($State | ConvertTo-Json -Depth 4), $script:AccUtf8) }

function Invoke-Guardian {
    param([string[]] $Arguments, [string] $Label)
    $stamp = Get-Stamp
    $out = Join-Path $results "auto-$Label-$stamp.txt"
    $err = Join-Path $results "auto-$Label-$stamp.stderr.txt"
    $p = Start-Process -FilePath $exe -WorkingDirectory $install -ArgumentList $Arguments -RedirectStandardOutput $out -RedirectStandardError $err -Wait -PassThru -WindowStyle Hidden
    return [pscustomobject]@{ ExitCode = $p.ExitCode; Output = $out; Error = $err }
}

function Register-ResumeTask {
    $argument = '-NoProfile -ExecutionPolicy Bypass -File "{0}" -Resume -ConfirmDisposableVm -InstallDirectory "{1}"' -f $PSCommandPath, $install
    try {
        $user = [Security.Principal.WindowsIdentity]::GetCurrent().Name
        $action = New-ScheduledTaskAction -Execute 'powershell.exe' -Argument $argument
        $trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
        $trigger.Delay = 'PT15S'
        $principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Limited
        $settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -ExecutionTimeLimit (New-TimeSpan -Hours 2)
        Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Force | Out-Null
        return $true
    }
    catch {
        Write-Warning ('Could not register the sign-in task: ' + $_.Exception.Message + ' Run this script from an elevated Windows PowerShell for the replacement stage.')
        return $false
    }
}

function Remove-ResumeTask { try { Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue } catch { } }

function Get-LogonUtc {
    try {
        $session = (Get-Process -Id $PID).SessionId
        $winlogon = Get-Process -Name winlogon -ErrorAction Stop | Where-Object { $_.SessionId -eq $session } | Select-Object -First 1
        if ($winlogon) { return $winlogon.StartTime.ToUniversalTime() }
    }
    catch { }
    return $null
}

function Invoke-Probe {
    param([string] $Gate, [scriptblock] $Body)
    try { & $Body }
    catch { Add-Observation $Gate 'probe-error' Inconclusive ($_.Exception.Message) }
    finally {
        try { Close-OpenPopups } catch { }
        try { Stop-Fixtures } catch { }
    }
}

function Invoke-SignOut {
    Write-Host 'Signing out in 15 seconds. Sign back in; the run resumes by itself.' -ForegroundColor Cyan
    Start-Sleep -Seconds 15
    Start-Process -FilePath 'shutdown.exe' -ArgumentList '/l'
}

# ---------------------------------------------------------------- stages

function Invoke-CoexistenceStage {
    param([string[]] $Selected)
    $all = -not $Selected -or $Selected.Count -eq 0
    function Want([string] $Name) { return ($all -or ($Selected -contains $Name)) }
    $null = Initialize-Acceptance -Install $install -Results $results -Stage $(if ($all) { 'coexistence' } else { 'adhoc' })
    if ($all -or (Want 'Coexistence') -or -not (Get-HostProcessIds).Count) {
        Invoke-Probe 'Coexistence' { [void](Invoke-ProbeCoexistence) }
    }
    if (-not (Get-HostProcessIds).Count) {
        Add-Observation 'Coexistence' 'later-probes' Inconclusive 'the host is not running, so the interactive probes were not attempted'
        return
    }
    if (Want 'Hover') { Invoke-Probe 'S29.2' { Invoke-ProbeHover } }
    if (Want 'Desktop') { Invoke-Probe 'S30' { Invoke-ProbeDesktop } }
    if (Want 'WinX') { Invoke-Probe 'S28' { Invoke-ProbeWinX -Attempts $WinXAttempts } }
    if (Want 'AltTab') { Invoke-Probe 'S31' { Invoke-ProbeAltTab } }
    if (Want 'TaskbarTransport') { Invoke-Probe 'S29.3' { Invoke-ProbeTaskbarTransport -Mode Baseline } }
    if (Want 'ShowDesktop') { Invoke-Probe 'S12.6' { Invoke-ProbeShowDesktop } }
    if (Want 'FullScreen') { Invoke-Probe 'S12.3' { Invoke-ProbeFullScreen } }
    if (Want 'Samples') { Invoke-Probe 'S14.4' { Invoke-ProbeSampleShots } }
    if (Want 'ExplorerRestart') { Invoke-Probe 'ExplorerRestore' { Invoke-ProbeExplorerRestart } }
    Invoke-Probe 'S29.4' { Invoke-ProbeJumpListEvidence }
    if ($all) { Stop-WindowManager }
}

$traceVariable = 'SUBZERODEV_WM_TASKBAR_TRACE'

# The host's opt-in taskbar trace is switched on through the user environment, which the replacement
# shell inherits at the next sign-in. It is removed again on every way out of the run.
function Set-TaskbarTraceVariable {
    param($State)
    if (-not (Test-Path -LiteralPath (Join-Path $install 'TaskbarProbe.exe'))) { return '' }
    $path = Join-Path $results ('host-taskbar-trace-' + (Get-Stamp) + '.jsonl')
    try { Set-ItemProperty -Path 'HKCU:\Environment' -Name $traceVariable -Value $path -Type String }
    catch { Write-Warning ('Could not enable the host taskbar trace: ' + $_.Exception.Message); return '' }
    $State | Add-Member -NotePropertyName taskbarTrace -NotePropertyValue $path -Force
    return $path
}

function Remove-TaskbarTraceVariable {
    try { Remove-ItemProperty -Path 'HKCU:\Environment' -Name $traceVariable -ErrorAction SilentlyContinue } catch { }
}

function Start-ReplacementArm {
    param($State)
    $null = Initialize-Acceptance -Install $install -Results $results -Stage 'replacement-arm'
    $State.shellBefore = [string](Get-ShellRegistration)
    $State.phase = 'replacement-run'
    [void](Set-TaskbarTraceVariable $State)
    Save-State $State
    $on = Invoke-Guardian @('--shell', 'on') 'shell-on'
    if ($on.ExitCode -ne 0) {
        $State.phase = 'aborted'; Save-State $State; Remove-ResumeTask; Remove-TaskbarTraceVariable
        Add-Observation 'Replacement' 'shell-on' Fail ("--shell on exited with $($on.ExitCode); see $($on.Output) and $($on.Error). Nothing was changed.")
        return $false
    }
    $after = [string](Get-ShellRegistration)
    Add-Observation 'Replacement' 'shell-on-registers-this-user' $(if ($after -ne $State.shellBefore) { 'Pass' } else { 'Inconclusive' }) ("registration before '$($State.shellBefore)', after '$after'")
    return $true
}

function Invoke-ReplacementStage {
    param($State)
    $null = Initialize-Acceptance -Install $install -Results $results -Stage 'replacement-run'
    try {
        Start-Sleep -Seconds 25
        $logon = Get-LogonUtc
        $state_ = if ($logon) { @(Invoke-ProbeReplacementState -LogonUtc $logon) } else { @(Invoke-ProbeReplacementState) }
        $ok = [bool]($state_ | Select-Object -Last 1)
        if ($ok) {
            $script:Acc.GateSuffix = ' (replacement)'
            Invoke-Probe 'S29.2' { Invoke-ProbeHover }
            Invoke-Probe 'S30' { Invoke-ProbeDesktop }
            Invoke-Probe 'S28' { Invoke-ProbeWinX -Attempts $WinXAttempts }
            Invoke-Probe 'S31' { Invoke-ProbeAltTab }
            $tracePath = [string]$State.taskbarTrace
            Invoke-Probe 'S29.3' { Invoke-ProbeTaskbarTransport -Mode Host -TracePath $tracePath }
            Invoke-Probe 'S12.6' { Invoke-ProbeShowDesktop }
            Invoke-Probe 'S12.3' { Invoke-ProbeFullScreen }
            Invoke-Probe 'S14.4' { Invoke-ProbeSampleShots }
        }
    }
    catch { Add-Observation 'Replacement' 'stage-error' Inconclusive $_.Exception.Message }
    finally {
        $script:Acc.GateSuffix = ''
        try { Stop-Fixtures } catch { }
        Remove-TaskbarTraceVariable
        # Whatever happened above, the guest must go back to Explorer.
        $off = Invoke-Guardian @('--shell', 'off') 'shell-off'
        Add-Observation 'WaysBack' 'shell-off-exit-code' $(if ($off.ExitCode -eq 0) { 'Pass' } else { 'Fail' }) ("--shell off exited with $($off.ExitCode); see $($off.Output)")
        $State.phase = 'restore-check'
        Save-State $State
    }
    if ($AllowSignOut) { Invoke-SignOut } else { Write-Host 'Sign out and back in to finish; the run resumes after sign-in.' -ForegroundColor Cyan }
}

function Invoke-RestoreStage {
    param($State)
    $null = Initialize-Acceptance -Install $install -Results $results -Stage 'restore-check'
    try {
        Start-Sleep -Seconds 25
        Invoke-ProbeBackToExplorer -ExpectedRegistration $State.shellBefore
    }
    catch { Add-Observation 'WaysBack' 'stage-error' Inconclusive $_.Exception.Message }
    $State.phase = 'done'
    Save-State $State
    Remove-ResumeTask
    Remove-TaskbarTraceVariable
}

# ---------------------------------------------------------------- report and hand-off to the manual runner

function Complete-Run {
    $csv = Join-Path $results 'auto-observations.csv'
    $records = if (Test-Path -LiteralPath $csv) { @(Import-Csv -LiteralPath $csv) } else { @() }
    $facts = @{}
    $os = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $facts['Windows'] = "build $($os.CurrentBuild).$($os.UBR)"
    $facts['Generated (UTC)'] = [DateTime]::UtcNow.ToString('o')
    $facts['Install'] = $install
    $receipt = Join-Path $install 'vm-install.json'
    if (Test-Path -LiteralPath $receipt) { $facts['Build receipt'] = 'vm-install.json present' }
    $facts['Stages seen'] = (($records | ForEach-Object { $_.stage } | Sort-Object -Unique) -join ', ')
    $system = Get-CimInstance Win32_ComputerSystem
    $facts['Machine'] = "$($system.Manufacturer) / $($system.Model), hypervisor=$($system.HypervisorPresent)"
    $report = Get-AcceptanceReport -Records $records -StillManual (Get-StillManual) -Facts $facts
    $reportPath = Join-Path $results 'auto-report.md'
    [IO.File]::WriteAllText($reportPath, $report, $script:AccUtf8)
    Write-Host "Report: $reportPath" -ForegroundColor Cyan

    $runner = Join-Path $install 'Test-AcceptanceVM.ps1'
    if (-not (Test-Path -LiteralPath $runner)) { Write-Warning 'Test-AcceptanceVM.ps1 is not installed; skipped the observations.csv and evidence ZIP hand-off.'; return }
    $tests = @{}
    foreach ($g in ($records | Group-Object gate)) {
        if ($g.Name -eq 'NotScripted') { continue }
        $test = Get-RunnerTest $g.Name
        $key = if ($test -eq 'Other') { $g.Name } else { $test }
        if (-not $tests.ContainsKey($key)) { $tests[$key] = [pscustomobject]@{ Test = $test; Verdicts = @(); Gates = @() } }
        $tests[$key].Verdicts += @($g.Group | ForEach-Object { $_.verdict })
        $tests[$key].Gates += $g.Name
    }
    foreach ($key in ($tests.Keys | Sort-Object)) {
        $entry = $tests[$key]
        $verdict = Get-GateVerdict $entry.Verdicts
        $counts = ($entry.Verdicts | Group-Object | ForEach-Object { '{0} {1}' -f $_.Count, $_.Name }) -join ', '
        $notes = "Scripted ($($entry.Gates -join ', ')): $counts. Not a human judgement; see auto-report.md."
        try { & $runner -Action Record -InstallDirectory $install -Test $entry.Test -Result (ConvertTo-RunnerResult $verdict) -Notes $notes -NoOpen | Out-Null }
        catch { Write-Warning ("Could not record $key in observations.csv: " + $_.Exception.Message) }
    }
    try { & $runner -Action Collect -InstallDirectory $install -NoOpen }
    catch { Write-Warning ('Evidence collection failed: ' + $_.Exception.Message) }
    Write-Host ''
    Write-Host 'Still manual:' -ForegroundColor Yellow
    foreach ($item in (Get-StillManual)) { Write-Host ('  - ' + $item) }
}

function Invoke-AbortRun {
    $null = Initialize-Acceptance -Install $install -Results $results -Stage 'abort'
    Stop-WindowManager
    $off = Invoke-Guardian @('--shell', 'off') 'abort-shell-off'
    Remove-ResumeTask
    Remove-TaskbarTraceVariable
    $state = Read-State
    if ($state) { $state.phase = 'aborted'; Save-State $state }
    Write-Host ("--shell off exited with $($off.ExitCode). Sign-in task removed. If this session is still under the replacement shell, sign out to get Explorer back, or restore the VM snapshot.") -ForegroundColor Yellow
}

# ---------------------------------------------------------------- main

$mutex = New-Object Threading.Mutex($false, 'Local\SubZeroDevWmAcceptanceAuto')
if (-not $mutex.WaitOne(0)) { throw 'Another acceptance run is active in this session.' }
$transcript = $false
try { Start-Transcript -Path (Join-Path $results ('auto-transcript-' + (Get-Stamp) + '.txt')) -Append | Out-Null; $transcript = $true } catch { }
try {
    if ($Abort) { Invoke-AbortRun; return }
    if ($ReportOnly) { $null = Initialize-Acceptance -Install $install -Results $results -Stage 'report'; Complete-Run; return }
    $null = Assert-DisposableVm -Acknowledged:$ConfirmDisposableVm

    if ($Resume) {
        $state = Read-State
        if (-not $state -or @('replacement-run', 'restore-check') -notcontains [string]$state.phase) { Write-Host 'No acceptance run is waiting to resume.'; return }
        if ($state.phase -eq 'replacement-run') { Invoke-ReplacementStage $state }
        else { Invoke-RestoreStage $state; Complete-Run }
        return
    }

    if ($Only -and $Only.Count) {
        Invoke-CoexistenceStage -Selected $Only
        Complete-Run
        return
    }

    $existing = Read-State
    if ($existing -and @('done', 'aborted') -notcontains [string]$existing.phase) {
        throw "An acceptance run is in phase '$($existing.phase)'. Finish it, or run with -Abort first."
    }
    if ($IncludeReplacement) {
        if (-not $SnapshotTaken) { throw 'The replacement stage needs -SnapshotTaken: take a restorable VM snapshot first.' }
        if (-not $AllowSignOut) { throw 'The replacement stage signs the guest out twice. Pass -AllowSignOut to allow it.' }
        if (-not (Register-ResumeTask)) { throw 'The replacement stage needs the sign-in task. Nothing was changed.' }
    }
    $state = [pscustomobject]@{ phase = 'coexistence'; includeReplacement = [bool]$IncludeReplacement; shellBefore = ''; startedUtc = [DateTime]::UtcNow.ToString('o'); install = $install; taskbarTrace = '' }
    Save-State $state
    Invoke-CoexistenceStage
    $next = Get-NextPhase 'coexistence' ([bool]$IncludeReplacement)
    if ($next -eq 'replacement-arm') {
        if (Start-ReplacementArm $state) { Invoke-SignOut }
        else { Complete-Run }
    }
    else {
        $state.phase = 'done'; Save-State $state
        Complete-Run
    }
}
finally {
    if ($transcript) { try { Stop-Transcript | Out-Null } catch { } }
    $mutex.ReleaseMutex()
}

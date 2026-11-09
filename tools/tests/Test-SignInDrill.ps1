# Dependency-free harness tests. No actual desktop launches, shell writes,
# sign-outs, host terminations or policy changes. Windows adds read-only probes.
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$script = Join-Path $repo 'tools/SignInDrill/Invoke-SignInDrill.ps1'
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('WM-signin-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$tokens = $null; $parseErrors = $null
[Management.Automation.Language.Parser]::ParseFile($script, [ref]$tokens, [ref]$parseErrors) | Out-Null
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
. $script -EvidenceDirectory $testRoot
$script:checks = 0
function Assert($Condition, [string] $Message) {
    if (-not $Condition) { throw $Message }
    $script:checks++
}
function Expect-Error([scriptblock] $Run, [string] $Pattern) {
    try { & $Run }
    catch {
        if ($_.Exception.Message -notlike $Pattern) { throw }
        $script:checks++
        return
    }
    throw "Expected rejection: $Pattern"
}
function New-RegistryValue([bool] $Present = $false, $Value = $null, $Kind = $null) {
    [pscustomobject]@{ hive = 'HKCU'; path = 'unused-in-mocked-test'; name = 'Shell'; keyPresent = $false; present = $Present; value = $Value; kind = $Kind }
}
function New-Registration {
    [pscustomobject]@{
        userShell = New-RegistryValue; machineShell = New-RegistryValue $true 'explorer.exe' 'String'
        userPolicy = New-RegistryValue; machinePolicy = New-RegistryValue
        shellLauncher = New-RegistryValue; userShellLauncher = New-RegistryValue; run = New-RegistryValue
    }
}
$identity = [pscustomobject]@{ computer = 'test'; sid = 'S-1-test'; user = 'test-user' }
$baseline = [pscustomobject]@{ identity = $identity; registration = New-Registration; dataRoot = (Join-Path $testRoot 'data'); installDirectory = (Join-Path $testRoot 'install'); shellState = [pscustomobject]@{ present = $false; base64 = $null } }
Assert-DrillIdentity $baseline $identity
$script:checks++
Expect-Error { Assert-DrillIdentity $baseline ([pscustomobject]@{ computer = 'other'; sid = 'S-1-test' }) } '*different computer or user*'
Expect-Error { Assert-DrillIdentity $baseline ([pscustomobject]@{ computer = 'test'; sid = 'other' }) } '*different computer or user*'
Assert-DrillClean $baseline.registration $baseline.shellState
$script:checks++
foreach ($override in @('userPolicy','machinePolicy','shellLauncher','userShellLauncher')) {
    $reg = New-Registration
    $reg.$override = New-RegistryValue $true 'someone-elses-shell.exe' 'String'
    Expect-Error { Assert-DrillClean $reg $baseline.shellState } '*no policy is changed*'
}
$reg = New-Registration; $reg.machineShell = New-RegistryValue $true 'foreign.exe' 'String'
Expect-Error { Assert-DrillClean $reg $baseline.shellState } '*Machine Shell is not Explorer*'
$reg = New-Registration; $reg.run = New-RegistryValue $true 'guardian' 'String'
Expect-Error { Assert-DrillClean $reg $baseline.shellState } '*would confound*'
$reg = New-Registration; $reg.userShell = New-RegistryValue $true 'foreign-shell.exe' 'String'
Expect-Error { Assert-DrillClean $reg $baseline.shellState } '*clean test account*'
$reg.userShell = New-RegistryValue $true 'explorer.exe' 'ExpandString'
Expect-Error { Assert-DrillClean $reg $baseline.shellState } '*clean test account*'
$reg.userShell = New-RegistryValue $true 'explorer.exe' 'String'
Assert-DrillClean $reg $baseline.shellState
$script:checks++
$registered = [pscustomobject]@{ present = $true; base64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes('{"registered":true}')) }
Expect-Error { Assert-DrillClean $reg $registered } '*already says registered*'

# On Windows only: exercise native/CIM/registry reads without any fake adapters.
if ($env:OS -eq 'Windows_NT') {
    $native = Get-DrillShell
    Assert ($native.logonId -match '^[0-9a-f]{8}:[0-9a-f]{8}$') 'Invalid token logon ID.'
    $build = Get-DrillBuild
    Assert (-not [string]::IsNullOrWhiteSpace($build.build)) 'Build probe missing.'
    $actualRegistration = Get-DrillRegistration
    $actualProcesses = @(Get-DrillProcesses)
    Write-DrillJson @{ shell = $native; windows = $build; registration = $actualRegistration; processes = $actualProcesses } (Join-Path $testRoot 'windows-readonly.json')
    'PASS: actual Windows native/CIM/registration reads (not sign-in acceptance).'
    # Exercise actual entry-point guards; no registry mutation is reachable.
    $gateRoot = Join-Path $testRoot 'entry-guards'
    Expect-Error { & $script -Action Initialize -EvidenceDirectory $gateRoot } '*requires -IsolatedAccount -SnapshotTaken*'
    Assert (-not [IO.Directory]::Exists($gateRoot)) 'Initialization guard wrote evidence.'
    [IO.Directory]::CreateDirectory($gateRoot) | Out-Null
    Expect-Error { & $script -Action Initialize -EvidenceDirectory $gateRoot -IsolatedAccount -SnapshotTaken } '*already exists*'
    $gateBaseline = @{ schemaVersion = 1; identity = (Get-DrillIdentity) }
    Write-DrillJson $gateBaseline (Join-Path $gateRoot 'baseline.json')
    foreach ($action in @('Register','Unregister','RestoreBaseline')) {
        Expect-Error { & $script -Action $action -EvidenceDirectory $gateRoot } '*requires -Force*'
    }
}

# All writes/launches below are adapters; files and JSON/CSV collection are real.
function Get-DrillIdentity { $identity }
function Get-DrillRegistration { $script:registration }
function Write-DrillUserShell($Shell) { $script:registration.userShell = $Shell }
function Get-DrillBuild { [pscustomobject]@{ build = 'mock-build' } }
function Get-DrillShell { [pscustomobject]@{ logonId = 'mock-logon'; desktop = @{ hwnd = 100; pid = 200 }; tray = @{ hwnd = 101; pid = 200 } } }
function Get-DrillProcesses { $script:processes }
function Get-DrillOverrides { @() }
$script:registration = New-Registration
$script:processes = @()
Assert-DrillStopped
$script:checks++
$script:processes = @([pscustomobject]@{ currentSession = $true; name = 'SubZeroDev.WindowManager.exe' })
Expect-Error { Assert-DrillStopped } '*never stops them*'
$script:processes = @([pscustomobject]@{ currentSession = $false; name = 'SubZeroDev.WindowManager.exe' })
Assert-DrillStopped
$script:checks++
$script:processes = @()
[IO.Directory]::CreateDirectory($baseline.dataRoot) | Out-Null
$statePath = Join-Path $baseline.dataRoot 'shell.json'
[IO.File]::WriteAllText($statePath, '{"registered":true}')
Restore-DrillBaseline $baseline
Assert (-not [IO.File]::Exists($statePath)) 'Absent original shell.json was not restored.'
Assert (-not $script:registration.userShell.present) 'Absent original value was not restored.'
$original = [Text.Encoding]::UTF8.GetBytes("{ `"registered`": false }`r`n")
$baseline.shellState = [pscustomobject]@{ present = $true; base64 = [Convert]::ToBase64String($original) }
$baseline.registration.userShell = New-RegistryValue $true 'explorer.exe' 'String'
Restore-DrillBaseline $baseline
Assert ((Read-DrillFile $statePath).base64 -ceq $baseline.shellState.base64) 'Original shell.json bytes changed.'
Assert ($script:registration.userShell.value -ceq 'explorer.exe') 'Original explorer value lost.'
function Write-DrillUserShell($Shell) { $script:registration.userShell = New-RegistryValue $true 'incorrect' 'String' }
Expect-Error { Restore-DrillBaseline $baseline } '*Baseline comparison failed*'
$script:registration = New-Registration
$baseline.registration.userShell = New-RegistryValue
$command = '"' + (Join-Path $baseline.installDirectory 'SubZeroDev.WindowManager.exe') + '" --mode replacement'
$script:registration.userShell = New-RegistryValue $true $command 'String'
Write-DrillJson @{ registered = $true; previous = @{ present = $false } } $statePath
Assert-DrillApplicationRegistration $baseline $true
$script:checks++
Write-DrillJson @{ registered = $true; previous = @{ present = $true; value = 'other' } } $statePath
Expect-Error { Assert-DrillApplicationRegistration $baseline $true } '*Registration/state read-back*'
$script:registration.userShell = New-RegistryValue
Write-DrillJson @{ registered = $false; previous = @{ present = $false } } $statePath
Assert-DrillApplicationRegistration $baseline $false
$script:checks++
$script:registration.userShell = New-RegistryValue $true $command 'String'
Expect-Error { Assert-DrillApplicationRegistration $baseline $false } '*unregistration read-back*'
$script:registration = New-Registration

$logRoot = Join-Path $baseline.dataRoot 'logs'
[IO.Directory]::CreateDirectory($logRoot) | Out-Null
[IO.File]::WriteAllText((Join-Path $logRoot 'guardian-20261105.log'), 'guardian marker')
[IO.File]::WriteAllBytes((Join-Path $logRoot 'host-20261105.log'), [byte[]]@())
[IO.File]::WriteAllText((Join-Path $logRoot 'host-20261104.log'), ('x' * (1MB + 17)))
[IO.File]::WriteAllText((Join-Path $baseline.dataRoot 'settings.json'), '{"startAtSignIn":false,"restartPolicy":{"maxRestarts":3,"windowSeconds":300},"private":"excluded"}')
$folder = Save-DrillCapture $baseline $testRoot 'bounded-files'
$snapshot = Get-Content -LiteralPath (Join-Path $folder 'snapshot.json') -Raw | ConvertFrom-Json
Assert ($snapshot.acceptance -like 'Pending*') 'Capture inferred acceptance.'
Assert ($snapshot.shell.desktop.pid -eq 200) 'Actual owner identity was lost.'
Assert (@($snapshot.files).Count -eq 3) 'Wrong selected logs.'
$large = @($snapshot.files | Where-Object truncated)
Assert ($large.Count -eq 1 -and $large[0].copiedBytes -eq 1MB) 'Copy limit not enforced/reported.'
$empty = @($snapshot.files | Where-Object originalBytes -eq 0)
Assert ($empty.Count -eq 1 -and (Get-Item -LiteralPath (Join-Path $folder $empty[0].file)).Length -eq 0) 'Empty log failed.'
Assert ($snapshot.startupSettings.restartPolicy.maxRestarts -eq 3) 'Restart settings missing.'
Assert (-not ((Get-Content -LiteralPath (Join-Path $folder 'snapshot.json') -Raw) -match 'excluded')) 'Full settings leaked.'
function Get-DrillShell { throw 'native probe deliberately failed' }
$errorFolder = Save-DrillCapture $baseline $testRoot 'probe-failed'
$errorSnapshot = Get-Content -LiteralPath (Join-Path $errorFolder 'snapshot.json') -Raw | ConvertFrom-Json
Assert (@($errorSnapshot.errors | Where-Object { $_ -like '*native probe deliberately failed*' }).Count -eq 1) 'Probe error hidden.'

Expect-Error { Add-DrillObservation $testRoot 'Registration' 'Pass' '' @() } '*require Notes and Evidence*'
Expect-Error { Add-DrillObservation $testRoot 'Registration' 'Pass' 'observed' @('missing') } '*must exist inside*'
Expect-Error { Add-DrillObservation $testRoot 'Registration' 'Fail' 'observed' @('../outside') } '*must exist inside*'
Add-DrillObservation $testRoot 'Registration' 'Pending' '' @()
Add-DrillObservation $testRoot 'Shift' 'Fail' 'Failed, with "quotes" retained' @([IO.Path]::GetFileName($folder))
$rows = @(Import-Csv -LiteralPath (Join-Path $testRoot 'observations.csv'))
Assert ($rows.Count -eq 2 -and $rows[0].result -eq 'Pending' -and $rows[1].result -eq 'Fail') 'Results inferred or replaced.'
Assert ($rows[1].notes -ceq 'Failed, with "quotes" retained') 'CSV quoting lost.'

function Start-Process {
    param($FilePath,$WorkingDirectory,$ArgumentList,[switch]$PassThru,$RedirectStandardOutput,$RedirectStandardError)
    $script:verb = $ArgumentList -join ' '
    $fake = [pscustomobject]@{ ExitCode = $script:exitCode }
    $fake | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $script:exits }
    $fake | Add-Member ScriptMethod Kill { $script:killed = $true }
    $fake
}
[IO.Directory]::CreateDirectory($baseline.installDirectory) | Out-Null
$exe = Join-Path $baseline.installDirectory 'SubZeroDev.WindowManager.exe'
[IO.File]::WriteAllText($exe, 'never executed')
$script:exitCode = 0; $script:exits = $true; $script:killed = $false
Invoke-DrillRegistration $baseline 'on' $folder
Assert ($script:verb -eq '--shell on') 'Incorrect registration command.'
Invoke-DrillRegistration $baseline 'off' $folder
Assert ($script:verb -eq '--shell off') 'Incorrect restoration command.'
$script:exitCode = 5
Expect-Error { Invoke-DrillRegistration $baseline 'on' $folder } '*returned 5*'
$script:exits = $false
Expect-Error { Invoke-DrillRegistration $baseline 'on' $folder } '*registration may be partial*'
Assert $script:killed 'Timed-out command was not bounded.'
"PASS: $script:checks assertions, including negative cases. PowerShell $($PSVersionTable.PSVersion). Evidence retained: $testRoot"

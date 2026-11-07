# Runs real extraction and evidence collection; all desktop launches/registration
# are mocked. Requires the public acceptance bundle, not an SDK or Pester.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string] $BundlePath)
$ErrorActionPreference = 'Stop'
$repo = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$installer = Join-Path $repo 'tools\Start-AcceptanceVM.ps1'
$bundle = [IO.Path]::GetFullPath($BundlePath)
# Intentionally use the drive root: the original Windows PowerShell 5.1 bug
# occurred at C:\, while tests using a nested TEMP directory missed it.
$testRoot = Join-Path ([IO.Path]::GetPathRoot($env:SystemRoot)) ('Codex-WM-regression-' + [guid]::NewGuid().ToString('N'))
$global:wmAcceptanceBundle = $bundle
$global:wmAcceptanceBadDownload = $false
$global:wmAcceptanceLaunches = @()
function Invoke-WebRequest {
    param($Uri, $OutFile, $TimeoutSec, [switch]$UseBasicParsing)
    if ($global:wmAcceptanceBadDownload) { [IO.File]::WriteAllText($OutFile, 'corrupt') }
    else { Copy-Item -LiteralPath $global:wmAcceptanceBundle -Destination $OutFile }
}
function Start-Process {
    param($FilePath, $WorkingDirectory, $ArgumentList, $RedirectStandardOutput, $RedirectStandardError, $WindowStyle, [switch]$Wait, [switch]$PassThru)
    $global:wmAcceptanceLaunches += [pscustomobject]@{ file = $FilePath; arguments = $ArgumentList }
    if ($RedirectStandardOutput) { [IO.File]::WriteAllText($RedirectStandardOutput, '{}') }
    if ($RedirectStandardError) { [IO.File]::WriteAllText($RedirectStandardError, '') }
    if ($PassThru) {
        $process = [pscustomobject]@{ Id = 12345; ExitCode = 0 }
        $process | Add-Member ScriptMethod WaitForExit { param($milliseconds) return $false }
        $process
    }
}
function Expect-Error([scriptblock] $Run, [string] $Message) {
    try { & $Run }
    catch { if ($_.Exception.Message -notlike $Message) { throw }; return }
    throw "Expected an exception matching: $Message"
}
$originalDataRoot = $env:SUBZERODEV_WM_DATA_ROOT
try {
    foreach ($file in @($installer, (Join-Path $repo 'tools\Test-AcceptanceVM.ps1'))) {
        $tokens = $null; $parseErrors = $null
        [Management.Automation.Language.Parser]::ParseFile($file, [ref]$tokens, [ref]$parseErrors) | Out-Null
        if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
    }
    $source = [IO.File]::ReadAllText($installer)
    if ($PSVersionTable.PSEdition -ne 'Core') {
        $beforeFix = $source.Replace('[IO.Directory]::CreateDirectory($parent) | Out-Null','New-Item -ItemType Directory -Path $parent -Force | Out-Null')
        Expect-Error { & ([scriptblock]::Create($beforeFix)) -InstallDirectory $testRoot -NoLaunch } '*path is not of a legal form*'
        'PASS: reverting the fix reproduces the drive-root failure in Windows PowerShell.'
    }
    & $installer -InstallDirectory $testRoot -NoLaunch
    if ($global:wmAcceptanceLaunches.Count) { throw 'NoLaunch started a process.' }
    if (-not (Test-Path (Join-Path $testRoot 'Test-AcceptanceVM.ps1'))) { throw 'Installed runner missing.' }
    if (-not (Test-Path (Join-Path $testRoot 'TESTING.md'))) { throw 'Installed manual missing.' }
    'PASS: real bundle installed directly below the drive root; no desktop process launched.'

    # Exercise the actual IEX entry point using the existing receipt, with launches mocked.
    $iexSource = $source.Replace("[string] `$InstallDirectory = 'C:\WindowManager'", "[string] `$InstallDirectory = '$testRoot'")
    $preference = $ErrorActionPreference
    $iexSource | Invoke-Expression
    if ($ErrorActionPreference -ne $preference) { throw 'IEX changed caller preference.' }
    # A running guardian may suppress the launch. Notepad must always be requested.
    if (@($global:wmAcceptanceLaunches | Where-Object file -eq 'notepad.exe').Count -ne 1) { throw 'IEX did not open instructions.' }
    $global:wmAcceptanceLaunches = @()
    'PASS: repeat IEX verifies installed hashes and opens instructions.'

    $global:wmAcceptanceBadDownload = $true
    Expect-Error { & $installer -InstallDirectory ($testRoot + '-bad') -NoLaunch } '*SHA-256 mismatch*'
    if (Test-Path ($testRoot + '-bad')) { throw 'Corrupt bundle installed.' }
    $global:wmAcceptanceBadDownload = $false
    $foreign = Join-Path $testRoot 'existing'
    [IO.Directory]::CreateDirectory($foreign) | Out-Null
    [IO.File]::WriteAllText((Join-Path $foreign 'keep.txt'), 'keep')
    Expect-Error { & $installer -InstallDirectory $foreign -NoLaunch } '*already exists without*'
    if ([IO.File]::ReadAllText((Join-Path $foreign 'keep.txt')) -ne 'keep') { throw 'Existing files changed.' }
    $victim = Join-Path $testRoot 'TESTING.md'
    $before = [IO.File]::ReadAllBytes($victim)
    try {
        [IO.File]::AppendAllText($victim, 'modified')
        Expect-Error { & $installer -InstallDirectory $testRoot -NoLaunch } '*SHA-256 mismatch*'
    }
    finally { [IO.File]::WriteAllBytes($victim, $before) }
    'PASS: corrupt downloads, unknown folders and modified installed files are refused.'

    # Bundles built before the scripted suite existed legitimately lack it; when present it must be complete.
    $autoInstalled = @('Invoke-AcceptanceAuto.ps1', 'acceptance\Logic.ps1', 'acceptance\Common.ps1', 'acceptance\Probes.ps1' |
        Where-Object { Test-Path -LiteralPath (Join-Path $testRoot $_) -PathType Leaf })
    if ($autoInstalled.Count -eq 0) { 'NOTE: this bundle predates the scripted acceptance suite; install of it not exercised.' }
    elseif ($autoInstalled.Count -ne 4) { throw "Scripted acceptance suite partly installed: $($autoInstalled -join ', ')" }
    else { 'PASS: the scripted acceptance suite is installed beside the manual runner.' }
    $runner = Join-Path $testRoot 'Test-AcceptanceVM.ps1'
    $env:SUBZERODEV_WM_DATA_ROOT = Join-Path $testRoot 'fake-data'
    [IO.Directory]::CreateDirectory((Join-Path $env:SUBZERODEV_WM_DATA_ROOT 'logs')) | Out-Null
    [IO.File]::WriteAllText((Join-Path $env:SUBZERODEV_WM_DATA_ROOT 'logs\test.log'), 'test log')
    [IO.File]::WriteAllText((Join-Path $env:SUBZERODEV_WM_DATA_ROOT 'settings.json'), 'not for collection')
    & $runner -Action Checklist -NoOpen
    & $runner -Action Record -Test WinX -Result Fail -Notes '4 misses, 20 clicks; commas and quotes "retained"'
    $rows = @(Import-Csv (Join-Path $testRoot 'acceptance-results\observations.csv'))
    if ($rows.Count -ne 1 -or $rows[0].result -ne 'Fail' -or $rows[0].notes -ne '4 misses, 20 clicks; commas and quotes "retained"') { throw 'Observation lost or reclassified.' }
    Expect-Error { & $runner -Action EnableShell } '*requires -SnapshotTaken -Force*'
    if ($global:wmAcceptanceLaunches.Count) { throw 'Registration was invoked without flags.' }
    & $runner -Action EnableShell -SnapshotTaken -Force
    & $runner -Action RestoreShell
    if ($global:wmAcceptanceLaunches.Count -ne 2 -or ($global:wmAcceptanceLaunches[0].arguments -join ' ') -ne '--shell on' -or ($global:wmAcceptanceLaunches[1].arguments -join ' ') -ne '--shell off') { throw 'Wrong registration commands.' }
    'PASS: status/checklist/CSV work; missing snapshot flags refuse registration; explicit on/off commands are correct (mocked).'
    & $runner -Action Collect
    $zipPath = Get-ChildItem (Join-Path $testRoot 'acceptance-results') -Filter '*.zip' | Select-Object -First 1
    $zip = [IO.Compression.ZipFile]::OpenRead($zipPath.FullName)
    try {
        $names = @($zip.Entries | ForEach-Object { $_.FullName.Replace('\','/') })
        if ('logs/test.log' -notin $names -or 'results/observations.csv' -notin $names -or 'vm-install.json' -notin $names -or 'collection.json' -notin $names) { throw 'Evidence incomplete.' }
        if (@($names | Where-Object { $_ -like '*settings.json*' }).Count) { throw 'Settings included in evidence.' }
    }
    finally { $zip.Dispose() }
    'PASS: real evidence ZIP contains observations, logs and provenance; settings excluded.'
    "ALL CHECKS PASSED on PowerShell $($PSVersionTable.PSVersion). Files retained: $testRoot"
}
finally { $env:SUBZERODEV_WM_DATA_ROOT = $originalDataRoot }

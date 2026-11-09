<#
.SYNOPSIS
Collects issue #28 evidence; registration and rollback require explicit actions.
.DESCRIPTION
Run in 64-bit PowerShell 5.1+ as the isolated Windows test user. Default Capture
is read-only except evidence files. Never signs out, reboots, stops processes,
changes policy, breaks the host or infers an acceptance pass. See README.md.
#>
[CmdletBinding()]
param(
    [ValidateSet('Initialize','Capture','Register','Unregister','RestoreBaseline','Record')]
    [string] $Action = 'Capture',
    [Parameter(Mandatory=$true)][string] $EvidenceDirectory,
    [string] $InstallDirectory = 'C:\WindowManager',
    [ValidateSet('Registration','ShellOff','Settings','RestoreCommand','Shift','MissingHost','RestartBudget','ColdStart')]
    [string] $Test = 'Registration',
    [ValidateSet('before','after','restored')][string] $Phase = 'after',
    [ValidateSet('Pending','Pass','Fail','Blocked')][string] $Result = 'Pending',
    [string] $Notes = '',
    [string[]] $Evidence = @(),
    [switch] $IsolatedAccount,
    [switch] $SnapshotTaken,
    [switch] $Force
)

function Write-DrillJson($Value, [string] $Path) {
    $json = ConvertTo-Json -InputObject $Value -Depth 14
    [IO.File]::WriteAllText($Path, $json + "`n", (New-Object Text.UTF8Encoding($false)))
}
function Get-DrillIdentity {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    try { [pscustomobject]@{ computer = $env:COMPUTERNAME; sid = $identity.User.Value; user = $identity.Name } }
    finally { $identity.Dispose() }
}
function Assert-DrillIdentity($Baseline, $Identity) {
    if ($Baseline.identity.computer -ne $Identity.computer -or $Baseline.identity.sid -ne $Identity.sid) {
        throw 'Baseline belongs to a different computer or user. Do not restore another account through HKCU.'
    }
}
function Get-DrillOverrides {
    $found = @()
    foreach ($scope in @('Process','User','Machine')) {
        foreach ($entry in [Environment]::GetEnvironmentVariables($scope).GetEnumerator()) {
            if ($entry.Key -like 'SUBZERODEV_WM_*' -and -not [string]::IsNullOrEmpty([string]$entry.Value)) {
                $found += [pscustomobject]@{ scope = $scope; name = $entry.Key; value = [string]$entry.Value }
            }
        }
    }
    return $found
}
function Read-DrillRegistry([string] $Hive, [string] $Path, [string] $Name = 'Shell') {
    $root = if ($Hive -eq 'HKCU') { [Microsoft.Win32.Registry]::CurrentUser } else { [Microsoft.Win32.Registry]::LocalMachine }
    $key = $root.OpenSubKey($Path)
    try {
        $present = $null -ne $key -and $Name -in $key.GetValueNames()
        [pscustomobject]@{
            hive = $Hive; path = $Path; name = $Name; keyPresent = ($null -ne $key); present = $present
            kind = $(if ($present) { $key.GetValueKind($Name).ToString() } else { $null })
            value = $(if ($present) { $key.GetValue($Name, $null, [Microsoft.Win32.RegistryValueOptions]::DoNotExpandEnvironmentNames) } else { $null })
        }
    }
    finally { if ($key) { $key.Dispose() } }
}
function Get-DrillRegistration {
    $winlogon = 'Software\Microsoft\Windows NT\CurrentVersion\Winlogon'
    $policy = 'Software\Microsoft\Windows\CurrentVersion\Policies\System'
    $launcher = 'SOFTWARE\Microsoft\Windows Embedded\Shell Launcher'
    $sid = (Get-DrillIdentity).sid
    [pscustomobject]@{
        userShell = Read-DrillRegistry 'HKCU' $winlogon
        machineShell = Read-DrillRegistry 'HKLM' $winlogon
        userPolicy = Read-DrillRegistry 'HKCU' $policy
        machinePolicy = Read-DrillRegistry 'HKLM' $policy
        shellLauncher = Read-DrillRegistry 'HKLM' $launcher
        userShellLauncher = Read-DrillRegistry 'HKLM' "$launcher\$sid"
        run = Read-DrillRegistry 'HKCU' 'Software\Microsoft\Windows\CurrentVersion\Run' 'SubZeroDev.WindowManager'
    }
}
function Read-DrillFile([string] $Path) {
    if (-not [IO.File]::Exists($Path)) { return [pscustomobject]@{ present = $false; base64 = $null } }
    [pscustomobject]@{ present = $true; base64 = [Convert]::ToBase64String([IO.File]::ReadAllBytes($Path)) }
}
function Get-DrillProcesses {
    $session = [Diagnostics.Process]::GetCurrentProcess().SessionId
    @(Get-CimInstance Win32_Process -Filter "Name='explorer.exe' OR Name='SubZeroDev.WindowManager.exe' OR Name='SubZeroDev.WindowManager.Host.exe'" -OperationTimeoutSec 10 | ForEach-Object {
        $owner = $null; $ownerError = $null
        try {
            $answer = Invoke-CimMethod -InputObject $_ -MethodName GetOwnerSid -OperationTimeoutSec 10
            if ($answer.ReturnValue -eq 0) { $owner = $answer.Sid } else { $ownerError = "GetOwnerSid returned $($answer.ReturnValue)" }
        }
        catch { $ownerError = $_.Exception.Message }
        [pscustomobject]@{
            name = $_.Name; pid = $_.ProcessId; parentPid = $_.ParentProcessId; sessionId = $_.SessionId
            currentSession = ($_.SessionId -eq $session); ownerSid = $owner; ownerError = $ownerError
            executable = $_.ExecutablePath; commandLine = $_.CommandLine; created = $_.CreationDate
        }
    })
}
function Initialize-DrillNative {
    if ('SignInDrill.Native' -as [type]) { return }
    Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
using System.Security.Principal;
namespace SignInDrill {
    public static class Native {
        [DllImport("user32.dll")] public static extern IntPtr GetShellWindow();
        [DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr FindWindow(string cls, string title);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
        [DllImport("advapi32.dll", SetLastError=true)] private static extern bool GetTokenInformation(IntPtr token, int info, IntPtr data, int length, out int needed);
        public static string LogonId() {
            using (var identity = WindowsIdentity.GetCurrent()) {
                int needed;
                GetTokenInformation(identity.Token, 10, IntPtr.Zero, 0, out needed);
                var data = Marshal.AllocHGlobal(needed);
                try {
                    if (!GetTokenInformation(identity.Token, 10, data, needed, out needed)) throw new System.ComponentModel.Win32Exception();
                    return ((uint)Marshal.ReadInt32(data, 12)).ToString("x8") + ":" + ((uint)Marshal.ReadInt32(data, 8)).ToString("x8");
                } finally { Marshal.FreeHGlobal(data); }
            }
        }
    }
}
'@
}
function Get-DrillShellOwner([IntPtr] $Window) {
    [uint32] $ownerPid = 0
    if ($Window -ne [IntPtr]::Zero) { [void][SignInDrill.Native]::GetWindowThreadProcessId($Window, [ref]$ownerPid) }
    [pscustomobject]@{ hwnd = $Window.ToInt64(); pid = $ownerPid }
}
function Get-DrillShell {
    Initialize-DrillNative
    [pscustomobject]@{
        logonId = [SignInDrill.Native]::LogonId()
        desktop = Get-DrillShellOwner ([SignInDrill.Native]::GetShellWindow())
        tray = Get-DrillShellOwner ([SignInDrill.Native]::FindWindow('Shell_TrayWnd', $null))
    }
}
function Get-DrillBuild {
    $key = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $os = Get-CimInstance Win32_OperatingSystem -OperationTimeoutSec 10
    [pscustomobject]@{
        edition = $key.EditionID; product = $os.Caption; build = $key.CurrentBuild; revision = $key.UBR
        displayVersion = $key.DisplayVersion; lastBoot = $os.LastBootUpTime; architecture = $env:PROCESSOR_ARCHITECTURE
        powershell = $PSVersionTable.PSVersion.ToString(); sessionId = [Diagnostics.Process]::GetCurrentProcess().SessionId
    }
}
function Get-DrillHash([string] $Path) {
    if ([IO.File]::Exists($Path)) { return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
    return $null
}
function Get-DrillProvenance([string] $Install) {
    @('SubZeroDev.WindowManager.exe','host\SubZeroDev.WindowManager.Host.exe','host\SubZeroDev.WindowManager.Host.dll','SubZeroDev.WindowManager.Host.exe','SubZeroDev.WindowManager.Host.dll','vm-install.json','acceptance-artifacts.json') | ForEach-Object {
        $path = Join-Path $Install $_
        [pscustomobject]@{ path = $path; sha256 = Get-DrillHash $path }
    }
}
function Save-DrillCapture($Baseline, [string] $Root, [string] $Label) {
    $folder = Join-Path $Root ((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss-fff') + '-' + $Label + '-' + [guid]::NewGuid().ToString('N').Substring(0,8))
    [IO.Directory]::CreateDirectory($folder) | Out-Null
    $errors = @()
    $snapshot = [ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); label = $Label; acceptance = 'Pending unless explicitly observed and recorded'; identity = Get-DrillIdentity }
    foreach ($probe in @{
        registration = { Get-DrillRegistration }; processes = { Get-DrillProcesses }; shell = { Get-DrillShell }
        windows = { Get-DrillBuild }; environmentOverrides = { @(Get-DrillOverrides) }
        provenance = { @(Get-DrillProvenance $Baseline.installDirectory) }
        shellState = { Read-DrillFile (Join-Path $Baseline.dataRoot 'shell.json') }
        startupSettings = {
            $settingsPath = Join-Path $Baseline.dataRoot 'settings.json'
            if ([IO.File]::Exists($settingsPath)) {
                $settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
                [pscustomobject]@{ sha256 = Get-DrillHash $settingsPath; startAtSignIn = $settings.startAtSignIn; restartPolicy = $settings.restartPolicy }
            }
        }
    }.GetEnumerator()) {
        try { $snapshot[$probe.Key] = & $probe.Value }
        catch { $errors += "$($probe.Key): $($_.Exception.Message)" }
    }
    # Preserve only bounded relevant logs, crash records and this session's journal.
    $candidates = @()
    foreach ($pattern in @('guardian-*.log','host-*.log')) {
        $enumerationErrors = @()
        $candidates += @(Get-ChildItem -LiteralPath (Join-Path $Baseline.dataRoot 'logs') -Filter $pattern -File -ErrorAction SilentlyContinue -ErrorVariable enumerationErrors | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 2)
        $errors += @($enumerationErrors | ForEach-Object { $_.ToString() })
    }
    $enumerationErrors = @()
    $candidates += @(Get-ChildItem -LiteralPath (Join-Path $Baseline.dataRoot 'logs\crash') -Filter '*.json' -File -ErrorAction SilentlyContinue -ErrorVariable enumerationErrors | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 8)
    $errors += @($enumerationErrors | ForEach-Object { $_.ToString() })
    $journal = Join-Path $Baseline.dataRoot ('recovery\session-' + [Diagnostics.Process]::GetCurrentProcess().SessionId + '.json')
    if ([IO.File]::Exists($journal)) { $candidates += Get-Item -LiteralPath $journal }
    $copies = @(); $index = 0
    foreach ($file in $candidates) {
        $index++
        $target = Join-Path $folder ("{0:D2}-{1}" -f $index, $file.Name)
        try {
            # Read a suffix with sharing enabled, at most 1 MiB per file; metadata records truncation.
            $stream = [IO.File]::Open($file.FullName, 'Open', 'Read', 'ReadWrite,Delete')
            try {
                $length = $stream.Length; $count = [int][Math]::Min($length, 1MB)
                [void]$stream.Seek(-$count, [IO.SeekOrigin]::End)
                $bytes = New-Object byte[] $count; $read = 0
                while ($read -lt $count) { $n = $stream.Read($bytes, $read, $count - $read); if ($n -eq 0) { break }; $read += $n }
                if ($read -gt 0) { [IO.File]::WriteAllBytes($target, [byte[]]$bytes[0..($read-1)]) }
                else { [IO.File]::WriteAllBytes($target, [byte[]]@()) }
                $copies += [pscustomobject]@{ source = $file.FullName; file = [IO.Path]::GetFileName($target); originalBytes = $length; copiedBytes = $read; truncated = ($length -gt $read) }
            }
            finally { $stream.Dispose() }
        }
        catch { $errors += "$($file.FullName): $($_.Exception.Message)" }
    }
    $snapshot['files'] = $copies
    $snapshot['logsFound'] = ($candidates.Count -gt 0)
    $snapshot['errors'] = $errors
    Write-DrillJson $snapshot (Join-Path $folder 'snapshot.json')
    Write-Host "Capture: $folder"
    return $folder
}
function Assert-DrillClean($Registration, $State) {
    foreach ($name in @('userPolicy','machinePolicy','shellLauncher','userShellLauncher')) {
        if ($Registration.$name.present) { throw "Existing $name Shell value: use a clean isolated account/VM; no policy is changed by this drill." }
    }
    if ($Registration.machineShell.present -and $Registration.machineShell.value -ine 'explorer.exe') { throw 'Machine Shell is not Explorer. Use a standard isolated VM; no machine configuration is changed.' }
    $shell = $Registration.userShell
    if ($shell.present -and ($shell.kind -ne 'String' -or $shell.value -ine 'explorer.exe')) { throw 'Initial per-user Shell must be absent or REG_SZ explorer.exe. Use a clean test account.' }
    if ($Registration.run.present) { throw 'Disable coexistence startAtSignIn/Run registration before initializing; it would confound real sign-in evidence.' }
    if ($State.present) {
        $json = [Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($State.base64)) | ConvertFrom-Json
        if ($json.registered) { throw 'shell.json already says registered. Restore the existing setup before initializing.' }
    }
}
function Assert-DrillStopped {
    $running = @(Get-DrillProcesses | Where-Object { $_.currentSession -and $_.name -ne 'explorer.exe' })
    if ($running.Count) { throw 'Exit guardian and host in this session before rollback/registration. The harness never stops them.' }
}
function Invoke-DrillRegistration($Baseline, [string] $Verb, [string] $Folder) {
    $exe = Join-Path $Baseline.installDirectory 'SubZeroDev.WindowManager.exe'
    if (-not [IO.File]::Exists($exe)) { throw "Guardian missing: $exe; use RestoreBaseline for independent recovery." }
    $process = Start-Process -FilePath $exe -WorkingDirectory $Baseline.installDirectory -ArgumentList @('--shell', $Verb) -PassThru -RedirectStandardOutput (Join-Path $Folder "shell-$Verb.stdout.txt") -RedirectStandardError (Join-Path $Folder "shell-$Verb.stderr.txt")
    if (-not $process.WaitForExit(15000)) {
        # Only the command launched by us, never an existing desktop process.
        $process.Kill()
        throw "--shell $Verb exceeded 15 s; registration may be partial. Capture and restore baseline before continuing."
    }
    $code = $process.ExitCode
    Write-DrillJson @{ verb = $Verb; exitCode = $code } (Join-Path $Folder "shell-$Verb.exit.json")
    if ($code -ne 0) { throw "--shell $Verb returned $code; inspect stdout/stderr. Do not sign out." }
}
function Write-DrillUserShell($Shell) {
    $shell = $Shell
    $key = [Microsoft.Win32.Registry]::CurrentUser.CreateSubKey($shell.path)
    try {
        if ($shell.present) { $key.SetValue('Shell', [string]$shell.value, [Microsoft.Win32.RegistryValueKind]::String) }
        else { $key.DeleteValue('Shell', $false) }
    }
    finally { $key.Dispose() }
}
function Assert-DrillApplicationRegistration($Baseline, [bool] $Registered) {
    $actual = (Get-DrillRegistration).userShell
    $json = Get-Content -LiteralPath (Join-Path $Baseline.dataRoot 'shell.json') -Raw | ConvertFrom-Json
    $original = $Baseline.registration.userShell
    if ($Registered) {
        $expected = '"' + (Join-Path $Baseline.installDirectory 'SubZeroDev.WindowManager.exe') + '" --mode replacement'
        if (-not $actual.present -or $actual.kind -ne 'String' -or $actual.value -cne $expected -or -not $json.registered -or $json.previous.present -ne $original.present -or ($original.present -and $json.previous.value -cne $original.value)) {
            throw 'Registration/state read-back does not match the guardian command and original baseline. Do not sign out.'
        }
    }
    elseif ($actual.present -ne $original.present -or $actual.kind -ne $original.kind -or $actual.value -cne $original.value -or $json.registered) {
        throw 'Application unregistration read-back does not match the original baseline. Capture and recover before sign-out.'
    }
}
function Restore-DrillBaseline($Baseline) {
    $shell = $Baseline.registration.userShell
    Write-DrillUserShell $shell
    $statePath = Join-Path $Baseline.dataRoot 'shell.json'
    if ($Baseline.shellState.present) {
        [IO.Directory]::CreateDirectory($Baseline.dataRoot) | Out-Null
        [IO.File]::WriteAllBytes($statePath, [Convert]::FromBase64String($Baseline.shellState.base64))
    }
    elseif ([IO.File]::Exists($statePath)) { [IO.File]::Delete($statePath) }
    # Never remove the key: another application may have added unrelated values.
    $restored = Get-DrillRegistration
    $state = Read-DrillFile $statePath
    if ($restored.userShell.present -ne $shell.present -or $restored.userShell.kind -ne $shell.kind -or $restored.userShell.value -cne $shell.value -or $state.base64 -cne $Baseline.shellState.base64 -or $state.present -ne $Baseline.shellState.present) {
        throw 'Baseline comparison failed; retain evidence and use snapshot recovery.'
    }
}
function Add-DrillObservation([string] $Root, [string] $Case, [string] $Outcome, [string] $Text, [string[]] $References) {
    if ($Outcome -ne 'Pending' -and ([string]::IsNullOrWhiteSpace($Text) -or $References.Count -eq 0)) { throw 'Observed results require Notes and Evidence capture paths.' }
    foreach ($reference in $References) {
        $path = [IO.Path]::GetFullPath((Join-Path $Root $reference))
        if (-not $path.StartsWith($Root.TrimEnd('\','/') + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $path)) { throw "Evidence must exist inside this drill directory: $reference" }
    }
    $row = [pscustomobject]@{ utc = [DateTime]::UtcNow.ToString('o'); test = $Case; result = $Outcome; notes = $Text; evidence = ($References -join '; '); source = 'Operator supplied; not inferred by harness' }
    $row | Export-Csv -LiteralPath (Join-Path $Root 'observations.csv') -NoTypeInformation -Encoding UTF8 -Append
}

# Dot-source only defines functions, permitting non-destructive adapter tests on any OS.
if ($MyInvocation.InvocationName -eq '.') { return }
$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT' -or -not [Environment]::Is64BitProcess) { throw 'Run in 64-bit Windows PowerShell 5.1+ or PowerShell 7 as the test user.' }
$root = [IO.Path]::GetFullPath($EvidenceDirectory).TrimEnd('\')
$baselinePath = Join-Path $root 'baseline.json'
$identity = Get-DrillIdentity
if ($Action -eq 'Initialize') {
    if (-not $IsolatedAccount -or -not $SnapshotTaken) { throw 'Initialize requires -IsolatedAccount -SnapshotTaken and recovery access outside this account.' }
    if ([IO.Directory]::Exists($root)) { throw 'Evidence directory already exists. Use a new directory; baseline is never overwritten.' }
    if (@(Get-DrillOverrides).Count) { throw 'Clear SUBZERODEV_WM_* overrides from Process/User/Machine before initializing.' }
    Assert-DrillStopped
    $registration = Get-DrillRegistration
    $dataRoot = Join-Path $env:LOCALAPPDATA 'SubZeroDev\WindowManager'
    $state = Read-DrillFile (Join-Path $dataRoot 'shell.json')
    Assert-DrillClean $registration $state
    $install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
    foreach ($directory in @($install, $dataRoot)) {
        if ($root -ieq $directory -or $root.StartsWith($directory + '\', [StringComparison]::OrdinalIgnoreCase)) { throw 'Keep evidence outside the installation and application data directories.' }
    }
    if (-not [IO.File]::Exists((Join-Path $install 'SubZeroDev.WindowManager.exe'))) { throw 'Guardian missing from InstallDirectory.' }
    [IO.Directory]::CreateDirectory($root) | Out-Null
    # Copy recovery script before ever allowing registration, independent of the tested installation.
    Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $root 'Invoke-SignInDrill.ps1')
    $baseline = [pscustomobject]@{ schemaVersion = 1; createdUtc = [DateTime]::UtcNow.ToString('o'); identity = $identity; installDirectory = $install; dataRoot = $dataRoot; registration = $registration; shellState = $state; provenance = @(Get-DrillProvenance $install) }
    Write-DrillJson $baseline $baselinePath
    [void](Save-DrillCapture $baseline $root 'baseline')
    Write-Host "Baseline saved. Read README.md before Register; recovery script: $root\Invoke-SignInDrill.ps1"
    return
}
if (-not [IO.File]::Exists($baselinePath)) { throw 'Initialize a new drill before this action.' }
$baseline = Get-Content -LiteralPath $baselinePath -Raw | ConvertFrom-Json
if ($baseline.schemaVersion -ne 1) { throw 'Unsupported baseline schema.' }
Assert-DrillIdentity $baseline $identity
switch ($Action) {
    'Capture' { [void](Save-DrillCapture $baseline $root "$Test-$Phase") }
    'Record' { Add-DrillObservation $root $Test $Result $Notes $Evidence }
    default {
        if (-not $Force) { throw "$Action requires -Force. Configuration was not changed." }
        if ($Action -ne 'RestoreBaseline' -and @(Get-DrillOverrides).Count) { throw 'Environment overrides would redirect registration. Configuration was not changed.' }
        $before = Save-DrillCapture $baseline $root "$Test-before-$Action"
        try {
            if ($Action -eq 'RestoreBaseline') { Assert-DrillStopped; Restore-DrillBaseline $baseline }
            elseif ($Action -eq 'Register') {
                Assert-DrillStopped
                Assert-DrillClean (Get-DrillRegistration) (Read-DrillFile (Join-Path $baseline.dataRoot 'shell.json'))
                Invoke-DrillRegistration $baseline 'on' $before
                Assert-DrillApplicationRegistration $baseline $true
            }
            else { Invoke-DrillRegistration $baseline 'off' $before; Assert-DrillApplicationRegistration $baseline $false }
        }
        finally { [void](Save-DrillCapture $baseline $root "$Test-after-$Action") }
        Write-Host "$Action complete. Observe and sign out/in manually only when ready; no acceptance result was inferred."
    }
}

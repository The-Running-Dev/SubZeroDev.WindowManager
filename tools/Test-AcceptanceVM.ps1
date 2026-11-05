<#
.SYNOPSIS
Runs the installed VM acceptance workflow without an SDK or an assistant.
.DESCRIPTION
The default action opens the checklist and records machine/build information.
Other actions are explicit; none signs out, stops Explorer or reboots the guest.
EnableShell requires both -SnapshotTaken and -Force. Observed passes are supplied
by the tester; collecting logs never marks an acceptance gate passed.
#>
[CmdletBinding()]
param(
    [ValidateSet('Checklist','Status','Start','EnableShell','RestoreShell','Record','Collect')]
    [string] $Action = 'Checklist',
    [string] $InstallDirectory = $PSScriptRoot,
    [ValidateSet('Coexistence','ReplacementSignIn','WinX','Hover','AltTab','Desktop','ExplorerRestore','Other')]
    [string] $Test = 'Other',
    [ValidateSet('Pass','Fail','Blocked','NotRun')]
    [string] $Result = 'NotRun',
    [string] $Notes = '',
    [switch] $SnapshotTaken,
    [switch] $Force,
    [switch] $NoOpen
)

$ErrorActionPreference = 'Stop'
if ($env:OS -ne 'Windows_NT') { throw 'Run this script inside the Windows acceptance VM.' }
$install = [IO.Path]::GetFullPath($InstallDirectory).TrimEnd('\')
$exe = Join-Path $install 'SubZeroDev.WindowManager.exe'
if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw "Launcher missing: $exe. Run the bootstrap first, or specify -InstallDirectory." }
$results = Join-Path $install 'acceptance-results'
[IO.Directory]::CreateDirectory($results) | Out-Null
$stamp = [DateTime]::UtcNow.ToString('yyyyMMdd-HHmmss-fff')
$utf8 = New-Object Text.UTF8Encoding($false)
$dataRoot = if ($env:SUBZERODEV_WM_DATA_ROOT) { $env:SUBZERODEV_WM_DATA_ROOT } else { Join-Path $env:LOCALAPPDATA 'SubZeroDev\WindowManager' }

function Save-Json($Value, [string] $Path) {
    [IO.File]::WriteAllText($Path, ($Value | ConvertTo-Json -Depth 8), $utf8)
}

function Save-Status {
    $windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
    $shellKey = Get-Item 'HKCU:\Software\Microsoft\Windows NT\CurrentVersion\Winlogon' -ErrorAction SilentlyContinue
    $registration = if ($shellKey) { $shellKey.GetValue('Shell', $null) } else { $null }
    $receipt = Join-Path $install 'vm-install.json'
    $build = if (Test-Path -LiteralPath $receipt) { Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json } else { $null }
    $graphics = @()
    $graphicsError = $null
    try { $graphics = @(Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, CurrentHorizontalResolution, CurrentVerticalResolution) }
    catch { $graphicsError = $_.Exception.Message }
    $status = [ordered]@{
        capturedUtc = [DateTime]::UtcNow.ToString('o')
        windows = [ordered]@{ build = $windows.CurrentBuild; revision = $windows.UBR; displayVersion = $windows.DisplayVersion; edition = $windows.EditionID }
        powershell = $PSVersionTable.PSVersion.ToString()
        architecture = $env:PROCESSOR_ARCHITECTURE
        installDirectory = $install
        build = $build
        shellRegistration = $registration
        processes = @(Get-Process -Name explorer,SubZeroDev.WindowManager,SubZeroDev.WindowManager.Host -ErrorAction SilentlyContinue | Select-Object ProcessName, Id)
        graphics = $graphics
        graphicsError = $graphicsError
    }
    $path = Join-Path $results "status-$stamp.json"
    Save-Json $status $path
    Write-Host "Status saved: $path"
}

function Invoke-Registration([string] $Verb) {
    $output = Join-Path $results "shell-$Verb-$stamp.txt"
    $errorOutput = Join-Path $results "shell-$Verb-$stamp.stderr.txt"
    $process = Start-Process -FilePath $exe -WorkingDirectory $install -ArgumentList @('--shell', $Verb) -RedirectStandardOutput $output -RedirectStandardError $errorOutput -Wait -PassThru -WindowStyle Hidden
    if ($process.ExitCode -ne 0) { throw "--shell $Verb failed with exit code $($process.ExitCode). Read $output and $errorOutput" }
    Save-Status
}

switch ($Action) {
    'Checklist' {
        Save-Status
        $checklist = Join-Path $install 'TESTING.md'
        if (-not (Test-Path -LiteralPath $checklist)) { throw "Checklist missing: $checklist" }
        Write-Host "Checklist: $checklist"
        Write-Host "Results: $results"
        if (-not $NoOpen) { Start-Process notepad.exe -ArgumentList ('"' + $checklist + '"') }
    }
    'Status' { Save-Status }
    'Start' {
        $running = @(Get-Process -Name SubZeroDev.WindowManager -ErrorAction SilentlyContinue)
        if ($running.Count) { Write-Host 'A WindowManager guardian is already running; no second instance started.' }
        else {
            $process = Start-Process -FilePath $exe -WorkingDirectory $install -PassThru -WindowStyle Hidden
            if ($process.WaitForExit(4000)) { throw "Launcher exited early with code $($process.ExitCode). Run -Action Collect to retain logs." }
            Write-Host "Launcher started (PID $($process.Id)); observe the visible desktop."
        }
        Save-Status
    }
    'EnableShell' {
        if (-not $SnapshotTaken -or -not $Force) { throw 'EnableShell requires -SnapshotTaken -Force after taking a restorable VM snapshot. Registration was not changed.' }
        Invoke-Registration 'on'
        Write-Host 'Guest per-user registration enabled. Sign out and back in manually when ready.'
    }
    'RestoreShell' {
        Invoke-Registration 'off'
        Write-Host 'Previous registration restored. Sign out and back in manually to check Explorer returns.'
    }
    'Record' {
        $row = [pscustomobject][ordered]@{ utc = [DateTime]::UtcNow.ToString('o'); test = $Test; result = $Result; notes = $Notes }
        $csv = Join-Path $results 'observations.csv'
        $row | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding UTF8 -Append
        Write-Host "Recorded $Test / $Result in $csv"
    }
    'Collect' {
        Save-Status
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $temporary = Join-Path ([IO.Path]::GetTempPath()) ('SubZeroDev-WM-evidence-' + [guid]::NewGuid().ToString('N'))
        [IO.Directory]::CreateDirectory($temporary) | Out-Null
        try {
            $target = Join-Path $temporary 'results'
            [IO.Directory]::CreateDirectory($target) | Out-Null
            Get-ChildItem -LiteralPath $results -File | Where-Object { $_.Extension -in '.json','.csv','.md','.txt','.png','.jpg' } | Copy-Item -Destination $target
            foreach ($file in @('vm-install.json','acceptance-artifacts.json','TESTING.md')) {
                $path = Join-Path $install $file
                if (Test-Path -LiteralPath $path) { Copy-Item -LiteralPath $path -Destination $temporary }
            }
            $logRoot = Join-Path $dataRoot 'logs'
            $copyErrors = @()
            if (Test-Path -LiteralPath $logRoot) { Copy-Item -LiteralPath $logRoot -Destination (Join-Path $temporary 'logs') -Recurse -ErrorAction SilentlyContinue -ErrorVariable +copyErrors }
            $bootstrapError = Join-Path ([IO.Path]::GetTempPath()) 'WindowManager-bootstrap-error.log'
            if (Test-Path -LiteralPath $bootstrapError) { Copy-Item -LiteralPath $bootstrapError -Destination $temporary }
            $collection = [ordered]@{ collectedUtc = [DateTime]::UtcNow.ToString('o'); logsFound = (Test-Path -LiteralPath $logRoot); copyErrors = @($copyErrors | ForEach-Object { $_.ToString() }); acceptance = 'Not inferred from collection; see observations.csv and manual evidence.' }
            Save-Json $collection (Join-Path $temporary 'collection.json')
            $zip = Join-Path $results "WindowManager-evidence-$stamp.zip"
            [IO.Compression.ZipFile]::CreateFromDirectory($temporary, $zip)
            Write-Host "Evidence ZIP: $zip"
            Write-Host 'Review the files before sharing. Settings and notification history were not collected.'
        }
        finally {
            $full = [IO.Path]::GetFullPath($temporary)
            if ([IO.Path]::GetDirectoryName($full).TrimEnd('\') -ne [IO.Path]::GetTempPath().TrimEnd('\') -or -not [IO.Path]::GetFileName($full).StartsWith('SubZeroDev-WM-evidence-')) { throw 'Invalid evidence cleanup boundary.' }
            Remove-Item -LiteralPath $full -Recurse -Force
        }
    }
}

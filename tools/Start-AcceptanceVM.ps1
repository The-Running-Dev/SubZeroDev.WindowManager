<#
.SYNOPSIS
Downloads, verifies and starts the pinned WindowManager VM acceptance candidate.
.DESCRIPTION
Works in Windows PowerShell 5.1 or PowerShell 7, including through irm | iex.
No SDK, GitHub login, elevation, execution-policy change or shell registration.
The public prerelease is a test candidate, not a native-acceptance claim.
.PARAMETER InstallDirectory
New installation folder. An existing folder is reused only when its receipt and
installed file hashes match this candidate; other contents are never overwritten.
.PARAMETER NoLaunch
Install and print instructions without starting the application or Notepad.
#>
[CmdletBinding()]
param(
    [string] $InstallDirectory = 'C:\WindowManager',
    [switch] $NoLaunch
)

# A child scope keeps preferences, functions and variables out of the IEX caller.
& {
    param([string] $Destination, [bool] $SkipLaunch)
    $ErrorActionPreference = 'Stop'
    $ProgressPreference = 'SilentlyContinue'
    $bundleUrl = 'https://github.com/The-Running-Dev/SubZeroDev.WindowManager/releases/download/acceptance-vm-2026-11-05-r2/WindowManager-VM-acceptance-2026-11-05-r2.zip'
    $bundleSha256 = '5d9b4e2cae958ec30c551a5985e077ae71fef23f51b297674f716353979c8674'
    $appFile = 'SubZeroDev.WindowManager-0.1.0-ci.23-win-x64.zip'
    $appSha256 = '3dff2572aaa68ee6a9ac870b8638e2ab7b897703c7448135256e0bcec1431028'
    $buildCommit = 'c86a05468b295febdf918d5b9db3b7fb61e49d5c'
    $temporary = $null
    $stage = $null

    function Get-Sha256([string] $Path) {
        $stream = [IO.File]::OpenRead($Path)
        $algorithm = [Security.Cryptography.SHA256]::Create()
        try { [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
        finally { $algorithm.Dispose(); $stream.Dispose() }
    }

    function Assert-Hash([string] $Path, [string] $Expected) {
        if ((Get-Sha256 $Path) -ne $Expected) {
            throw "SHA-256 mismatch: $Path. Nothing will be launched."
        }
    }

    function Expand-Checked([string] $Archive, [string] $Directory) {
        # Reject path traversal before extracting either archive.
        $prefix = [IO.Path]::GetFullPath($Directory).TrimEnd('\') + '\'
        $zip = [IO.Compression.ZipFile]::OpenRead($Archive)
        try {
            foreach ($entry in $zip.Entries) {
                $name = $entry.FullName.Replace('/', '\')
                $resolved = [IO.Path]::GetFullPath((Join-Path $Directory $name))
                if ([IO.Path]::IsPathRooted($name) -or $name.Contains(':') -or
                    -not $resolved.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Unsafe archive entry: $($entry.FullName)"
                }
            }
        }
        finally { $zip.Dispose() }
        [IO.Compression.ZipFile]::ExtractToDirectory($Archive, $Directory)
    }

    function Remove-Temporary([string] $Path, [string] $Parent, [string] $Prefix) {
        if (-not $Path -or -not (Test-Path -LiteralPath $Path)) { return }
        $full = [IO.Path]::GetFullPath($Path)
        $parentFull = [IO.Path]::GetFullPath($Parent).TrimEnd('\')
        if ([IO.Path]::GetDirectoryName($full).TrimEnd('\') -ne $parentFull -or
            -not [IO.Path]::GetFileName($full).StartsWith($Prefix, [StringComparison]::Ordinal)) {
            throw "Refusing cleanup outside the temporary directory boundary: $full"
        }
        Remove-Item -LiteralPath $full -Recurse -Force
    }

    try {
        if ($env:OS -ne 'Windows_NT' -or [IntPtr]::Size -ne 8 -or
            [Environment]::Is64BitOperatingSystem -ne $true -or
            [Environment]::OSVersion.Version.Build -lt 22000 -or
            $env:PROCESSOR_ARCHITECTURE -ne 'AMD64') {
            throw 'Use 64-bit Windows PowerShell 5.1 or PowerShell 7 in a Windows 11 x64 VM.'
        }
        if ($PSVersionTable.PSVersion -lt [version]'5.1') { throw 'PowerShell 5.1 or later is required.' }
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        $destinationFull = [IO.Path]::GetFullPath($Destination).TrimEnd('\')
        $parent = [IO.Path]::GetDirectoryName($destinationFull)
        if (-not $parent -or $destinationFull.StartsWith('\\')) { throw 'Choose a local installation folder, not a drive root or network path.' }

        $receiptPath = Join-Path $destinationFull 'vm-install.json'
        if (Test-Path -LiteralPath $destinationFull) {
            if (-not (Test-Path -LiteralPath $receiptPath -PathType Leaf)) {
                throw "$destinationFull already exists without this installer's receipt. Use a new -InstallDirectory; existing files were preserved."
            }
            $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json
            if ($receipt.bundleSha256 -ne $bundleSha256 -or $receipt.appSha256 -ne $appSha256 -or -not $receipt.files) {
                throw 'This folder contains another candidate or an incomplete receipt. Use a new -InstallDirectory.'
            }
            foreach ($file in $receipt.files) {
                $installed = [IO.Path]::GetFullPath((Join-Path $destinationFull $file.path))
                if (-not $installed.StartsWith($destinationFull + '\', [StringComparison]::OrdinalIgnoreCase)) {
                    throw 'Invalid file path in the install receipt.'
                }
                Assert-Hash $installed $file.sha256
            }
            Write-Host 'Existing candidate verified; reusing the installation.'
        }
        else {
            # Windows PowerShell 5.1 New-Item -Force rejects an existing drive root.
            [IO.Directory]::CreateDirectory($parent) | Out-Null
            $temporary = Join-Path ([IO.Path]::GetTempPath()) ('SubZeroDev-WM-download-' + [guid]::NewGuid().ToString('N'))
            New-Item -ItemType Directory -Path $temporary | Out-Null
            $download = Join-Path $temporary 'candidate.zip'
            Write-Host 'Downloading the pinned VM candidate (about 116 MB)...'
            $originalTls = [Net.ServicePointManager]::SecurityProtocol
            try {
                [Net.ServicePointManager]::SecurityProtocol = $originalTls -bor [Net.SecurityProtocolType]::Tls12
                Invoke-WebRequest -UseBasicParsing -Uri $bundleUrl -OutFile $download -TimeoutSec 600
            }
            finally { [Net.ServicePointManager]::SecurityProtocol = $originalTls }
            Assert-Hash $download $bundleSha256
            $expanded = Join-Path $temporary 'bundle'
            Expand-Checked $download $expanded
            $manifestPath = Join-Path $expanded 'artifacts\acceptance-artifacts.json'
            $manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
            if ($manifest.commit -ne $buildCommit -or @($manifest.archives).Count -ne 1 -or
                $manifest.archives[0].file -ne $appFile -or $manifest.archives[0].sha256 -ne $appSha256) {
                throw 'Unexpected CI build provenance in the downloaded bundle.'
            }
            $appArchive = Join-Path $expanded ('artifacts\' + $appFile)
            Assert-Hash $appArchive $appSha256
            $stage = Join-Path $parent ('SubZeroDev-WM-install-' + [guid]::NewGuid().ToString('N'))
            Expand-Checked $appArchive $stage
            Copy-Item -LiteralPath (Join-Path $expanded 'docs') -Destination (Join-Path $stage 'docs') -Recurse
            Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $stage 'acceptance-artifacts.json')
            Copy-Item -LiteralPath (Join-Path $stage 'docs\acceptance\VM-PROMPT.md') -Destination (Join-Path $stage 'VM-PROMPT.md')
            Copy-Item -LiteralPath (Join-Path $expanded 'tools\Test-AcceptanceVM.ps1') -Destination (Join-Path $stage 'Test-AcceptanceVM.ps1')
            Copy-Item -LiteralPath (Join-Path $stage 'docs\acceptance\VM-MANUAL.md') -Destination (Join-Path $stage 'TESTING.md')
            $quickStart = @"
WindowManager VM acceptance - installed to $destinationFull

Launch: $destinationFull\SubZeroDev.WindowManager.exe
Read:   $destinationFull\docs\acceptance\VM-START-HERE.md
Tests:  $destinationFull\TESTING.md (manual tests; no Codex or SDK needed)
Runner: powershell.exe -NoProfile -ExecutionPolicy Bypass -File "$destinationFull\Test-AcceptanceVM.ps1"
Gates:  $destinationFull\docs\acceptance.md
Build:  $destinationFull\acceptance-artifacts.json and vm-install.json
Logs:   %LOCALAPPDATA%\SubZeroDev\WindowManager\logs

Explorer remains enabled. Take a VM snapshot before any shell replacement test.
After reading the guide, guest-only registration commands are:
  & '$destinationFull\SubZeroDev.WindowManager.exe' --shell status
  & '$destinationFull\SubZeroDev.WindowManager.exe' --shell on
  & '$destinationFull\SubZeroDev.WindowManager.exe' --shell off
Sign out/in only when that test is prepared. Restore the VM snapshot if needed.

No compiler, native TaskbarProbe, Spy++, notification certificate or automated
test suite is installed. Native acceptance and physical device checks remain open.
The bootstrap does not stop Explorer, change the sign-in shell or reboot.
The runner opens the checklist, records observations, controls guest shell
registration only when explicitly requested, and collects an evidence ZIP.
"@
            $utf8 = New-Object Text.UTF8Encoding($false)
            [IO.File]::WriteAllText((Join-Path $stage 'VM-START-HERE.txt'), $quickStart, $utf8)
            $prefixLength = $stage.Length + 1
            $files = @(Get-ChildItem -LiteralPath $stage -Recurse -File | ForEach-Object {
                [ordered]@{ path = $_.FullName.Substring($prefixLength); sha256 = Get-Sha256 $_.FullName }
            })
            $receipt = [ordered]@{
                bundleUrl = $bundleUrl; bundleSha256 = $bundleSha256; appSha256 = $appSha256
                buildCommit = $buildCommit; version = $manifest.version
                installedUtc = [DateTime]::UtcNow.ToString('o'); files = $files
            }
            [IO.File]::WriteAllText((Join-Path $stage 'vm-install.json'), ($receipt | ConvertTo-Json -Depth 5), $utf8)
            # Both resolved paths must remain direct children of the selected local parent.
            if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($stage)) -ne $parent -or
                [IO.Path]::GetDirectoryName($destinationFull) -ne $parent) { throw 'Invalid installation move boundary.' }
            for ($attempt = 1; ; $attempt++) {
                try { [IO.Directory]::Move($stage, $destinationFull); break }
                catch [IO.IOException] {
                    if ($attempt -ge 5) { throw }
                    Start-Sleep -Milliseconds 500
                }
            }
            $stage = $null
        }

        $exe = Join-Path $destinationFull 'SubZeroDev.WindowManager.exe'
        if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) { throw 'The installed launcher is missing.' }
        Write-Host "Installed: $destinationFull"
        Write-Host "Instructions: $destinationFull\VM-START-HERE.txt"
        Write-Host "Manual tests: $destinationFull\TESTING.md"
        Write-Host "Runner: powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$destinationFull\Test-AcceptanceVM.ps1`""
        Write-Host 'Explorer is still enabled. Native acceptance is pending.'
        if (-not $SkipLaunch) {
            $running = @(Get-Process -Name 'SubZeroDev.WindowManager' -ErrorAction SilentlyContinue)
            if ($running.Count -gt 0) { Write-Host 'WindowManager is already running; no second instance started.' }
            else {
                $process = Start-Process -FilePath $exe -WorkingDirectory $destinationFull -WindowStyle Hidden -PassThru
                if ($process.WaitForExit(4000)) { throw "Launcher exited early (code $($process.ExitCode)); inspect the application logs." }
                Write-Host "Launcher started (PID $($process.Id)). Check the visible panels; this is not an acceptance pass."
            }
            Start-Process -FilePath 'notepad.exe' -ArgumentList ('"' + (Join-Path $destinationFull 'TESTING.md') + '"')
        }
    }
    catch {
        $failure = $_
        $errorLog = Join-Path ([IO.Path]::GetTempPath()) 'WindowManager-bootstrap-error.log'
        try {
            [IO.File]::WriteAllText($errorLog, ($failure | Out-String), (New-Object Text.UTF8Encoding($false)))
            Write-Warning "Setup failed. Error saved to $errorLog"
        }
        catch { Write-Warning 'Could not save the bootstrap error log.' }
        throw $failure
    }
    finally {
        # A cleanup failure must not hide the original download/extraction error.
        if ($stage) {
            try { Remove-Temporary $stage $parent 'SubZeroDev-WM-install-' }
            catch { Write-Warning "Temporary install folder retained at ${stage}: $($_.Exception.Message)" }
        }
        if ($temporary) {
            try { Remove-Temporary $temporary ([IO.Path]::GetTempPath()) 'SubZeroDev-WM-download-' }
            catch { Write-Warning "Temporary download folder retained at ${temporary}: $($_.Exception.Message)" }
        }
    }
} $InstallDirectory ([bool] $NoLaunch)

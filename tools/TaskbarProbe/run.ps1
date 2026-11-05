param(
    [Parameter(Mandatory)]
    [ValidateSet('explorer-a', 'replacement-existing', 'replacement-late', 'replacement-forced', 'explorer-restored')]
    [string] $Label
)
$ErrorActionPreference = 'Stop'
if ($env:SUBZERODEV_WM_TEST_HOOK) { throw 'Unset SUBZERODEV_WM_TEST_HOOK: this run requires the real desktop.' }
$root = (Resolve-Path "$PSScriptRoot\..\..").Path
$exe = Join-Path $root 'artifacts\taskbar-probe\TaskbarProbe.exe'
if (-not (Test-Path $exe)) { throw 'Build tools/TaskbarProbe/build.cmd first.' }
$runDirectory = Join-Path $root ("artifacts\taskbar-probe\{0}-{1}" -f $Label, [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory $runDirectory | Out-Null
$windows = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
[ordered]@{
    label = $Label
    utc = [DateTimeOffset]::UtcNow.ToString('O')
    gitHead = (& git -C $root rev-parse HEAD)
    probeSha256 = (Get-FileHash $exe -Algorithm SHA256).Hash
    windowsBuild = "$($windows.CurrentBuildNumber).$($windows.UBR)"
    displayVersion = $windows.DisplayVersion
    session = (Get-Process -Id $PID).SessionId
    user = [System.Security.Principal.WindowsIdentity]::GetCurrent().Name
    explorerProcesses = @(Get-Process explorer -ErrorAction SilentlyContinue | Select-Object Id, SessionId, Path)
    evidence = 'Label is operator supplied; verify actual Shell_TrayWnd owner in client.log / Spy++.'
} | ConvertTo-Json -Depth 4 | Set-Content -Encoding utf8 (Join-Path $runDirectory 'metadata.json')
Write-Host "Evidence: $runDirectory"
Write-Host 'F5 repeats after readiness; F6 forces native calls without readiness (separate exploratory result); Esc closes.'
& $exe (Join-Path $runDirectory 'client.log')
if ($LASTEXITCODE -ne 0) { throw "TaskbarProbe exited $LASTEXITCODE. Preserve the log and treat the run as incomplete." }

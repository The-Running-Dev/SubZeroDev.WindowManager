<#
.SYNOPSIS
Assembles the VM acceptance bundle from a downloaded CI release artifact.
.DESCRIPTION
Takes the extracted `release-<version>` CI artifact (put it in its own new, empty
folder; it is untrusted until verified), checks the app archive against the CI
provenance manifest, and writes the bundle zip that tools/Start-AcceptanceVM.ps1
downloads, plus a provenance JSON. Every document, script and the native
TaskbarProbe.exe is taken from the artifact, so the bundle contains exactly what CI
built and tested. The installer itself is fetched separately and is not in the bundle.
Prints the values to pin in Start-AcceptanceVM.ps1.
.PARAMETER ArtifactDirectory
The extracted CI artifact (contains artifacts\, docs\ and tools\).
.PARAMETER OutputDirectory
Where the zip and the provenance JSON are written. Must not already hold them.
.PARAMETER Revision
Bundle revision label, for example r1.
.PARAMETER Date
Bundle date, yyyy-MM-dd. Defaults to today.
.PARAMETER SourcePR
Optional URL of the pull request the build came from, recorded in the provenance.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $ArtifactDirectory,
    [Parameter(Mandatory)] [string] $OutputDirectory,
    [Parameter(Mandatory)] [ValidatePattern('^r\d+$')] [string] $Revision,
    [ValidatePattern('^\d{4}-\d{2}-\d{2}$')] [string] $Date = (Get-Date -Format 'yyyy-MM-dd'),
    [string] $SourcePR = ''
)

$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

function Get-Sha256([string] $Path) {
    $stream = [IO.File]::OpenRead($Path)
    $algorithm = [Security.Cryptography.SHA256]::Create()
    try { [BitConverter]::ToString($algorithm.ComputeHash($stream)).Replace('-', '').ToLowerInvariant() }
    finally { $algorithm.Dispose(); $stream.Dispose() }
}

$root = (Resolve-Path -LiteralPath $ArtifactDirectory).Path.TrimEnd('\')
$manifestPath = Join-Path $root 'artifacts\acceptance-artifacts.json'
if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) { throw "No artifacts\acceptance-artifacts.json under $root." }
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if (@($manifest.archives).Count -ne 1) { throw 'The CI manifest must list exactly one app archive.' }
$app = $manifest.archives[0]
$appPath = Join-Path $root ('artifacts\' + $app.file)
if (-not (Test-Path -LiteralPath $appPath -PathType Leaf)) { throw "Missing app archive $($app.file)." }
if ((Get-Sha256 $appPath) -ne $app.sha256) { throw 'The app archive does not match the CI manifest hash.' }
if ((Get-Item -LiteralPath $appPath).Length -ne [int64] $app.bytes) { throw 'The app archive size does not match the CI manifest.' }

# name inside the bundle -> source in the artifact. Anything else in the artifact is left out.
$entries = [ordered]@{}
$entries['artifacts/' + $app.file] = $appPath
$entries['artifacts/acceptance-artifacts.json'] = $manifestPath
foreach ($name in 'acceptance.md', 'budgets.md', 'replacement-mode.md', 'taskbar-backchannel-experiment.md', 'test-matrix.md') {
    $entries['docs/' + $name] = Join-Path $root ('docs\' + $name)
}
$excluded = 'bootstrap-build.json', 'VM-RUNNER-VERIFICATION.md'
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'docs\acceptance') -File) {
    if ($excluded -notcontains $file.Name) { $entries['docs/acceptance/' + $file.Name] = $file.FullName }
}
$entries['tools/Test-AcceptanceVM.ps1'] = Join-Path $root 'tools\Test-AcceptanceVM.ps1'
$entries['tools/Invoke-AcceptanceAuto.ps1'] = Join-Path $root 'tools\Invoke-AcceptanceAuto.ps1'
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $root 'tools\acceptance') -File -Filter '*.ps1') {
    $entries['tools/acceptance/' + $file.Name] = $file.FullName
}
$entries['tools/TaskbarProbe.exe'] = Join-Path $root 'artifacts\taskbar-probe\TaskbarProbe.exe'
foreach ($key in $entries.Keys) {
    if (-not (Test-Path -LiteralPath $entries[$key] -PathType Leaf)) { throw "The CI artifact is missing $key." }
}
foreach ($required in 'docs/acceptance/VM-PROMPT.md', 'docs/acceptance/VM-MANUAL.md', 'docs/acceptance/VM-SCRIPTED.md', 'tools/acceptance/Probes.ps1') {
    if (-not $entries.Contains($required)) { throw "The CI artifact is missing $required." }
}

$bundleName = "WindowManager-VM-acceptance-$Date-$Revision.zip"
$tag = "acceptance-vm-$Date-$Revision"
$out = [IO.Directory]::CreateDirectory($OutputDirectory).FullName
$zipPath = Join-Path $out $bundleName
$provenancePath = Join-Path $out ([IO.Path]::GetFileNameWithoutExtension($bundleName) + '.json')
if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $provenancePath)) { throw "$out already holds this bundle; choose another revision or folder." }

$zip = [IO.Compression.ZipFile]::Open($zipPath, [IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($key in $entries.Keys) {
        # Forward slashes in entry names, as zip requires; Windows PowerShell's helper writes backslashes.
        [void] [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip, $entries[$key], $key, [IO.Compression.CompressionLevel]::Optimal)
    }
}
finally { $zip.Dispose() }

$bundleSha = Get-Sha256 $zipPath
$provenance = [ordered]@{
    releaseTag     = $tag
    artifact       = $bundleName
    url            = "https://github.com/The-Running-Dev/SubZeroDev.WindowManager/releases/download/$tag/$bundleName"
    sha256         = $bundleSha
    bytes          = (Get-Item -LiteralPath $zipPath).Length
    ciRun          = [string] $manifest.run
    ciBuildCommit  = [string] $manifest.commit
    sourcePR       = $SourcePR
    appArchive     = [ordered]@{ file = $app.file; bytes = [int64] $app.bytes; sha256 = $app.sha256 }
    includes       = @($entries.Keys)
    purpose        = 'Public disposable-VM bootstrap bundle with the scripted acceptance suite; native acceptance remains open'
    bootstrapRevision = $tag
}
[IO.File]::WriteAllText($provenancePath, ($provenance | ConvertTo-Json -Depth 5), (New-Object Text.UTF8Encoding($false)))

Write-Host "Bundle:      $zipPath"
Write-Host "Provenance:  $provenancePath"
Write-Host "Pin in tools\Start-AcceptanceVM.ps1:"
Write-Host "  tag          $tag"
Write-Host "  bundleName   $bundleName"
Write-Host "  bundleSha256 $bundleSha"
Write-Host "  appFile      $($app.file)"
Write-Host "  appSha256    $($app.sha256)"
Write-Host "  buildCommit  $($manifest.commit)"

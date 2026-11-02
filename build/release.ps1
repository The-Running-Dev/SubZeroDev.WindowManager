<#
.SYNOPSIS
Builds a release: the app folder and zip, the Sdk and wm-pack packages, and the sample plugin packaged with them.

.DESCRIPTION
Layout under -Output (default artifacts\):
  release\                      the app as installed: the guardian, host\, runtime\
  SubZeroDev.WindowManager-<version>-win-x64.zip
  packages\                     SubZeroDev.WindowManager.Sdk and SubZeroDev.WindowManager.PackTool .nupkg files
  plugins\<id>.zip              wm-pack output for each built-in plugin, extracted into release\host\content\plugins
  sample\example.hello.zip      the samples\ plugin, built from packages\ only and packed by the installed tool

-Verify packs the built-in plugins again and checks that the packages and the release folders match them byte for
byte (S15.4). It builds nothing.
#>
[CmdletBinding()]
param(
    [string]$Version = '0.1.0',
    [string]$Output = (Join-Path $PSScriptRoot '..\artifacts'),
    [switch]$Verify
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Output = [IO.Path]::GetFullPath($Output)
$release = Join-Path $Output 'release'
$packages = Join-Path $Output 'packages'
$plugins = Join-Path $Output 'plugins'
$sample = Join-Path $Output 'sample'
$zip = Join-Path $Output "SubZeroDev.WindowManager-$Version-win-x64.zip"
$wmPack = Join-Path $root 'src\SubZeroDev.WindowManager.PackTool\bin\Release\net10.0\wm-pack.dll'

$builtIn = @(
    @{ Id = 'subzerodev.clock'; Project = 'SubZeroDev.WindowManager.Plugins.Clock' }
    @{ Id = 'subzerodev.running-windows'; Project = 'SubZeroDev.WindowManager.Plugins.RunningWindows' }
    @{ Id = 'subzerodev.launcher'; Project = 'SubZeroDev.WindowManager.Plugins.Launcher' }
)

function Invoke-Checked {
    param([string]$Command, [string[]]$Arguments, [int]$Attempts = 1)
    for ($attempt = 1; ; $attempt++) {
        & $Command @Arguments
        if ($LASTEXITCODE -eq 0) { return }
        if ($attempt -ge $Attempts) {
            throw "$Command $($Arguments -join ' ') failed with exit code $LASTEXITCODE."
        }
        Start-Sleep -Seconds 2
    }
}

function Get-BuildOutput([hashtable]$Plugin) {
    Join-Path $root "src\$($Plugin.Project)\bin\Release\net10.0\win-x64"
}

function Reset-Directory([string]$Path) {
    # A build server or a file sync can hold the folder for a moment after the last run.
    for ($attempt = 1; Test-Path $Path; $attempt++) {
        try { Remove-Item $Path -Recurse -Force -ErrorAction Stop }
        catch {
            if ($attempt -ge 5) { throw }
            Start-Sleep -Seconds 2
        }
    }
    New-Item -ItemType Directory -Path $Path | Out-Null
}

function Compare-Folder([string]$Expected, [string]$Actual) {
    $problems = @()
    $left = @(Get-ChildItem $Expected -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($Expected, $_.FullName) } | Sort-Object)
    $right = @(Get-ChildItem $Actual -Recurse -File | ForEach-Object { [IO.Path]::GetRelativePath($Actual, $_.FullName) } | Sort-Object)
    foreach ($difference in Compare-Object $left $right) {
        $problems += "$($difference.InputObject) is only in $(if ($difference.SideIndicator -eq '<=') { $Expected } else { $Actual })."
    }

    foreach ($file in $left | Where-Object { $right -contains $_ }) {
        if ((Get-FileHash (Join-Path $Expected $file)).Hash -ne (Get-FileHash (Join-Path $Actual $file)).Hash) {
            $problems += "$file differs."
        }
    }

    $problems
}

if ($Verify) {
    $problems = @()
    foreach ($plugin in $builtIn) {
        $scratch = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
        New-Item -ItemType Directory -Path $scratch | Out-Null
        try {
            $repacked = Join-Path $scratch "$($plugin.Id).zip"
            Invoke-Checked dotnet @($wmPack, (Get-BuildOutput $plugin), '--out', $repacked)
            if ((Get-FileHash $repacked).Hash -ne (Get-FileHash (Join-Path $plugins "$($plugin.Id).zip")).Hash) {
                $problems += "$($plugin.Id): the package differs from a fresh wm-pack run."
            }

            Expand-Archive $repacked (Join-Path $scratch 'files')
            $problems += Compare-Folder (Join-Path $scratch 'files') (Join-Path $release "host\content\plugins\$($plugin.Id)") |
                ForEach-Object { "$($plugin.Id): $_" }
        }
        finally {
            Remove-Item $scratch -Recurse -Force
        }
    }

    if ($problems) {
        $problems | ForEach-Object { Write-Error $_ -ErrorAction Continue }
        exit 1
    }

    "The built-in plugins in the release match wm-pack byte for byte."
    exit 0
}

Reset-Directory $release
Reset-Directory $packages
Reset-Directory $plugins
Reset-Directory $sample
Remove-Item $zip -ErrorAction SilentlyContinue
# The samples restore into their own cache; a package of the same version from an earlier run must not stand in.
Remove-Item (Join-Path $Output 'samples-packages\subzerodev.*') -Recurse -Force -ErrorAction SilentlyContinue

# The Sdk (with Protocol inside) and wm-pack, as plugin authors get them.
foreach ($project in 'SubZeroDev.WindowManager.Sdk', 'SubZeroDev.WindowManager.PackTool') {
    Invoke-Checked dotnet @('pack', (Join-Path $root "src\$project\$project.csproj"), '-c', 'Release', "-p:Version=$Version", '-o', $packages)
}

# The guardian at the root and the host in host\, both finding .NET in runtime\ and nowhere else.
Invoke-Checked dotnet @('publish', (Join-Path $root 'src\SubZeroDev.WindowManager.Guardian\SubZeroDev.WindowManager.Guardian.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', "-p:Version=$Version",
    '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=runtime', '-o', $release)
Invoke-Checked dotnet @('publish', (Join-Path $root 'src\SubZeroDev.WindowManager.Host.App\SubZeroDev.WindowManager.Host.App.csproj'),
    '-c', 'Release', '-r', 'win-x64', '--self-contained', 'false', "-p:Version=$Version",
    '-p:AppHostDotNetSearch=AppRelative', '-p:AppHostRelativeDotNet=..\runtime', '-o', (Join-Path $release 'host'))

# The runtime this machine builds with: the muxer, the newest host resolver and the newest .NET 10 runtime.
$runtime = & dotnet --list-runtimes |
    ForEach-Object { if ($_ -match '^Microsoft\.NETCore\.App (10\.\d+\.\d+) \[(.+)\]$') { [pscustomobject]@{ Version = [version]$Matches[1]; Shared = $Matches[2] } } } |
    Sort-Object Version | Select-Object -Last 1
if (-not $runtime) { throw 'No .NET 10 runtime is installed beside the SDK.' }
$dotnetRoot = Split-Path (Split-Path $runtime.Shared)
$fxr = Get-ChildItem (Join-Path $dotnetRoot 'host\fxr') -Directory | Where-Object Name -Match '^\d+\.\d+\.\d+$' |
    Sort-Object { [version]$_.Name } | Select-Object -Last 1
$bundled = Join-Path $release 'runtime'
New-Item -ItemType Directory -Path (Join-Path $bundled 'host\fxr'), (Join-Path $bundled 'shared\Microsoft.NETCore.App') | Out-Null
Copy-Item (Join-Path $dotnetRoot 'dotnet.exe') $bundled
foreach ($notice in 'LICENSE.txt', 'ThirdPartyNotices.txt') {
    $path = Join-Path $dotnetRoot $notice
    if (Test-Path $path) { Copy-Item $path $bundled }
}
Copy-Item $fxr.FullName (Join-Path $bundled 'host\fxr') -Recurse
Copy-Item (Join-Path $runtime.Shared $runtime.Version.ToString()) (Join-Path $bundled 'shared\Microsoft.NETCore.App') -Recurse

# Built-in plugins ship exactly as wm-pack packages them.
foreach ($plugin in $builtIn) {
    $package = Join-Path $plugins "$($plugin.Id).zip"
    Invoke-Checked dotnet @($wmPack, (Get-BuildOutput $plugin), '--out', $package)
    $installed = Join-Path $release "host\content\plugins\$($plugin.Id)"
    if (Test-Path $installed) { Remove-Item $installed -Recurse -Force }
    Expand-Archive $package $installed
}

[IO.Compression.ZipFile]::CreateFromDirectory($release, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)

# The third-party sample: the released packages and nothing from src\.
$tools = Join-Path $Output 'tools'
Reset-Directory $tools
Invoke-Checked dotnet @('tool', 'install', 'SubZeroDev.WindowManager.PackTool', '--version', $Version, '--tool-path', $tools,
    '--add-source', $packages, '--ignore-failed-sources') -Attempts 3
$sampleProject = Join-Path $root 'samples\HelloPlugin\HelloPlugin.csproj'
Invoke-Checked dotnet @('build', $sampleProject, '-c', 'Release', '--force')
Invoke-Checked (Join-Path $tools 'wm-pack.exe') @((Join-Path $root 'samples\HelloPlugin\bin\Release\net10.0'), '--out', (Join-Path $sample 'example.hello.zip'))

"Release $Version written to $Output."

# Registers (or with -Unregister removes) the probe's sparse package for the current user, pointing at the built
# probe. Packs Package\AppxManifest.xml with a generated logo into obj\ and signs it with a CN=SubZeroDev certificate
# from the user's own store, creating one if needed. Windows installs it only once the machine trusts that
# certificate, which takes administrator rights once; the script prints that command and stops until then.
param(
    [switch]$Unregister,
    [string]$Configuration = 'Release'
)
$ErrorActionPreference = 'Stop'

$name = 'SubZeroDev.WindowManager.NotificationProbe'
$existing = Get-AppxPackage -Name $name
if ($existing) { Remove-AppxPackage -Package $existing.PackageFullName }
if ($Unregister) { return }

$project = $PSScriptRoot
$output = Join-Path $project "bin\$Configuration\net10.0-windows10.0.26100.0\win-x64"
if (-not (Test-Path (Join-Path $output "$name.exe"))) { throw "Build the probe first: $output" }

$codeSigning = '1.3.6.1.5.5.7.3.3'
$certificate = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object { $_.Subject -eq 'CN=SubZeroDev' -and $_.HasPrivateKey -and $_.NotAfter -gt (Get-Date) -and
        $_.EnhancedKeyUsageList.ObjectId -contains $codeSigning } |
    Sort-Object NotAfter -Descending | Select-Object -First 1
if (-not $certificate) {
    $certificate = New-SelfSignedCertificate -Type CodeSigningCert -Subject 'CN=SubZeroDev' -FriendlyName 'SubZeroDev package signing' `
        -CertStoreLocation Cert:\CurrentUser\My -KeyUsage DigitalSignature -NotAfter (Get-Date).AddYears(5) `
        -TextExtension @("2.5.29.37={text}$codeSigning", '2.5.29.19={text}')
}

$obj = Join-Path $project 'obj'
New-Item -ItemType Directory -Force $obj | Out-Null
$publicCertificate = Join-Path $obj 'SubZeroDev.cer'
Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
    Write-Host 'The machine does not trust the signing certificate yet. In an administrator PowerShell, run once:'
    Write-Host "  Import-Certificate -FilePath '$publicCertificate' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    exit 2
}

$layout = Join-Path $obj 'package'
if (Test-Path $layout) { Remove-Item $layout -Recurse -Force }
New-Item -ItemType Directory -Force $layout | Out-Null
Copy-Item (Join-Path $project 'Package\AppxManifest.xml') $layout

Add-Type -AssemblyName System.Drawing
$bitmap = [System.Drawing.Bitmap]::new(150, 150)
$bitmap.Save((Join-Path $layout 'logo.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$bitmap.Dispose()

$tools = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Include makeappx.exe, signtool.exe |
    Where-Object FullName -match '\\x64\\'
$makeAppx = $tools | Where-Object Name -eq 'makeappx.exe' | Select-Object -First 1 -ExpandProperty FullName
$signTool = $tools | Where-Object Name -eq 'signtool.exe' | Select-Object -First 1 -ExpandProperty FullName

$msix = Join-Path $obj 'probe.msix'
& $makeAppx pack /o /d $layout /p $msix /nv | Out-Null
if ($LASTEXITCODE -ne 0) { throw "makeappx failed with $LASTEXITCODE" }
& $signTool sign /q /fd SHA256 /s My /sha1 $certificate.Thumbprint $msix
if ($LASTEXITCODE -ne 0) { throw "signtool failed with $LASTEXITCODE" }

Add-AppxPackage -Path $msix -ExternalLocation $output
Get-AppxPackage -Name $name | Select-Object Name, PackageFullName, SignatureKind

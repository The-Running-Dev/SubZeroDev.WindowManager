# Registers (or with -Unregister removes) the notifications worker's sparse package for the current user. It packs
# AppxManifest.xml and logo.png into subzerodev.notifications.msix beside this script, signs it with a CN=SubZeroDev
# certificate from the user's own store (created when missing) and writes that certificate's public half to
# SubZeroDev.cer here. The script never imports the certificate and never changes trust, notification access or the
# notifications switch: those are the user's to do (I32). It prints the one command that trusts the certificate.
# Run it from the plugin's folder: <host>\content\plugins\subzerodev.notifications\package.
param(
    [switch]$Unregister
)
$ErrorActionPreference = 'Stop'

$name = 'SubZeroDev.WindowManager.Notifications'
$existing = Get-AppxPackage -Name $name
if ($existing) { Remove-AppxPackage -Package $existing.PackageFullName }
if ($Unregister) { return }

$package = $PSScriptRoot
$plugin = Split-Path $package -Parent
if (-not (Test-Path (Join-Path $plugin 'SubZeroDev.WindowManager.Plugins.Notifications.exe'))) {
    throw "The worker is not beside this package: $plugin"
}

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

$publicCertificate = Join-Path $package 'SubZeroDev.cer'
Export-Certificate -Cert $certificate -FilePath $publicCertificate | Out-Null
if (-not (Get-ChildItem Cert:\LocalMachine\TrustedPeople | Where-Object Thumbprint -eq $certificate.Thumbprint)) {
    Write-Host 'The machine does not trust the signing certificate yet. In an administrator PowerShell, run once:'
    Write-Host "  Import-Certificate -FilePath '$publicCertificate' -CertStoreLocation Cert:\LocalMachine\TrustedPeople"
    exit 2
}

$layout = Join-Path ([IO.Path]::GetTempPath()) ([guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $layout | Out-Null
try {
    Copy-Item (Join-Path $package 'AppxManifest.xml') $layout
    Copy-Item (Join-Path $package 'logo.png') $layout

    $tools = Get-ChildItem "$env:USERPROFILE\.nuget\packages\microsoft.windows.sdk.buildtools" -Recurse -Include makeappx.exe, signtool.exe -ErrorAction SilentlyContinue |
        Where-Object FullName -match '\\x64\\'
    $makeAppx = $tools | Where-Object Name -eq 'makeappx.exe' | Select-Object -First 1 -ExpandProperty FullName
    $signTool = $tools | Where-Object Name -eq 'signtool.exe' | Select-Object -First 1 -ExpandProperty FullName
    if (-not $makeAppx -or -not $signTool) { throw 'makeappx.exe and signtool.exe come with the Windows SDK build tools package (restore the solution once).' }

    $msix = Join-Path $package 'subzerodev.notifications.msix'
    & $makeAppx pack /o /d $layout /p $msix /nv | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "makeappx failed with $LASTEXITCODE" }
    & $signTool sign /q /fd SHA256 /s My /sha1 $certificate.Thumbprint $msix
    if ($LASTEXITCODE -ne 0) { throw "signtool failed with $LASTEXITCODE" }

    Add-AppxPackage -Path $msix -ExternalLocation $plugin
    Get-AppxPackage -Name $name | Select-Object Name, PackageFullName, SignatureKind
}
finally {
    Remove-Item $layout -Recurse -Force
}

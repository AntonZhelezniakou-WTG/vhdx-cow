#Requires -RunAsAdministrator
<#
.SYNOPSIS
    One-time setup of a self-signed code-signing certificate for development MSIs.

.DESCRIPTION
    Creates a self-signed code-signing cert, exports it to installer/dev-cert.pfx
    (gitignored), and imports it into the LocalMachine Trusted Root and
    Trusted Publisher stores. After this, every MSI build picks up the PFX
    automatically (see SignMsi target in installer/VhdxCow.Installer.wixproj),
    and Windows UAC dialogs show "Verified publisher: WiseTechGlobal" + the
    product name instead of "Unknown publisher".

    The cert is only trusted on machines where it was imported. To enable
    other dev machines, either run this script there (it will reuse the
    existing PFX if found) or distribute the .cer (public key) and import
    via Trust-DevCert.ps1.

.NOTES
    The PFX password is hardcoded ('vhdx-cow-dev') because this is a dev-only
    cert. For production, replace dev-cert.pfx with a real CA-issued cert and
    override the DevCertPassword MSBuild property at build time.
#>

[CmdletBinding()]
param(
    [string]$Subject = 'CN=VHDX Copy-on-Write manager (Development), O=WiseTechGlobal, OU=Personal',
    [string]$PfxPath = (Join-Path $PSScriptRoot '..\installer\dev-cert.pfx'),
    [string]$PfxPassword = 'vhdx-cow-dev',
    [int]$ValidYears = 3
)

$ErrorActionPreference = 'Stop'

$PfxPath = [System.IO.Path]::GetFullPath($PfxPath)
$pfxDir = Split-Path -Path $PfxPath -Parent
if (-not (Test-Path $pfxDir)) {
    New-Item -ItemType Directory -Path $pfxDir -Force | Out-Null
}

$securePassword = ConvertTo-SecureString $PfxPassword -AsPlainText -Force

Write-Host "Creating self-signed code-signing certificate..." -ForegroundColor Cyan
Write-Host "  Subject:    $Subject"
Write-Host "  Valid for:  $ValidYears years"

$cert = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $Subject `
    -CertStoreLocation Cert:\CurrentUser\My `
    -KeyExportPolicy Exportable `
    -KeyLength 2048 `
    -KeyUsage DigitalSignature `
    -KeyAlgorithm RSA `
    -HashAlgorithm SHA256 `
    -NotAfter ((Get-Date).AddYears($ValidYears))

Write-Host "Exporting PFX (private key) to: $PfxPath" -ForegroundColor Cyan
Export-PfxCertificate -Cert $cert -FilePath $PfxPath -Password $securePassword | Out-Null

$cerTemp = Join-Path $env:TEMP 'vhdxcow-dev-cert.cer'
Export-Certificate -Cert $cert -FilePath $cerTemp -Force | Out-Null

Write-Host "Trusting cert on this machine (LocalMachine\Root + LocalMachine\TrustedPublisher)..." -ForegroundColor Cyan
Import-Certificate -FilePath $cerTemp -CertStoreLocation Cert:\LocalMachine\Root          | Out-Null
Import-Certificate -FilePath $cerTemp -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null
Remove-Item $cerTemp -Force

Write-Host ""
Write-Host "Done. Subsequent MSI builds will be signed automatically." -ForegroundColor Green
Write-Host ""
Write-Host "Cert details:"
Write-Host "  Thumbprint:  $($cert.Thumbprint)"
Write-Host "  PFX path:    $PfxPath"
Write-Host "  PFX password: $PfxPassword (dev-only, hardcoded)"
Write-Host ""
Write-Host "After rebuilding, UAC will show:"
Write-Host '  "Verified publisher: WiseTechGlobal"'
Write-Host '  "Program: VHDX Copy-on-Write manager"'

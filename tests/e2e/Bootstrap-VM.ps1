<#
.SYNOPSIS
  One-shot script that creates the VhdxManager E2E test VM on a Hyper-V host.

.DESCRIPTION
  Run this once on a developer workstation that has Hyper-V enabled. The
  script will:

    1. Verify prerequisites (admin elevation, Hyper-V installed, vTPM
       available — Windows 11 install requires Secure Boot + TPM).
    2. Resolve the Win11 Enterprise Eval ISO (asks user; offers download URL).
    3. Resolve the VM root directory (C:\HyperV if it exists, otherwise a
       folder-picker dialog).
    4. Generate a random local-admin password and persist it as
       tests\e2e\.vm-creds.json (gitignored).
    5. Materialise autounattend.xml from the template (substitutes the password)
       and pack it together with FirstLogon.ps1 into autounattend.iso.
    6. Create the VM (Gen2, 2 vCPU, 4GB dynamic RAM, 32GB OS VHDX, vTPM,
       Secure Boot, no networking, both ISOs attached).
    7. Start it. Block until the guest writes C:\Setup\boot-complete.flag.
    8. Shut down cleanly, detach the install ISO, take checkpoint
       'pre-install-clean'.

  After this script returns successfully you have a Hyper-V VM ready for
  test fixtures to install the MSI against.

.PARAMETER VmName
  Hyper-V VM display name. Default: VhdxManagerE2E.

.PARAMETER IsoPath
  Absolute path to the Windows 11 Enterprise Eval ISO. If omitted in
  interactive mode the script prompts and shows a file picker. Required
  when -Silent is set.

.PARAMETER VmRoot
  Absolute path to the parent directory that will hold the VM's disk and
  config files. Used verbatim — no namespace appending. If omitted: falls
  back to C:\HyperV\VhdxManagerE2E when C:\HyperV exists, otherwise opens a
  folder picker (or fails in -Silent mode).

.PARAMETER Silent
  Non-interactive mode for CI/automation. Disables every Read-Host /
  dialog / browser prompt. Required values must be supplied via -IsoPath
  and -VmRoot; missing values cause the script to fail fast with a clear
  message. Self-elevation is refused (caller must already be elevated).

.PARAMETER Force
  Overwrite an existing VM with the same name (deletes its files). Without
  this flag, the script aborts if the VM already exists.

.EXAMPLE
  PS> .\Bootstrap-VM.ps1
  Interactive run from any PowerShell. Self-elevates via UAC, prompts for
  ISO and VM location.

.EXAMPLE
  PS> .\Bootstrap-VM.ps1 -Silent -IsoPath D:\ISOs\Win11_Eval.iso `
  >>     -VmRoot D:\TestVMs\VhdxManagerE2E -Force
  Fully scripted run for CI. Must be invoked from an already-elevated PS.

.NOTES
  Run from any PowerShell (PS5.1 or PS7+). In interactive mode the script
  self-elevates via UAC if the current session isn't already running as
  Administrator. In -Silent mode the caller must pre-elevate.
  Tested on Windows 11 24H2 with Hyper-V.
#>

[CmdletBinding()]
param(
	[string] $VmName = 'VhdxManagerE2E',
	[string] $IsoPath,
	[string] $VmRoot,
	[switch] $Silent,
	[switch] $Force
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ----------------------------------------------------------------------------
# Setup
# ----------------------------------------------------------------------------

$ScriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Definition
. (Join-Path $ScriptRoot 'lib\Helpers.ps1')

# Bail out clearly on older PowerShell hosts before we touch anything that
# might fail with a less helpful error. PS 5.1 ships with Windows 10 1607+
# and Windows 11 — the practical floor for our Hyper-V usage.
Assert-PowerShellVersion -MinimumVersion '5.1'

# Self-elevate if launched from a normal PowerShell. The new elevated process
# replays our exact arguments (including -Silent / -IsoPath / -VmRoot) and
# -NoExit keeps its window open so the user can read whatever happens. In
# -Silent mode self-elevation is refused — the caller must pre-elevate.
Request-Elevation -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters -Silent:$Silent

Test-HyperVPrereqs

$TemplatesDir = Join-Path $ScriptRoot 'templates'
$AutounattendTemplate = Join-Path $TemplatesDir 'autounattend.xml.template'
$FirstLogonScript = Join-Path $TemplatesDir 'FirstLogon.ps1'
$CredsPath = Join-Path $ScriptRoot '.vm-creds.json'

foreach ($p in @($AutounattendTemplate, $FirstLogonScript)) {
	if (-not (Test-Path -LiteralPath $p)) {
		throw "Template missing: $p"
	}
}

# ----------------------------------------------------------------------------
# 1) Resolve inputs
# ----------------------------------------------------------------------------

$IsoPath = Resolve-Iso -ProvidedPath $IsoPath -Silent:$Silent
if (-not $IsoPath) {
	# Interactive run, user answered "no" or cancelled the picker. Resolve-Iso
	# already printed the download URL. Nothing to retry/repair — exit cleanly.
	Write-Host "Re-run this script after downloading the ISO." -ForegroundColor Yellow
	exit 0
}
Write-Host "Using install ISO: $IsoPath" -ForegroundColor Green

$VmRoot = Resolve-VmRoot -ProvidedRoot $VmRoot -Silent:$Silent
$VmDir = Join-Path $VmRoot $VmName
$VhdxPath = Join-Path $VmDir 'os.vhdx'
$AutounattendIsoPath = Join-Path $VmDir 'autounattend.iso'
$StagingDir = Join-Path $VmDir 'autounattend-staging'

# ----------------------------------------------------------------------------
# 2) Existing-VM guard
# ----------------------------------------------------------------------------

$existing = Get-VM -Name $VmName -ErrorAction SilentlyContinue
if ($existing) {
	if (-not $Force) {
		throw "VM '$VmName' already exists. Use -Force to remove it and start over."
	}
	Write-Host "Removing existing VM '$VmName'..." -ForegroundColor Yellow
	if ($existing.State -ne 'Off') {
		Stop-VM -Name $VmName -TurnOff -Force -ErrorAction SilentlyContinue
	}
	Remove-VM -Name $VmName -Force
}
if ($Force -and (Test-Path -LiteralPath $VmDir)) {
	Write-Host "Removing $VmDir..." -ForegroundColor Yellow
	Remove-Item -LiteralPath $VmDir -Recurse -Force
}
New-Item -ItemType Directory -Path $VmDir -Force | Out-Null

# ----------------------------------------------------------------------------
# 3) Credentials
# ----------------------------------------------------------------------------

$User = 'vhdxtest'
$Password = New-RandomPassword
$creds = [pscustomobject]@{
	VmName       = $VmName
	Username     = $User
	Password     = $Password
	GeneratedAt  = (Get-Date).ToString('o')
	VmRoot       = $VmRoot
	VmDir        = $VmDir
	IsoPath      = $IsoPath
}
$creds | ConvertTo-Json | Out-File -LiteralPath $CredsPath -Encoding utf8 -Force
Write-Host "Saved credentials → $CredsPath (gitignored)" -ForegroundColor Green

$secure = ConvertTo-SecureString $Password -AsPlainText -Force
$Credential = New-Object System.Management.Automation.PSCredential("$VmName\$User", $secure)

# ----------------------------------------------------------------------------
# 4) Build autounattend.iso
# ----------------------------------------------------------------------------

Write-Host "Materialising autounattend.xml..." -ForegroundColor Cyan
if (Test-Path -LiteralPath $StagingDir) { Remove-Item -LiteralPath $StagingDir -Recurse -Force }
New-Item -ItemType Directory -Path $StagingDir -Force | Out-Null

$xml = Get-Content -LiteralPath $AutounattendTemplate -Raw
# {IMAGE_NAME} → the WIM image inside the Eval ISO. Microsoft's Eval ISO
# typically ships 'Windows 11 Enterprise'. If your ISO is different, run
#   Dism /Get-WimInfo /WimFile:<mounted ISO>\sources\install.wim
# and override here.
$xml = $xml.Replace('{IMAGE_NAME}', 'Windows 11 Enterprise')
$xml = $xml.Replace('{PASSWORD}', $Password)
$xml | Out-File -LiteralPath (Join-Path $StagingDir 'autounattend.xml') -Encoding utf8 -Force

Copy-Item -LiteralPath $FirstLogonScript -Destination (Join-Path $StagingDir 'FirstLogon.ps1') -Force

Write-Host "Packing autounattend.iso..." -ForegroundColor Cyan
New-IsoFromFolder -SourceFolder $StagingDir -IsoPath $AutounattendIsoPath -VolumeLabel 'UNATTEND'
Write-Host "  → $AutounattendIsoPath" -ForegroundColor Green

# ----------------------------------------------------------------------------
# 5) Create the VM
# ----------------------------------------------------------------------------

Write-Host "Creating VM '$VmName'..." -ForegroundColor Cyan
# 32 GB dynamic OS disk — Win11 needs ~25GB after install; dynamic so we only
# pay for what's actually used.
New-VHD -Path $VhdxPath -SizeBytes 32GB -Dynamic | Out-Null

$vm = New-VM `
	-Name $VmName `
	-Generation 2 `
	-MemoryStartupBytes 4GB `
	-VHDPath $VhdxPath `
	-Path $VmRoot `
	-NoVMCheckpoint
# Note: -SwitchName intentionally omitted → no virtual network adapter.
# All host↔guest traffic flows over VMBus via PowerShell Direct.

Set-VM -Name $VmName `
	-ProcessorCount 2 `
	-DynamicMemory `
	-MemoryMinimumBytes 1GB `
	-MemoryMaximumBytes 4GB `
	-AutomaticCheckpointsEnabled $false `
	-CheckpointType Standard `
	-AutomaticStartAction Nothing `
	-AutomaticStopAction TurnOff

# Win11 requires Secure Boot + vTPM. Install the Microsoft UEFI cert + key
# protector for the vTPM.
Set-VMFirmware -VMName $VmName -EnableSecureBoot On -SecureBootTemplate 'MicrosoftWindows'
$keyProtector = New-HgsKeyProtector -Owner (Get-HgsGuardian -Name 'UntrustedGuardian' -ErrorAction SilentlyContinue) `
	-AllowUntrustedRoot -ErrorAction SilentlyContinue
if (-not $keyProtector) {
	# First-time setup: create the local "UntrustedGuardian" used for vTPM.
	if (-not (Get-HgsGuardian -Name 'UntrustedGuardian' -ErrorAction SilentlyContinue)) {
		New-HgsGuardian -Name 'UntrustedGuardian' -GenerateCertificates | Out-Null
	}
	$keyProtector = New-HgsKeyProtector -Owner (Get-HgsGuardian -Name 'UntrustedGuardian') -AllowUntrustedRoot
}
Set-VMKeyProtector -VMName $VmName -KeyProtector $keyProtector.RawData
Enable-VMTPM -VMName $VmName

# Attach both DVD drives:
#   - Slot 0: Windows install ISO
#   - Slot 1: autounattend ISO
Add-VMDvdDrive -VMName $VmName -Path $IsoPath
Add-VMDvdDrive -VMName $VmName -Path $AutounattendIsoPath

# Boot order: first DVD (the Win11 ISO) before the empty hard drive.
$winDvd = (Get-VMDvdDrive -VMName $VmName | Where-Object { $_.Path -eq $IsoPath })[0]
$osDrv  = Get-VMHardDiskDrive -VMName $VmName
Set-VMFirmware -VMName $VmName -BootOrder $winDvd, $osDrv

Write-Host "VM created." -ForegroundColor Green

# ----------------------------------------------------------------------------
# 6) Boot and wait
# ----------------------------------------------------------------------------

Write-Host "Starting VM (Windows install begins now; expect 8-15 minutes)..." -ForegroundColor Cyan
Start-VM -Name $VmName

try {
	Wait-VmReady -VmName $VmName -Credential $Credential -TimeoutMinutes 45
}
catch {
	Write-Error "First boot did not complete: $($_.Exception.Message)"
	Write-Host ""
	Write-Host "Open Hyper-V Manager → connect to '$VmName' to inspect manually." -ForegroundColor Yellow
	Write-Host "Common causes:" -ForegroundColor Yellow
	Write-Host "  - Wrong image name in autounattend.xml (mismatch with ISO contents)."
	Write-Host "  - VM not booting from DVD (boot order)."
	Write-Host "  - autounattend.iso missing FirstLogon.ps1 (rerun with -Force)."
	throw
}

# ----------------------------------------------------------------------------
# 7) Cleanup: detach install ISO, take checkpoint
# ----------------------------------------------------------------------------

Write-Host "Shutting down VM..." -ForegroundColor Cyan
Invoke-InGuest -VmName $VmName -Credential $Credential -ScriptBlock {
	Stop-Computer -Force
} | Out-Null
# Wait for it to actually power off.
$timeout = (Get-Date).AddMinutes(3)
while ((Get-VM -Name $VmName).State -ne 'Off') {
	if ((Get-Date) -gt $timeout) {
		Stop-VM -Name $VmName -TurnOff -Force
		break
	}
	Start-Sleep -Seconds 2
}

Write-Host "Detaching install media..." -ForegroundColor Cyan
# Detach the Win11 ISO (we don't need it again). Keep autounattend.iso so
# C:\Setup\FirstLogon.ps1 + log are accessible if we ever -Force re-bootstrap
# without redownloading the OS ISO.
$winDvd = Get-VMDvdDrive -VMName $VmName | Where-Object { $_.Path -eq $IsoPath }
if ($winDvd) { Remove-VMDvdDrive -VMObject $winDvd }
$autoDvd = Get-VMDvdDrive -VMName $VmName | Where-Object { $_.Path -eq $AutounattendIsoPath }
if ($autoDvd) { Set-VMDvdDrive -VMName $VmName -ControllerNumber $autoDvd.ControllerNumber `
	-ControllerLocation $autoDvd.ControllerLocation -Path $null }

Write-Host "Creating checkpoint 'pre-install-clean'..." -ForegroundColor Cyan
Checkpoint-VM -Name $VmName -SnapshotName 'pre-install-clean'

# ----------------------------------------------------------------------------
# 8) Done
# ----------------------------------------------------------------------------

Write-Host ""
Write-Host "================================================================" -ForegroundColor Green
Write-Host " VhdxManager E2E test VM is ready." -ForegroundColor Green
Write-Host "================================================================" -ForegroundColor Green
Write-Host "  VM name        : $VmName"
Write-Host "  Disk           : $VhdxPath"
Write-Host "  Credentials    : $CredsPath"
Write-Host "  Checkpoint     : pre-install-clean"
Write-Host ""
Write-Host "Next steps:"
Write-Host "  1. The test runner will copy the MSI into the guest, install it,"
Write-Host "     and create an 'installed-clean' checkpoint."
Write-Host "  2. To start the VM manually for inspection:"
Write-Host "       Start-VM -Name $VmName"
Write-Host "       vmconnect.exe localhost $VmName"
Write-Host "  3. To restore the clean checkpoint:"
Write-Host "       Restore-VMSnapshot -VMName $VmName -Name pre-install-clean -Confirm:`$false"
Write-Host ""

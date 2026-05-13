<#
.SYNOPSIS
  One-shot script that creates the VhdxManager E2E test VM on a Hyper-V host.

.DESCRIPTION
  Run this once on a developer workstation that has Hyper-V enabled. The
  script will:

    1. Verify prerequisites (admin elevation, Hyper-V installed).
    2. Resolve the Win11 Eval ISO (asks user; offers download URL if missing).
    3. Resolve the VM root directory (C:\Hyper-V if it exists, otherwise a
       folder-picker dialog).
    4. Generate a random local-admin password and persist it as
       tests\e2e\.vm-creds.json (gitignored).
    5. Materialise autounattend.xml from the template (substitutes the
       password) and pack it together with FirstLogon.ps1 into
       autounattend.iso.
    6. Create a Gen1 (BIOS) VM with the install ISO + autounattend ISO
       attached. Win11 hardware checks (TPM 2.0, Secure Boot, CPU/RAM/
       storage) are bypassed via registry tweaks injected during WinPE.
    7. Start it. Block until the guest writes C:\Setup\boot-complete.flag.
    8. Shut down cleanly, detach the install ISO, take checkpoint
       'pre-install-clean'.

  After this script returns successfully you have a Hyper-V VM ready for
  test fixtures to install the MSI against.

  Why Gen1 and not Gen2:
    Gen2 + UEFI Secure Boot rejects the bootloader on some Windows ISOs
    (notably LTSC Eval ISOs after Microsoft's August 2024 DBX update
    revoked older Windows bootloaders for the BlackLotus mitigation),
    producing a "boot loader failed" / "signed image's hash is not allowed
    (DB)" error. Disabling Secure Boot on Gen2 doesn't fully fix it
    because the bootloaders themselves expect a signed boot environment.
    Gen1 is BIOS-only — there's no signature check at all, so any ISO
    that happens to be UEFI-only-troublesome boots cleanly. The trade-off
    (no vTPM, no Secure Boot inside the VM) is irrelevant to the MSI
    under test.

.PARAMETER VmName
  Hyper-V VM display name. Default: VhdxManagerE2E.

.PARAMETER IsoPath
  Absolute path to the Windows 11 Eval ISO. If omitted in interactive mode
  the script shows a file picker. Required when -Silent is set.

.PARAMETER VmRoot
  Absolute path to the parent directory under which the VM subdirectory is
  created. The VM's files land in <VmRoot>\<VmName> (e.g. passing
  C:\Hyper-V yields C:\Hyper-V\VhdxManagerE2E). If omitted: falls back to
  C:\Hyper-V when it exists, otherwise opens a folder picker (or fails in
  -Silent mode).

.PARAMETER ImageName
  WIM image name inside the ISO (the value passed to <ImageInstall> in
  autounattend.xml). When omitted, the script mounts the ISO briefly,
  enumerates available images, and:
    * uses the only image if there's exactly one,
    * otherwise auto-picks a common Eval/Enterprise SKU
      ('Windows 11 Enterprise', 'Windows 11 Enterprise LTSC Evaluation', …),
    * otherwise prompts the user to pick (interactive) or fails with a
      list (silent mode).
  Pass this only if you need to override the auto-selection (e.g. install
  Pro instead of Enterprise on an ISO that has both).

.PARAMETER Silent
  Non-interactive mode for CI/automation. Disables every Read-Host /
  dialog / browser prompt. Required values must be supplied via -IsoPath
  and -VmRoot; missing values cause the script to fail fast with a clear
  message. Self-elevation is refused (caller must already be elevated).

.PARAMETER Force
  Skip confirmation and delete an existing VM (and any stale files in its
  directory) without prompting. In interactive mode the script otherwise
  asks "Remove and recreate?" before destroying anything; in -Silent mode
  -Force is REQUIRED to overwrite (CI must explicitly opt in to data loss).

.EXAMPLE
  PS> .\Bootstrap-VM.ps1
  Interactive run from any PowerShell. Self-elevates via UAC, prompts for
  ISO and VM location.

.EXAMPLE
  PS> .\Bootstrap-VM.ps1 -Silent -IsoPath D:\ISOs\Win11_Eval.iso `
  >>     -VmRoot D:\TestVMs -Force
  Fully scripted run for CI. Must be invoked from an already-elevated PS.

.EXAMPLE
  PS> .\Bootstrap-VM.ps1 -IsoPath D:\ISOs\Win11_LTSC_Eval.iso `
  >>     -ImageName 'Windows 11 Enterprise LTSC Evaluation'
  Use when the ISO is the LTSC Evaluation edition (image name differs from
  the standard Enterprise Eval ISO).

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
	[string] $ImageName,
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
$SetupCompleteScript = Join-Path $TemplatesDir 'SetupComplete.cmd.template'
$CredsPath = Join-Path $ScriptRoot '.vm-creds.json'

foreach ($p in @($AutounattendTemplate, $FirstLogonScript, $SetupCompleteScript)) {
	if (-not (Test-Path -LiteralPath $p)) {
		throw "Template missing: $p"
	}
}

# ----------------------------------------------------------------------------
# 1) Resolve inputs
# ----------------------------------------------------------------------------

$IsoPath = Resolve-Iso -ProvidedPath $IsoPath -Silent:$Silent
if (-not $IsoPath) {
	# Interactive run, user cancelled the picker. Resolve-Iso already printed
	# the download URL. Nothing to retry/repair — exit cleanly.
	Write-Host "Re-run this script after downloading the ISO." -ForegroundColor Yellow
	exit 0
}
Write-Host "Using install ISO: $IsoPath" -ForegroundColor Green

# Resolve which Windows edition to install. Mounts the ISO briefly to read
# the WIM image list, then auto-picks (or prompts on ambiguity). Done up
# front so we fail fast on bad ISOs / unrecognised editions before doing
# the expensive VM setup.
$ImageName = Resolve-ImageName -IsoPath $IsoPath -RequestedName $ImageName -Silent:$Silent

$VmRoot = Resolve-VmRoot -ProvidedRoot $VmRoot -Silent:$Silent
$VmDir = Join-Path $VmRoot $VmName
$VhdxPath = Join-Path $VmDir 'os.vhdx'
$AutounattendIsoPath = Join-Path $VmDir 'autounattend.iso'
$StagingDir = Join-Path $VmDir 'autounattend-staging'

# ----------------------------------------------------------------------------
# 2) Existing-VM guard
# ----------------------------------------------------------------------------

$existing = Get-VM -Name $VmName -ErrorAction SilentlyContinue
$dirHasContent = (Test-Path -LiteralPath $VmDir) -and `
	(Get-ChildItem -LiteralPath $VmDir -Force -ErrorAction SilentlyContinue)

# If a previous run left state behind (registered VM and/or stale files in
# VmDir), confirm with the user before destroying it. -Force skips the
# prompt; -Silent without -Force is a hard error so CI never silently
# wipes someone's VM.
if (($existing -or $dirHasContent) -and -not $Force) {
	$what = if ($existing) {
		"VM '$VmName' already exists"
	} else {
		"VM directory '$VmDir' has stale files (no VM registered, but leftover content)"
	}
	if ($Silent) {
		throw "$what. Pass -Force to remove it (silent mode requires explicit confirmation)."
	}
	Write-Host ""
	Write-Host $what -ForegroundColor Yellow
	$answer = Read-Host "Remove and recreate? [y/N]"
	if ($answer -notmatch '^(y|yes)$') {
		Write-Host "Aborted." -ForegroundColor Yellow
		exit 0
	}
}

if ($existing) {
	Write-Host "Removing existing VM '$VmName'..." -ForegroundColor Yellow
	if ($existing.State -ne 'Off') {
		Stop-VM -Name $VmName -TurnOff -Force -ErrorAction SilentlyContinue
	}
	Remove-VM -Name $VmName -Force
}
if (Test-Path -LiteralPath $VmDir) {
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
$xml = $xml.Replace('{IMAGE_NAME}', $ImageName)
$xml = $xml.Replace('{PASSWORD}', $Password)
$xml | Out-File -LiteralPath (Join-Path $StagingDir 'autounattend.xml') -Encoding utf8 -Force

Copy-Item -LiteralPath $FirstLogonScript -Destination (Join-Path $StagingDir 'FirstLogon.ps1') -Force

# SetupComplete.cmd is also templated — we need to inject the test user's
# credentials so the cmd can create-or-reset the account itself, defending
# against Win11 25H2 OOBE quirks where <LocalAccount> from autounattend.xml
# can be silently ignored or applied with a different password.
$cmd = Get-Content -LiteralPath $SetupCompleteScript -Raw
$cmd = $cmd.Replace('{USERNAME}', $User)
$cmd = $cmd.Replace('{PASSWORD}', $Password)
# `cmd.exe` requires CRLF + ANSI/UTF-8-no-BOM. PowerShell 5.1's `Out-File -Encoding utf8`
# writes a BOM which `cmd /c` tolerates but mis-renders the first line ("´╗┐@echo off"),
# so explicitly use a no-BOM UTF-8 writer.
[System.IO.File]::WriteAllText(
	(Join-Path $StagingDir 'SetupComplete.cmd'),
	$cmd,
	(New-Object System.Text.UTF8Encoding($false)))

Write-Host "Packing autounattend.iso..." -ForegroundColor Cyan
New-IsoFromFolder -SourceFolder $StagingDir -IsoPath $AutounattendIsoPath -VolumeLabel 'UNATTEND'
Write-Host "  → $AutounattendIsoPath" -ForegroundColor Green

# ----------------------------------------------------------------------------
# 5) Create the VM (Generation 1 — BIOS, no Secure Boot, no vTPM)
# ----------------------------------------------------------------------------

Write-Host "Creating VM '$VmName' (Generation 1 / BIOS)..." -ForegroundColor Cyan
# 32 GB dynamic OS disk — Win11 needs ~25GB after install; dynamic so we only
# pay for what's actually used.
New-VHD -Path $VhdxPath -SizeBytes 32GB -Dynamic | Out-Null

$vm = New-VM `
	-Name $VmName `
	-Generation 1 `
	-MemoryStartupBytes 4GB `
	-VHDPath $VhdxPath `
	-Path $VmRoot
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

# Attach both DVD drives. Gen1 puts the boot VHD on IDE 0,0 by default;
# DVDs go to the next available IDE slots:
#   - First DVD (Win11 install ISO)  → IDE 0,1 or 1,0
#   - Second DVD (autounattend ISO)  → next slot
Add-VMDvdDrive -VMName $VmName -Path $IsoPath
Add-VMDvdDrive -VMName $VmName -Path $AutounattendIsoPath

# BIOS startup order (Gen1 only — Gen2 uses Set-VMFirmware -BootOrder).
# 'CD' covers DVD drives. The BIOS scans CDs in attachment order; the install
# ISO is attached first, so it gets tried first. The autounattend ISO isn't
# bootable (built without an El Torito boot record) so even if scanned it
# falls through cleanly.
Set-VMBios -VMName $VmName -StartupOrder ('CD', 'IDE', 'LegacyNetworkAdapter', 'Floppy')

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
	Write-Host "    Pass -ImageName <name> and re-run with -Force."
	Write-Host "  - VM didn't boot from DVD (BIOS startup order issue)."
	Write-Host "  - autounattend.iso missing FirstLogon.ps1 / SetupComplete.cmd (rerun with -Force)."
	Write-Host "  - 'Guest rejected the configured credentials': SetupComplete.cmd"
	Write-Host "    didn't run, or the test user wasn't created. Sign in to the VM"
	Write-Host "    interactively and inspect C:\Setup\SetupComplete.log."
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
# Detach the Win11 ISO (we don't need it again). Empty the autounattend slot
# too — keep the drive but clear the ISO so subsequent boots don't see it.
$winDvd = Get-VMDvdDrive -VMName $VmName | Where-Object { $_.Path -eq $IsoPath }
if ($winDvd) { Remove-VMDvdDrive -VMName $VmName -ControllerNumber $winDvd.ControllerNumber -ControllerLocation $winDvd.ControllerLocation }
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
Write-Host "  Generation     : 1 (BIOS, no vTPM, no Secure Boot)"
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

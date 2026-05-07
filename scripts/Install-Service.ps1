#Requires -RunAsAdministrator
<#
.SYNOPSIS
	Install, uninstall, or manage the VhdxCow Windows Service.

.PARAMETER Action
	The action to perform: Install, Uninstall, Start, Stop, Status.

.PARAMETER ServicePath
	Path to VhdxCow.Service.exe. Required for Install action.

.EXAMPLE
	.\Install-Service.ps1 -Action Install -ServicePath "C:\Services\VhdxCow.Service.exe"
	.\Install-Service.ps1 -Action Start
	.\Install-Service.ps1 -Action Status
	.\Install-Service.ps1 -Action Uninstall
#>
param(
	[Parameter(Mandatory)]
	[ValidateSet("Install", "Uninstall", "Start", "Stop", "Status")]
	[string]$Action,

	[Parameter()]
	[string]$ServicePath
)

$ServiceName = "VhdxCowService"
$DisplayName = "VHDX Copy-on-Write Service"
$Description = "Manages VHDX differencing disks for Copy-on-Write workflows."
$EventLogSource = "VhdxCow"

function Install-VhdxCowService {
	if (-not $ServicePath) {
		Write-Error "ServicePath is required for Install action"
		return
	}

	$resolvedPath = Resolve-Path $ServicePath -ErrorAction SilentlyContinue
	if (-not $resolvedPath) {
		Write-Error "Service executable not found: $ServicePath"
		return
	}

	# Check if service already exists
	$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
	if ($existing) {
		Write-Warning "Service '$ServiceName' already exists. Uninstall first."
		return
	}

	# Register Windows Event Log source
	if (-not [System.Diagnostics.EventLog]::SourceExists($EventLogSource)) {
		Write-Host "Registering event log source '$EventLogSource'..."
		New-EventLog -LogName Application -Source $EventLogSource
	}

	# Create the service
	Write-Host "Installing service '$ServiceName'..."
	sc.exe create $ServiceName `
		binPath= "`"$resolvedPath`"" `
		displayName= "$DisplayName" `
		start= auto `
		obj= "LocalSystem"

	if ($LASTEXITCODE -ne 0) {
		Write-Error "Failed to create service (exit code: $LASTEXITCODE)"
		return
	}

	# Set description
	sc.exe description $ServiceName "$Description"

	# Configure recovery: restart after 60s on first two failures
	sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000//0

	# Create ProgramData directory
	$dataDir = Join-Path $env:ProgramData "VhdxCow"
	if (-not (Test-Path $dataDir)) {
		New-Item -ItemType Directory -Path $dataDir -Force | Out-Null
		New-Item -ItemType Directory -Path (Join-Path $dataDir "logs") -Force | Out-Null
		Write-Host "Created data directory: $dataDir"
	}

	Write-Host "Service '$ServiceName' installed successfully." -ForegroundColor Green
	Write-Host "Start it with: .\Install-Service.ps1 -Action Start"
}

function Uninstall-VhdxCowService {
	$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
	if (-not $existing) {
		Write-Warning "Service '$ServiceName' is not installed"
		return
	}

	if ($existing.Status -eq "Running") {
		Write-Host "Stopping service..."
		Stop-Service -Name $ServiceName -Force
		Start-Sleep -Seconds 2
	}

	Write-Host "Removing service '$ServiceName'..."
	sc.exe delete $ServiceName

	if ($LASTEXITCODE -eq 0) {
		Write-Host "Service '$ServiceName' removed successfully." -ForegroundColor Green
	}
	else {
		Write-Error "Failed to remove service (exit code: $LASTEXITCODE)"
	}
}

function Start-VhdxCowService {
	$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
	if (-not $existing) {
		Write-Error "Service '$ServiceName' is not installed"
		return
	}

	if ($existing.Status -eq "Running") {
		Write-Host "Service is already running"
		return
	}

	Write-Host "Starting service '$ServiceName'..."
	Start-Service -Name $ServiceName
	Write-Host "Service started." -ForegroundColor Green
}

function Stop-VhdxCowService {
	$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
	if (-not $existing) {
		Write-Error "Service '$ServiceName' is not installed"
		return
	}

	if ($existing.Status -eq "Stopped") {
		Write-Host "Service is already stopped"
		return
	}

	Write-Host "Stopping service '$ServiceName'..."
	Stop-Service -Name $ServiceName -Force
	Write-Host "Service stopped." -ForegroundColor Green
}

function Get-VhdxCowServiceStatus {
	$existing = Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
	if (-not $existing) {
		Write-Host "Service '$ServiceName' is not installed" -ForegroundColor Yellow
		return
	}

	Write-Host "Service:      $ServiceName"
	Write-Host "Display name: $($existing.DisplayName)"
	Write-Host "Status:       $($existing.Status)"
	Write-Host "Start type:   $($existing.StartType)"

	$dataDir = Join-Path $env:ProgramData "VhdxCow"
	$stateFile = Join-Path $dataDir "state.json"
	if (Test-Path $stateFile) {
		$state = Get-Content $stateFile | ConvertFrom-Json
		Write-Host "Active mounts: $($state.Count)"
	}
	else {
		Write-Host "Active mounts: 0 (no state file)"
	}
}

switch ($Action) {
	"Install"   { Install-VhdxCowService }
	"Uninstall" { Uninstall-VhdxCowService }
	"Start"     { Start-VhdxCowService }
	"Stop"      { Stop-VhdxCowService }
	"Status"    { Get-VhdxCowServiceStatus }
}

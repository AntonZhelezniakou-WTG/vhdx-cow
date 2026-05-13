<#
.SYNOPSIS
  Runs once inside the freshly installed Win11 guest during SetupComplete.

.DESCRIPTION
  Configures the VM for headless E2E testing:
    * Disables Windows Update (so the VM can't reboot mid-test).
    * Disables sleep/hibernate (so a long test pause doesn't suspend the VM).
    * Disables Windows Search indexing service (cuts background CPU).
    * Enables PowerShell remoting (PowerShell Direct relies on the guest's
      WinRM listener, even though host↔guest traffic uses VMBus, not TCP).
    * Sets ExecutionPolicy=RemoteSigned (best-effort; LTSC builds with
      WDAC enforce policy via GPO and reject the change with a security
      error — that's fine, PS Direct doesn't need it).
    * Disables UAC consent prompts for the admin account so msiexec /quiet
      and other elevated invocations don't pop a dialog.
    * Drops C:\Setup\boot-complete.flag — the host polls this file via
      Invoke-Command -VMName to detect "guest is ready for tests".

  Each configuration step runs in its own try/catch via Try-Step. A failure
  in one (e.g. ExecutionPolicy on locked-down LTSC) does NOT abort the
  subsequent steps, and boot-complete.flag is written unconditionally at
  the end so the host poller unblocks. Per-step status is logged to
  C:\Setup\FirstLogon.log for post-mortem inspection.
#>

$LogPath = 'C:\Setup\FirstLogon.log'
$FlagPath = 'C:\Setup\boot-complete.flag'

function Log {
	param([string]$Message)
	$ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
	"$ts  $Message" | Out-File -FilePath $LogPath -Append -Encoding utf8
}

function Try-Step {
	param(
		[Parameter(Mandatory)] [string] $Name,
		[Parameter(Mandatory)] [scriptblock] $Action
	)
	try {
		Log "[ ] $Name"
		& $Action
		Log "[+] $Name"
	}
	catch {
		Log "[!] $Name FAILED: $($_.Exception.Message)"
	}
}

New-Item -Path 'C:\Setup' -ItemType Directory -Force | Out-Null
Log "FirstLogon started (PowerShell $($PSVersionTable.PSVersion))"

Try-Step "Disable Windows Update" {
	Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
	Set-Service  -Name wuauserv -StartupType Disabled -ErrorAction SilentlyContinue
	Stop-Service -Name UsoSvc   -Force -ErrorAction SilentlyContinue
	Set-Service  -Name UsoSvc   -StartupType Disabled -ErrorAction SilentlyContinue
}

Try-Step "Power: never sleep, never hibernate" {
	powercfg /change standby-timeout-ac 0
	powercfg /change standby-timeout-dc 0
	powercfg /change hibernate-timeout-ac 0
	powercfg /change hibernate-timeout-dc 0
	powercfg /change monitor-timeout-ac 0
	powercfg /change monitor-timeout-dc 0
	powercfg /hibernate off
}

Try-Step "Disable Windows Search indexer" {
	Stop-Service -Name WSearch -Force -ErrorAction SilentlyContinue
	Set-Service  -Name WSearch -StartupType Disabled -ErrorAction SilentlyContinue
}

Try-Step "Enable PSRemoting" {
	# PowerShell Direct uses the local WinRM listener on the guest side over
	# VMBus, but it does NOT need a TCP listener. We don't expose any.
	Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
}

Try-Step "Set ExecutionPolicy=RemoteSigned" {
	# Best-effort. On LTSC images with WDAC / GPO-enforced policy this throws
	# PSSecurityException ("Security error"). Not load-bearing for tests
	# because PS Direct runs script blocks in-process, not .ps1 files from
	# disk — Group Policy execution policy applies even if this call is
	# rejected, so we don't actually lose anything by skipping it.
	Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine -Force
}

Try-Step "Suppress UAC consent prompts" {
	# ConsentPromptBehaviorAdmin=0 means "elevate without prompting" for any
	# member of Administrators. Tests run as 'vhdxtest' which is admin.
	Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' `
		-Name 'ConsentPromptBehaviorAdmin' -Value 0 -Type DWord -Force
	Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' `
		-Name 'EnableLUA' -Value 1 -Type DWord -Force  # keep LUA, just no prompts
}

Try-Step "Disable Edge first-run experience" {
	$edgeKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
	if (-not (Test-Path $edgeKey)) { New-Item -Path $edgeKey -Force | Out-Null }
	Set-ItemProperty -Path $edgeKey -Name 'HideFirstRunExperience' -Value 1 -Type DWord -Force
}

# Always write the sentinel — even if some steps failed, the VM is in a
# usable-enough state that the host's test runner can take over and finish
# any remaining configuration via Invoke-Command. Failure-to-write here
# would leave the host stuck in Wait-VmReady forever.
try {
	'ok' | Out-File -FilePath $FlagPath -Encoding ascii -Force
	Log "boot-complete.flag written; FirstLogon done"
}
catch {
	Log "FATAL: could not write ${FlagPath}: $($_.Exception.Message)"
}

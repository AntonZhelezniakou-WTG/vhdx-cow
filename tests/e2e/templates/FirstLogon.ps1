<#
.SYNOPSIS
  Runs once inside the freshly installed Win11 guest, on first auto-logon.

.DESCRIPTION
  Configures the VM for headless E2E testing:
    * Disables Windows Update (so the VM can't reboot mid-test).
    * Disables sleep/hibernate (so a long test pause doesn't suspend the VM).
    * Disables Windows Search indexing service (cuts background CPU).
    * Enables PowerShell remoting (PowerShell Direct relies on the guest's
      WinRM listener, even though host↔guest traffic uses VMBus, not TCP).
    * Sets ExecutionPolicy=RemoteSigned so test scripts run without prompts.
    * Disables UAC consent prompts for the admin account so msiexec /quiet
      and other elevated invocations don't pop a dialog.
    * Drops C:\Setup\boot-complete.flag — the host polls this file via
      Invoke-Command -VMName to detect "guest is ready for tests".

  All errors are caught and logged to C:\Setup\FirstLogon.log so the host
  can diagnose post-mortem.
#>

$ErrorActionPreference = 'Continue'
$LogPath = 'C:\Setup\FirstLogon.log'

function Log {
	param([string]$Message)
	$ts = (Get-Date).ToString('yyyy-MM-dd HH:mm:ss')
	"$ts  $Message" | Out-File -FilePath $LogPath -Append -Encoding utf8
}

try {
	New-Item -Path 'C:\Setup' -ItemType Directory -Force | Out-Null
	Log "FirstLogon started"

	# ---- Disable Windows Update ----
	Log "Disabling Windows Update"
	Stop-Service -Name wuauserv -Force -ErrorAction SilentlyContinue
	Set-Service -Name wuauserv -StartupType Disabled -ErrorAction SilentlyContinue
	Stop-Service -Name UsoSvc -Force -ErrorAction SilentlyContinue
	Set-Service -Name UsoSvc -StartupType Disabled -ErrorAction SilentlyContinue

	# ---- Power: never sleep, never hibernate ----
	Log "Power settings → never sleep"
	powercfg /change standby-timeout-ac 0
	powercfg /change standby-timeout-dc 0
	powercfg /change hibernate-timeout-ac 0
	powercfg /change hibernate-timeout-dc 0
	powercfg /change monitor-timeout-ac 0
	powercfg /change monitor-timeout-dc 0
	powercfg /hibernate off

	# ---- Disable Search indexing (background CPU sink in tests) ----
	Log "Disabling Search indexer"
	Stop-Service -Name WSearch -Force -ErrorAction SilentlyContinue
	Set-Service -Name WSearch -StartupType Disabled -ErrorAction SilentlyContinue

	# ---- PSRemoting (required for Invoke-Command -VMName) ----
	Log "Enabling PSRemoting"
	Enable-PSRemoting -Force -SkipNetworkProfileCheck | Out-Null
	# PowerShell Direct uses the local WinRM listener on the guest side over
	# VMBus, but it does NOT need a TCP listener. We don't expose any.

	# ---- Execution policy ----
	Log "Setting ExecutionPolicy=RemoteSigned"
	Set-ExecutionPolicy -ExecutionPolicy RemoteSigned -Scope LocalMachine -Force

	# ---- Suppress UAC prompts for the admin account ----
	# ConsentPromptBehaviorAdmin=0 means "elevate without prompting" for any
	# member of Administrators. Tests run as 'vhdxtest' which is admin.
	Log "Disabling UAC consent prompts"
	Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' `
		-Name 'ConsentPromptBehaviorAdmin' -Value 0 -Type DWord -Force
	Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' `
		-Name 'EnableLUA' -Value 1 -Type DWord -Force  # keep LUA, just no prompts

	# ---- Disable Edge first-run experience ----
	Log "Disabling Edge first-run"
	$edgeKey = 'HKLM:\SOFTWARE\Policies\Microsoft\Edge'
	if (-not (Test-Path $edgeKey)) { New-Item -Path $edgeKey -Force | Out-Null }
	Set-ItemProperty -Path $edgeKey -Name 'HideFirstRunExperience' -Value 1 -Type DWord -Force

	# ---- Sentinel: host polls this file to know we're done ----
	Log "Marking boot-complete"
	'ok' | Out-File -FilePath 'C:\Setup\boot-complete.flag' -Encoding ascii -Force

	Log "FirstLogon completed successfully"
}
catch {
	Log "FirstLogon FAILED: $($_.Exception.Message)"
	Log $_.ScriptStackTrace
	# Don't throw — leave the VM in a state where the host can still log in
	# and inspect C:\Setup\FirstLogon.log.
}

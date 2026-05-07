#Requires -Version 5
<#
.SYNOPSIS
    Post-install diagnostic dialog for the VhdxCow MSI installer.

    Invoked by an immediate Custom Action in InstallUISequence after ExecuteAction
    completes. Detects whether the installation produced any visible problems —
    a stopped service, a fresh fatal report, recent Service Control Manager errors,
    .NET Runtime exceptions, etc. — and, if so, surfaces them in a WinForms dialog
    with Save/Copy/Open Folder buttons.

    Errors that occur while collecting the diagnostic sources themselves
    (insufficient privileges to read an event log, missing log file, etc.) are
    captured into the same report so the user always sees them — they are never
    silently swallowed.

    Exits silently when nothing notable is found, so it is safe to schedule
    unconditionally on every interactive install.
#>

$ErrorActionPreference = 'Stop'

$serviceName = 'VhdxCowService'
$logsDir = Join-Path $env:ProgramData 'VhdxCow\logs'
$cutoff = (Get-Date).AddMinutes(-5)

# Errors encountered while collecting diagnostic data. Surfaced to the user, not hidden.
$sourceErrors = New-Object System.Collections.Generic.List[object]

function Invoke-DiagSource {
	<#
		Runs a diagnostic-collection block. If it throws, records the error into
		$sourceErrors and returns $null. Get-WinEvent's "no events found" pseudo-
		error is treated as an empty result (it is not a real failure).
	#>
	param(
		[Parameter(Mandatory)][string]$Name,
		[Parameter(Mandatory)][scriptblock]$Block
	)
	try {
		return & $Block
	}
	catch {
		$err = $_
		if ($err.FullyQualifiedErrorId -like '*NoMatchingEventsFound*') {
			return @()  # Get-WinEvent: empty result, not an error
		}
		$script:sourceErrors.Add([pscustomobject]@{
			Source  = $Name
			Message = $err.Exception.Message
			Detail  = $err.Exception.ToString()
		})
		return $null
	}
}

function Show-FallbackError {
	param([string]$Title, [string]$Body)
	try {
		Add-Type -AssemblyName System.Windows.Forms | Out-Null
		[System.Windows.Forms.MessageBox]::Show($Body, $Title,
			[System.Windows.Forms.MessageBoxButtons]::OK,
			[System.Windows.Forms.MessageBoxIcon]::Error) | Out-Null
	}
	catch {
		# Truly nothing we can do — script ends, MSI proceeds.
	}
}

try {
	Add-Type -AssemblyName System.Windows.Forms, System.Drawing | Out-Null

	# ---------- 1) Service status ----------
	$svc = Invoke-DiagSource -Name 'Get-Service' -Block {
		Get-Service -Name $serviceName -ErrorAction Stop
	}

	# ---------- 2) Fresh fatal-report file from the service ----------
	$fatalLog = Invoke-DiagSource -Name 'Fatal-log scan' -Block {
		if (-not (Test-Path $logsDir)) { return $null }
		Get-ChildItem (Join-Path $logsDir 'fatal-*.log') -ErrorAction Stop |
			Where-Object { $_.LastWriteTime -gt $cutoff } |
			Sort-Object LastWriteTime -Descending |
			Select-Object -First 1
	}

	$fatalLogContent = $null
	if ($fatalLog) {
		$fatalLogContent = Invoke-DiagSource -Name "Fatal-log read ($($fatalLog.Name))" -Block {
			Get-Content $fatalLog.FullName -Raw -ErrorAction Stop
		}
	}

	# ---------- 3) Application Event Log (service entries, .NET Runtime, Application Error) ----------
	$appEvents = Invoke-DiagSource -Name 'Application Event Log' -Block {
		Get-WinEvent -FilterHashtable @{
			LogName   = 'Application'
			StartTime = $cutoff
			Level     = @(1, 2, 3)
		} -ErrorAction Stop | Where-Object {
			$_.ProviderName -eq 'VhdxCow' -or
			$_.ProviderName -eq '.NET Runtime' -or
			$_.ProviderName -eq 'Application Error' -or
			($_.ProviderName -eq 'Service Control Manager' -and $_.Message -match $serviceName)
		}
	}
	if ($null -eq $appEvents) { $appEvents = @() }

	# ---------- 4) System Event Log — Service Control Manager entries about the service ----------
	$sysEvents = Invoke-DiagSource -Name 'System Event Log (Service Control Manager)' -Block {
		Get-WinEvent -FilterHashtable @{
			LogName      = 'System'
			StartTime    = $cutoff
			ProviderName = 'Service Control Manager'
			Level        = @(1, 2, 3)
		} -ErrorAction Stop | Where-Object {
			$_.Message -match $serviceName -or $_.Message -match 'VhdxCow'
		}
	}
	if ($null -eq $sysEvents) { $sysEvents = @() }

	# ---------- 5) Decide whether anything is worth showing ----------
	$reasons = New-Object System.Collections.Generic.List[string]
	if ($null -eq $svc) {
		# Could be either "service not registered" or a Get-Service failure (already in $sourceErrors).
		# Differentiate by whether a Get-Service error was recorded.
		$svcErr = $sourceErrors | Where-Object { $_.Source -eq 'Get-Service' } | Select-Object -First 1
		if (-not $svcErr) {
			$reasons.Add("Service '$serviceName' is not registered. Installation may have failed before the service was created.")
		}
	}
	elseif ($svc.Status -ne 'Running') {
		$reasons.Add("Service '$serviceName' is registered but its current status is '$($svc.Status)'.")
	}
	if ($fatalLog) {
		$reasons.Add("A fatal report was written by the service: $($fatalLog.FullName)")
	}
	if ($appEvents.Count -gt 0) {
		$reasons.Add("$($appEvents.Count) related Application-log error/warning event(s) were recorded in the last 5 minutes.")
	}
	if ($sysEvents.Count -gt 0) {
		$reasons.Add("$($sysEvents.Count) Service Control Manager error/warning event(s) referencing the service were recorded in the last 5 minutes.")
	}
	if ($sourceErrors.Count -gt 0) {
		$reasons.Add("$($sourceErrors.Count) error(s) occurred while collecting diagnostic data — see the 'Diagnostic-collection errors' section below.")
	}

	if ($reasons.Count -eq 0) {
		# Installation looks fine and all sources were read successfully — nothing to show.
		return
	}

	# ---------- 6) Build the human-readable diagnostic report ----------
	$sb = New-Object System.Text.StringBuilder
	[void]$sb.AppendLine('VhdxCow installation diagnostic report')
	[void]$sb.AppendLine('========================================')
	[void]$sb.AppendLine("Generated:        $((Get-Date).ToString('o'))")
	[void]$sb.AppendLine("Computer:         $env:COMPUTERNAME")
	[void]$sb.AppendLine("User:             $env:USERDOMAIN\$env:USERNAME")
	[void]$sb.AppendLine("OS:               $([System.Environment]::OSVersion.VersionString)")
	[void]$sb.AppendLine()

	[void]$sb.AppendLine('---- Findings ----')
	foreach ($r in $reasons) { [void]$sb.AppendLine(" * $r") }
	[void]$sb.AppendLine()

	if ($sourceErrors.Count -gt 0) {
		[void]$sb.AppendLine('---- Diagnostic-collection errors ----')
		foreach ($err in $sourceErrors) {
			[void]$sb.AppendLine("[$($err.Source)]")
			[void]$sb.AppendLine($err.Message)
			[void]$sb.AppendLine('Details:')
			[void]$sb.AppendLine($err.Detail)
			[void]$sb.AppendLine('----')
		}
		[void]$sb.AppendLine()
	}

	[void]$sb.AppendLine('---- Service status ----')
	if ($svc) {
		[void]$sb.AppendLine("Name:        $($svc.Name)")
		[void]$sb.AppendLine("DisplayName: $($svc.DisplayName)")
		[void]$sb.AppendLine("Status:      $($svc.Status)")
		[void]$sb.AppendLine("StartType:   $($svc.StartType)")
	} else {
		[void]$sb.AppendLine('Service is not registered (or query failed — see collection errors above).')
	}
	[void]$sb.AppendLine()

	if ($fatalLogContent) {
		[void]$sb.AppendLine("---- Service fatal report ($($fatalLog.Name)) ----")
		[void]$sb.AppendLine($fatalLogContent)
		[void]$sb.AppendLine()
	}
	elseif ($fatalLog) {
		[void]$sb.AppendLine("---- Service fatal report ($($fatalLog.Name)) — could not be read, see collection errors ----")
		[void]$sb.AppendLine()
	}

	if ($appEvents.Count -gt 0) {
		[void]$sb.AppendLine('---- Application Event Log (last 5 minutes) ----')
		foreach ($ev in $appEvents) {
			[void]$sb.AppendLine("[$($ev.TimeCreated.ToString('o'))] $($ev.LevelDisplayName) | $($ev.ProviderName) | EventId=$($ev.Id)")
			[void]$sb.AppendLine($ev.Message)
			[void]$sb.AppendLine('----')
		}
		[void]$sb.AppendLine()
	}

	if ($sysEvents.Count -gt 0) {
		[void]$sb.AppendLine('---- System Event Log — Service Control Manager (last 5 minutes) ----')
		foreach ($ev in $sysEvents) {
			[void]$sb.AppendLine("[$($ev.TimeCreated.ToString('o'))] $($ev.LevelDisplayName) | EventId=$($ev.Id)")
			[void]$sb.AppendLine($ev.Message)
			[void]$sb.AppendLine('----')
		}
		[void]$sb.AppendLine()
	}

	[void]$sb.AppendLine('Please save this report and open an issue with the VhdxCow maintainers, attaching the saved file.')

	$reportText = $sb.ToString()

	# ---------- 7) Show the dialog ----------
	$form = New-Object System.Windows.Forms.Form
	$form.Text = 'VhdxCow installation — diagnostic report'
	$form.Size = New-Object System.Drawing.Size(960, 680)
	$form.StartPosition = 'CenterScreen'
	$form.MinimumSize = New-Object System.Drawing.Size(600, 400)
	$form.TopMost = $true

	$summary = New-Object System.Windows.Forms.Label
	$summary.Text = "VhdxCow installation completed with $($reasons.Count) issue(s) detected.`r`n" +
	                "Review the report below and use 'Save As...' to send it to the maintainers."
	$summary.Dock = 'Top'
	$summary.Height = 56
	$summary.Padding = New-Object System.Windows.Forms.Padding(12, 12, 12, 0)
	$summary.Font = New-Object System.Drawing.Font('Segoe UI', 10)
	$form.Controls.Add($summary)

	$tb = New-Object System.Windows.Forms.TextBox
	$tb.Multiline = $true
	$tb.ReadOnly = $true
	$tb.ScrollBars = 'Both'
	$tb.WordWrap = $false
	$tb.Dock = 'Fill'
	$tb.Font = New-Object System.Drawing.Font('Consolas', 9)
	$tb.Text = $reportText
	$form.Controls.Add($tb)

	$panel = New-Object System.Windows.Forms.FlowLayoutPanel
	$panel.Dock = 'Bottom'
	$panel.Height = 56
	$panel.FlowDirection = 'RightToLeft'
	$panel.Padding = New-Object System.Windows.Forms.Padding(8)

	$makeButton = {
		param($text, $width, $onClick)
		$b = New-Object System.Windows.Forms.Button
		$b.Text = $text
		$b.Width = $width
		$b.Height = 32
		$b.Margin = New-Object System.Windows.Forms.Padding(6, 4, 6, 4)
		$b.Add_Click($onClick)
		return $b
	}

	$btnClose = & $makeButton 'Close' 100 { $form.Close() }
	$btnFolder = & $makeButton 'Open Logs Folder' 150 {
		if (Test-Path $logsDir) { Start-Process explorer.exe $logsDir }
	}
	$btnCopy = & $makeButton 'Copy to Clipboard' 150 {
		[System.Windows.Forms.Clipboard]::SetText($tb.Text)
		[System.Windows.Forms.MessageBox]::Show('Report copied to clipboard.', 'Copied',
			[System.Windows.Forms.MessageBoxButtons]::OK,
			[System.Windows.Forms.MessageBoxIcon]::Information) | Out-Null
	}
	$btnSave = & $makeButton 'Save As...' 110 {
		$sfd = New-Object System.Windows.Forms.SaveFileDialog
		$sfd.FileName = "vhdx-cow-install-diag-$((Get-Date).ToString('yyyyMMdd-HHmmss')).log"
		$sfd.Filter = 'Log files (*.log)|*.log|All files (*.*)|*.*'
		$sfd.Title = 'Save VhdxCow installation diagnostic report'
		if ($sfd.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
			[System.IO.File]::WriteAllText($sfd.FileName, $tb.Text)
		}
	}

	$panel.Controls.AddRange(@($btnClose, $btnFolder, $btnCopy, $btnSave))
	$form.Controls.Add($panel)

	$form.AcceptButton = $btnSave
	$form.CancelButton = $btnClose

	[void]$form.ShowDialog()
}
catch {
	# UI construction or another unrecoverable error happened. We still want the user
	# to see SOMETHING — fall back to a plain MessageBox containing what we know,
	# including any source-collection errors collected so far.
	$msg = "VhdxCow installer diagnostic dialog failed to render.`r`n`r`n" +
	       "Error: $($_.Exception.Message)`r`n`r`n"
	if ($sourceErrors.Count -gt 0) {
		$msg += "Diagnostic-collection errors before the failure:`r`n"
		foreach ($err in $sourceErrors) {
			$msg += "[$($err.Source)] $($err.Message)`r`n"
		}
	}
	Show-FallbackError -Title 'VhdxCow installer diagnostic' -Body $msg
}

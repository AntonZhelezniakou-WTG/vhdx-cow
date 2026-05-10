<#
.SYNOPSIS
  Shared helpers for the VhdxManager E2E VM bootstrap and test runner scripts.

.DESCRIPTION
  Designed to be dot-sourced (`. .\lib\Helpers.ps1`). Provides:
    - Resolve-Iso             : ask user to point at a Win11 Eval ISO or
                                offer to download from Microsoft.
    - Resolve-VmRoot          : pick C:\HyperV if it exists, else folder dialog.
    - New-RandomPassword      : crypto-random local-admin password.
    - New-IsoFromFolder       : pack a folder into an ISO9660+Joliet image
                                using IMAPI2FS (no Windows ADK needed).
    - Wait-VmReady            : poll the guest via PowerShell Direct until the
                                FirstLogon sentinel exists.
    - Invoke-InGuest          : thin wrapper around Invoke-Command -VMName
                                with retry on transient WinRM transport errors.

  All functions assume PowerShell 5.1+ and Windows. They use no third-party
  modules.
#>

Set-StrictMode -Version Latest

# ────────────────────────────────────────────────────────────────────────────
# ISO resolution
# ────────────────────────────────────────────────────────────────────────────

# Microsoft Evaluation Center landing page for Windows 11 Enterprise. The
# direct ISO URL changes with each refresh of the eval cycle, so we point at
# the landing page and let the user grab the current download manually.
$Script:Win11EvalUrl = 'https://www.microsoft.com/en-us/evalcenter/download-windows-11-enterprise'

function Resolve-Iso {
	<#
	.SYNOPSIS
	  Prompts the user for a Windows 11 Enterprise Eval ISO. Returns its full path.
	.OUTPUTS
	  String — absolute path to a readable .iso file.
	#>
	[CmdletBinding()]
	param()

	Write-Host ""
	Write-Host "=== Windows 11 Enterprise Eval ISO ===" -ForegroundColor Cyan
	$answer = Read-Host "Do you already have the ISO downloaded? (y/n)"
	if ($answer -match '^(y|yes)$') {
		while ($true) {
			$path = Read-Host "Path to the .iso file"
			$path = $path.Trim('"', ' ')
			if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
				Write-Host "  Not found: $path" -ForegroundColor Yellow
				continue
			}
			if (-not $path.ToLowerInvariant().EndsWith('.iso')) {
				Write-Host "  Not an .iso file." -ForegroundColor Yellow
				continue
			}
			return (Resolve-Path -LiteralPath $path).Path
		}
	}

	Write-Host ""
	Write-Host "Download Windows 11 Enterprise (90-day evaluation) from:" -ForegroundColor Yellow
	Write-Host "  $Script:Win11EvalUrl" -ForegroundColor White
	Write-Host ""
	Write-Host "  - Pick the 'ISO - Enterprise (English, United States)' option."
	Write-Host "  - Save the file anywhere on this host."
	Write-Host "  - Re-run this script and answer 'y' on the prompt above."
	Write-Host ""
	throw "ISO not available. Re-run after downloading."
}

# ────────────────────────────────────────────────────────────────────────────
# VM-root resolution
# ────────────────────────────────────────────────────────────────────────────

function Resolve-VmRoot {
	<#
	.SYNOPSIS
	  Resolves the directory under which all VM artefacts (VHDX, ISOs, logs)
	  will live.

	.DESCRIPTION
	  If C:\HyperV exists, uses C:\HyperV\VhdxManagerE2E. Otherwise opens a
	  Windows folder-picker dialog and creates VhdxManagerE2E inside whatever
	  folder the user picks.
	#>
	[CmdletBinding()]
	param()

	$default = 'C:\HyperV'
	if (Test-Path -LiteralPath $default -PathType Container) {
		$root = Join-Path $default 'VhdxManagerE2E'
		Write-Host "Using VM root: $root" -ForegroundColor Green
		return $root
	}

	Write-Host "C:\HyperV does not exist. Please pick a parent directory for VM storage." -ForegroundColor Yellow
	Add-Type -AssemblyName System.Windows.Forms
	$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
	$dialog.Description = 'Pick a parent directory for VhdxManagerE2E (~40GB free space recommended)'
	$dialog.ShowNewFolderButton = $true
	$result = $dialog.ShowDialog()
	if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
		throw "VM-root selection cancelled."
	}
	$root = Join-Path $dialog.SelectedPath 'VhdxManagerE2E'
	Write-Host "Using VM root: $root" -ForegroundColor Green
	return $root
}

# ────────────────────────────────────────────────────────────────────────────
# Random password
# ────────────────────────────────────────────────────────────────────────────

function New-RandomPassword {
	<#
	.SYNOPSIS
	  Generates a cryptographically-random password meeting Windows complexity
	  policy (uppercase + lowercase + digit + symbol).

	.OUTPUTS
	  String of length 22.
	#>
	[CmdletBinding()]
	param()

	$bytes = New-Object byte[] 18
	[System.Security.Cryptography.RandomNumberGenerator]::Create().GetBytes($bytes)
	# Strip URL-unfriendly chars that also tend to confuse XML escaping.
	$base = [Convert]::ToBase64String($bytes) -replace '[+/=]', ''
	# Prefix guarantees the four character classes Windows wants.
	return ('Vh!' + $base.Substring(0, 19))
}

# ────────────────────────────────────────────────────────────────────────────
# ISO creation (IMAPI2FS — built into Windows, no ADK required)
# ────────────────────────────────────────────────────────────────────────────

# C# helper for streaming an IMAPI result image to disk. Compiled once per
# session via Add-Type and reused across invocations.
$Script:IsoFileTypeAdded = $false

function Initialize-IsoFileType {
	if ($Script:IsoFileTypeAdded) { return }
	# IMAPI2FS returns the image as an IStream; we copy it block-by-block to
	# a normal file. The /unsafe flag is needed because IStream::Read takes a
	# pointer to the bytes-read counter.
	$source = @"
using System;
using System.IO;
using System.Runtime.InteropServices.ComTypes;

public static class IsoFile {
	public static unsafe void Create(string path, object stream, int blockSize, int totalBlocks) {
		int bytes = 0;
		byte[] buf = new byte[blockSize];
		IntPtr ptr = (IntPtr)(&bytes);
		using (var output = File.OpenWrite(path)) {
			IStream input = (IStream)stream;
			while (totalBlocks-- > 0) {
				input.Read(buf, blockSize, ptr);
				output.Write(buf, 0, bytes);
			}
			output.Flush();
		}
	}
}
"@
	Add-Type -TypeDefinition $source -CompilerOptions '/unsafe' -Language CSharp
	$Script:IsoFileTypeAdded = $true
}

function New-IsoFromFolder {
	<#
	.SYNOPSIS
	  Packs the contents of a folder into an ISO image at the given path.

	.PARAMETER SourceFolder
	  Directory whose contents become the ISO root.
	.PARAMETER IsoPath
	  Destination .iso file. Overwritten if it exists.
	.PARAMETER VolumeLabel
	  ISO volume label (max 32 chars, will be uppercased).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string] $SourceFolder,
		[Parameter(Mandatory)] [string] $IsoPath,
		[Parameter(Mandatory)] [string] $VolumeLabel
	)

	if (-not (Test-Path -LiteralPath $SourceFolder -PathType Container)) {
		throw "Source folder not found: $SourceFolder"
	}
	Initialize-IsoFileType

	$image = New-Object -ComObject IMAPI2FS.MsftFileSystemImage
	# 1=ISO9660 + 2=Joliet → 3 (file names with mixed case work in Windows Setup).
	$image.FileSystemsToCreate = 3
	$image.VolumeName = $VolumeLabel.ToUpperInvariant()
	$image.Root.AddTreeWithNamedStreams((Resolve-Path -LiteralPath $SourceFolder).Path, $false)

	$result = $image.CreateResultImage()
	if (Test-Path -LiteralPath $IsoPath) { Remove-Item -LiteralPath $IsoPath -Force }
	[IsoFile]::Create($IsoPath, $result.ImageStream, $result.BlockSize, $result.TotalBlocks)

	# Release COM references explicitly so the file isn't held open.
	[System.Runtime.InteropServices.Marshal]::ReleaseComObject($result) | Out-Null
	[System.Runtime.InteropServices.Marshal]::ReleaseComObject($image) | Out-Null
}

# ────────────────────────────────────────────────────────────────────────────
# PowerShell Direct: invoke + wait
# ────────────────────────────────────────────────────────────────────────────

function Invoke-InGuest {
	<#
	.SYNOPSIS
	  Runs a script block in the guest VM via PowerShell Direct.

	.PARAMETER VmName
	  Hyper-V VM name.
	.PARAMETER Credential
	  Guest credentials (PSCredential).
	.PARAMETER ScriptBlock
	  Code to run. Use $using:VarName to capture host-side variables.
	.PARAMETER ArgumentList
	  Optional positional arguments forwarded to the script block.
	.PARAMETER MaxAttempts
	  Retry on transient transport errors (guest just booted, WinRM not ready).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string] $VmName,
		[Parameter(Mandatory)] [System.Management.Automation.PSCredential] $Credential,
		[Parameter(Mandatory)] [scriptblock] $ScriptBlock,
		[object[]] $ArgumentList,
		[int] $MaxAttempts = 1,
		[int] $RetryDelayMs = 2000
	)

	$attempt = 0
	while ($true) {
		$attempt++
		try {
			if ($null -ne $ArgumentList) {
				return Invoke-Command -VMName $VmName -Credential $Credential `
					-ScriptBlock $ScriptBlock -ArgumentList $ArgumentList -ErrorAction Stop
			} else {
				return Invoke-Command -VMName $VmName -Credential $Credential `
					-ScriptBlock $ScriptBlock -ErrorAction Stop
			}
		}
		catch {
			if ($attempt -ge $MaxAttempts) { throw }
			Start-Sleep -Milliseconds $RetryDelayMs
		}
	}
}

function Wait-VmReady {
	<#
	.SYNOPSIS
	  Blocks until the guest signals "first boot complete" by writing
	  C:\Setup\boot-complete.flag.

	.PARAMETER TimeoutMinutes
	  Maximum total wait. Win11 unattended install + first boot typically
	  takes 8-15 minutes on modern hardware.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string] $VmName,
		[Parameter(Mandatory)] [System.Management.Automation.PSCredential] $Credential,
		[int] $TimeoutMinutes = 30,
		[int] $PollIntervalSeconds = 15
	)

	$deadline = (Get-Date).AddMinutes($TimeoutMinutes)
	$poll = 0
	Write-Host "Waiting for guest first-boot to complete (up to $TimeoutMinutes min)..." -ForegroundColor Cyan
	while ((Get-Date) -lt $deadline) {
		$poll++
		try {
			$ready = Invoke-Command -VMName $VmName -Credential $Credential -ErrorAction Stop -ScriptBlock {
				Test-Path -LiteralPath 'C:\Setup\boot-complete.flag'
			}
			if ($ready) {
				Write-Host "  Guest is ready (poll #$poll)." -ForegroundColor Green
				return
			}
			Write-Host "  Poll #$poll`: guest reachable but flag not yet written." -ForegroundColor DarkGray
		}
		catch {
			# Expected during install / pre-OOBE / WinRM not yet up.
			Write-Host "  Poll #$poll`: $($_.Exception.Message.Split([char]10)[0])" -ForegroundColor DarkGray
		}
		Start-Sleep -Seconds $PollIntervalSeconds
	}
	throw "Guest did not become ready within $TimeoutMinutes minutes."
}

# ────────────────────────────────────────────────────────────────────────────
# Self-elevation
# ────────────────────────────────────────────────────────────────────────────

function Test-Elevated {
	<#
	.SYNOPSIS
	  Returns $true if the current PowerShell process is running as an
	  Administrator (elevated UAC token).
	#>
	[CmdletBinding()]
	param()
	$wi = [System.Security.Principal.WindowsIdentity]::GetCurrent()
	$wp = New-Object System.Security.Principal.WindowsPrincipal($wi)
	return $wp.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Request-Elevation {
	<#
	.SYNOPSIS
	  If the current process is not elevated, re-launches the calling script
	  via UAC and exits the current process. No-op when already elevated.

	.DESCRIPTION
	  Forwards all bound parameters to the new process so the user sees the
	  same behaviour they originally invoked. Uses the same PowerShell flavour
	  (powershell.exe for PS5.1, pwsh.exe for PS7+). Adds -NoExit so the
	  elevated window stays open and the user can read the output.

	  Caller pattern, near the top of the entry-point script:

	      . .\lib\Helpers.ps1
	      Request-Elevation -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters

	.PARAMETER ScriptPath
	  Full path to the script that should be re-launched. Pass $PSCommandPath
	  from the caller (resolves automatically to the script's own path).

	.PARAMETER BoundParameters
	  $PSBoundParameters from the caller. Each entry is forwarded as
	  "-Key Value" (or just "-Key" for switches that are present).
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string] $ScriptPath,
		[hashtable] $BoundParameters = @{}
	)

	if (Test-Elevated) { return }

	Write-Host "Not elevated — re-launching with UAC prompt..." -ForegroundColor Yellow

	# Use the same PS host the user invoked us with. (Get-Process -Id $PID).Path
	# returns the actual exe — powershell.exe (5.1) or pwsh.exe (7+).
	$hostExe = (Get-Process -Id $PID).Path
	if (-not $hostExe -or -not (Test-Path -LiteralPath $hostExe)) {
		# Fallback: classic PowerShell (always present on Windows).
		$hostExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
	}

	$argList = @(
		'-NoProfile'
		'-NoExit'                  # keep window open so user can read output
		'-ExecutionPolicy', 'Bypass'
		'-File', $ScriptPath
	)
	foreach ($key in $BoundParameters.Keys) {
		$val = $BoundParameters[$key]
		if ($val -is [System.Management.Automation.SwitchParameter]) {
			if ($val.IsPresent) { $argList += "-$key" }
		}
		else {
			$argList += "-$key"
			# Quote anything containing whitespace so Start-Process passes it as one token.
			$str = "$val"
			if ($str -match '\s') { $argList += "`"$str`"" } else { $argList += $str }
		}
	}

	try {
		Start-Process -FilePath $hostExe -ArgumentList $argList -Verb RunAs -ErrorAction Stop | Out-Null
	}
	catch {
		throw "Elevation failed (UAC declined or denied by policy): $($_.Exception.Message)"
	}

	# The elevated copy is now running in its own window. Exit the current
	# (un-elevated) process cleanly so the user doesn't see a confusing prompt.
	exit 0
}

# ────────────────────────────────────────────────────────────────────────────
# Hyper-V preflight
# ────────────────────────────────────────────────────────────────────────────

function Test-HyperVPrereqs {
	<#
	.SYNOPSIS
	  Verifies the host has Hyper-V installed. Elevation is enforced
	  separately by Request-Elevation in the entry-point script — we still
	  double-check here as a safety net for callers that didn't self-elevate.
	#>
	[CmdletBinding()]
	param()

	if (-not (Test-Elevated)) {
		throw "This script must be run elevated (Hyper-V cmdlets require admin). " +
			"The entry-point script should call Request-Elevation before this."
	}

	# Hyper-V module
	if (-not (Get-Command -Name New-VM -ErrorAction SilentlyContinue)) {
		throw "Hyper-V PowerShell module not found. Enable Hyper-V via " +
			"'Enable-WindowsOptionalFeature -Online -FeatureName Microsoft-Hyper-V -All' " +
			"and reboot."
	}

	# vTPM availability — required for Win11 install.
	if (-not (Get-Command -Name Set-VMKeyProtector -ErrorAction SilentlyContinue)) {
		Write-Warning "Set-VMKeyProtector not available. Win11 install will fail without vTPM."
	}
}

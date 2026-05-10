<#
.SYNOPSIS
  Shared helpers for the VhdxManager E2E VM bootstrap and test runner scripts.

.DESCRIPTION
  Designed to be dot-sourced (`. .\lib\Helpers.ps1`). Provides:
    - Resolve-Iso             : ask user to point at a Win11 Eval ISO or
                                offer to download from Microsoft.
    - Resolve-VmRoot          : pick C:\Hyper-V if it exists, else folder dialog.
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
# Console hyperlinks (OSC 8)
# ────────────────────────────────────────────────────────────────────────────

function Test-WindowsTerminal {
	<#
	.SYNOPSIS
	  Returns $true when the current process runs inside Windows Terminal
	  (as opposed to legacy ConHost / cmd.exe window).

	.DESCRIPTION
	  Windows Terminal sets WT_SESSION to a unique GUID per session — that's
	  the documented detection mechanism. Important for OSC 8 / hyperlinks:
	  WT supports them, ConHost does not.

	  When a PS script self-elevates via `Start-Process -Verb RunAs`, the new
	  admin process opens in ConHost by default. Request-Elevation works
	  around this by routing the elevation through `wt.exe new-tab` when
	  Windows Terminal is installed — in that case the elevated child sees
	  WT_SESSION too and Format-Hyperlink emits real OSC 8 escapes.
	#>
	[CmdletBinding()]
	param()
	return [bool]$env:WT_SESSION
}

function Format-Hyperlink {
	<#
	.SYNOPSIS
	  Wraps a URL in the OSC 8 hyperlink escape so Windows Terminal renders
	  it as a Ctrl-clickable link. Falls back to plain text in legacy
	  consoles (ConHost), which don't honour OSC 8.

	.DESCRIPTION
	  The full sequence is:  ESC ] 8 ;; URL ESC \  TEXT  ESC ] 8 ;; ESC \
	  Emitting it in ConHost would still render the visible text correctly
	  (the escape gets swallowed) but the link wouldn't be clickable, so we
	  bother only when we know the terminal supports it.

	.PARAMETER Url
	  Target URL.
	.PARAMETER Text
	  Visible text. Defaults to the URL itself.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string] $Url,
		[string] $Text
	)
	if (-not $Text) { $Text = $Url }
	if (Test-WindowsTerminal) {
		$e = [char]27
		return "$e]8;;$Url$e\$Text$e]8;;$e\"
	}
	return $Text
}

function Open-Url {
	<#
	.SYNOPSIS
	  Opens a URL in the user's default browser via Shell-Execute.

	.DESCRIPTION
	  `Start-Process <url>` invokes the shell handler for HTTP, which is the
	  user's configured default browser. Works from elevated processes — the
	  browser still launches as the interactive user (Windows shell handles
	  the de-elevation transparently for protocol handlers).
	#>
	[CmdletBinding()]
	param([Parameter(Mandatory)][string] $Url)
	try {
		Start-Process -FilePath $Url -ErrorAction Stop
	}
	catch {
		Write-Warning "Could not open browser automatically: $($_.Exception.Message)"
		Write-Host "Please copy the URL and open it manually." -ForegroundColor Yellow
	}
}

# ────────────────────────────────────────────────────────────────────────────
# ISO resolution
# ────────────────────────────────────────────────────────────────────────────

# Microsoft Evaluation Center landing page for Windows 11 Enterprise. The
# direct ISO URL changes with each refresh of the eval cycle, so we point at
# the landing page and let the user grab the current download manually.
$Script:Win11EvalUrl = 'https://www.microsoft.com/en-us/evalcenter/download-windows-11-enterprise'

function Show-IsoPickerDialog {
	<#
	.SYNOPSIS
	  Displays a Windows OpenFileDialog scoped to .iso files. Returns the
	  selected path, or $null if the user cancels.

	.DESCRIPTION
	  CheckFileExists + the .iso extension filter let the dialog itself
	  enforce "must exist + must be an ISO", so the caller doesn't need
	  the validation loop the old Read-Host path required.

	  InitialDirectory defaults to %USERPROFILE%\Downloads, where eval ISOs
	  usually land. Falls back to the user's profile root if that doesn't
	  exist.
	#>
	[CmdletBinding()]
	param()

	Add-Type -AssemblyName System.Windows.Forms | Out-Null

	# Resolve the real Downloads folder via the Shell KnownFolder API.
	# This correctly handles cases where the user relocated Downloads to a
	# different drive via Explorer → Properties → Location. A plain
	# Join-Path $env:USERPROFILE 'Downloads' would miss those.
	$initialDir = $env:USERPROFILE   # safe fallback
	try {
		$shell = New-Object -ComObject Shell.Application
		$knownPath = $shell.NameSpace('shell:Downloads').Self.Path
		[System.Runtime.InteropServices.Marshal]::ReleaseComObject($shell) | Out-Null
		if ($knownPath -and (Test-Path -LiteralPath $knownPath -PathType Container)) {
			$initialDir = $knownPath
		}
	}
	catch {
		# Shell.Application unavailable (shouldn't happen on any supported
		# Windows, but be defensive). Fall through to the $env:USERPROFILE
		# fallback set above.
	}

	$dialog = New-Object System.Windows.Forms.OpenFileDialog
	$dialog.Title            = 'Select the Windows 11 Enterprise Eval ISO'
	$dialog.Filter           = 'ISO image (*.iso)|*.iso'
	$dialog.CheckFileExists  = $true
	$dialog.Multiselect      = $false
	$dialog.InitialDirectory = $initialDir

	if ($dialog.ShowDialog() -eq [System.Windows.Forms.DialogResult]::OK) {
		return $dialog.FileName
	}
	return $null
}

function Resolve-Iso {
	<#
	.SYNOPSIS
	  Resolves the path to a Windows 11 Enterprise Eval ISO. Returns the path,
	  or $null if the user has no ISO yet (caller should exit cleanly).

	.PARAMETER ProvidedPath
	  Optional pre-supplied path (from the calling script's -IsoPath parameter).
	  If given, this function only validates and returns it — no prompts.

	.PARAMETER Silent
	  Non-interactive mode. If $true and no ProvidedPath is given, throws
	  instead of prompting. Used by automated/CI invocations.

	.OUTPUTS
	  String absolute path to a readable .iso file, or $null.
	#>
	[CmdletBinding()]
	param(
		[string] $ProvidedPath,
		[switch] $Silent
	)

	# Path supplied via parameter — validate and return.
	if ($ProvidedPath) {
		if (-not (Test-Path -LiteralPath $ProvidedPath -PathType Leaf)) {
			throw "ISO not found: $ProvidedPath"
		}
		if (-not $ProvidedPath.ToLowerInvariant().EndsWith('.iso')) {
			throw "Not an .iso file: $ProvidedPath"
		}
		return (Resolve-Path -LiteralPath $ProvidedPath).Path
	}

	# Silent mode without a path is a usage error: refuse to prompt.
	if ($Silent) {
		throw "ISO path is required in silent mode. Pass -IsoPath."
	}

	Write-Host ""
	Write-Host "=== Windows 11 Enterprise Eval ISO ===" -ForegroundColor Cyan
	Write-Host "A Windows 11 ISO is required to create the VM. Please select it in the file picker." -ForegroundColor White
	Write-Host ""

	$path = Show-IsoPickerDialog
	if ($null -ne $path) {
		Write-Host "  Selected: $path" -ForegroundColor DarkGray
		return $path
	}

	# User closed/cancelled the picker — offer download.
	Write-Host "No ISO selected." -ForegroundColor Yellow
	Write-Host ""
	Write-Host "Download Windows 11 Enterprise (90-day evaluation) from:" -ForegroundColor Yellow
	# OSC 8 hyperlink in Windows Terminal (Ctrl-click), plain text in ConHost.
	# When this script self-elevates, the elevated child runs in ConHost, so
	# Format-Hyperlink will return plain text in that case — that's why we
	# also auto-open the browser below as the universal fallback.
	Write-Host ("  " + (Format-Hyperlink -Url $Script:Win11EvalUrl)) -ForegroundColor White
	Write-Host ""
	Write-Host "  - Pick the 'ISO - Enterprise (English, United States)' option."
	Write-Host "  - Save the file anywhere on this host."
	Write-Host "  - Re-run this script and answer 'y' on the prompt above."
	Write-Host ""

	# Auto-open the browser unless the user explicitly declines. Default Y so
	# Enter is the one-keystroke path. Works in any terminal — UAC-elevated
	# ConHost included.
	$open = Read-Host "Open the download page in your default browser now? [Y/n]"
	if ([string]::IsNullOrWhiteSpace($open) -or $open -match '^(y|yes)$') {
		Open-Url -Url $Script:Win11EvalUrl
	}

	# Returning $null — not throwing — because this is the documented
	# "come back later" branch, not a failure. The caller decides what to do.
	return $null
}

# ────────────────────────────────────────────────────────────────────────────
# WIM image-name resolution
# ────────────────────────────────────────────────────────────────────────────

function Get-WindowsIsoImages {
	<#
	.SYNOPSIS
	  Briefly mounts a Windows install ISO, enumerates the images inside
	  sources\install.wim, and dismounts. Does not leave the ISO mounted.

	.OUTPUTS
	  Array of objects with ImageIndex (int) and ImageName (string).
	#>
	[CmdletBinding()]
	param([Parameter(Mandatory)] [string] $IsoPath)

	$diskImage = Mount-DiskImage -ImagePath $IsoPath -PassThru
	try {
		# Mount-DiskImage is asynchronous — Get-Volume can return $null briefly.
		$deadline = (Get-Date).AddSeconds(10)
		do {
			Start-Sleep -Milliseconds 200
			$volume = $diskImage | Get-Volume -ErrorAction SilentlyContinue
		} while ((-not $volume -or -not $volume.DriveLetter) -and (Get-Date) -lt $deadline)
		if (-not $volume -or -not $volume.DriveLetter) {
			throw "ISO mounted but volume not ready within 10s. Try manually: Mount-DiskImage -ImagePath '$IsoPath'"
		}

		$wimPath = Join-Path "$($volume.DriveLetter):" 'sources\install.wim'
		if (-not (Test-Path -LiteralPath $wimPath)) {
			throw "ISO has no sources\install.wim — is this a Windows install ISO?"
		}
		return @(Get-WindowsImage -ImagePath $wimPath | Select-Object ImageIndex, ImageName)
	}
	finally {
		Dismount-DiskImage -ImagePath $IsoPath -ErrorAction SilentlyContinue | Out-Null
	}
}

function Resolve-ImageName {
	<#
	.SYNOPSIS
	  Determines which Windows image inside install.wim to install.

	.DESCRIPTION
	  Resolution order:
	    1. If $RequestedName is given, validate it exists in the ISO.
	    2. If the ISO contains a single image, use it (no prompt).
	    3. Try a list of common Eval/Enterprise image names in priority
	       order — these handle the vast majority of Microsoft Eval ISOs.
	    4. Fall back to interactive picker; or fail with a clear list in
	       -Silent mode.

	.PARAMETER IsoPath
	  Path to the Windows install ISO. The ISO is mounted briefly and
	  released before this function returns.

	.PARAMETER RequestedName
	  User-supplied -ImageName, or $null/empty to trigger auto-resolution.

	.PARAMETER Silent
	  Non-interactive mode: forbid the picker.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string] $IsoPath,
		[string] $RequestedName,
		[switch] $Silent
	)

	Write-Host "Inspecting ISO for available Windows images..." -ForegroundColor DarkGray
	$images = Get-WindowsIsoImages -IsoPath $IsoPath

	# 1. User passed an explicit name — validate and return it as-is.
	if ($RequestedName) {
		$match = $images | Where-Object { $_.ImageName -eq $RequestedName }
		if (-not $match) {
			$list = ($images | ForEach-Object { "  [$($_.ImageIndex)] $($_.ImageName)" }) -join [Environment]::NewLine
			throw "Image '$RequestedName' not found in $IsoPath.$([Environment]::NewLine)Available images:$([Environment]::NewLine)$list"
		}
		Write-Host "Using image: '$RequestedName'" -ForegroundColor Green
		return $RequestedName
	}

	# 2. Single-image ISO → use it without prompting.
	if ($images.Count -eq 1) {
		Write-Host "ISO contains a single image: '$($images[0].ImageName)'" -ForegroundColor Green
		return $images[0].ImageName
	}

	# 3. Multiple images → try common defaults in priority order. The first
	#    two cover the standard Win11 Enterprise Eval and the LTSC Eval —
	#    the two ISOs people actually feed this script. The rest are
	#    pragmatic fallbacks for less-common SKUs.
	$candidates = @(
		'Windows 11 Enterprise'
		'Windows 11 Enterprise LTSC Evaluation'
		'Windows 11 Enterprise Evaluation'
		'Windows 11 Pro'
		'Windows 11 Pro Evaluation'
		'Windows 11 Education'
	)
	foreach ($c in $candidates) {
		if ($images | Where-Object { $_.ImageName -eq $c }) {
			Write-Host "Auto-selected image: '$c'" -ForegroundColor Green
			return $c
		}
	}

	# 4. Ambiguous — interactive picker, or fail in -Silent.
	if ($Silent) {
		$list = ($images | ForEach-Object { "  [$($_.ImageIndex)] $($_.ImageName)" }) -join [Environment]::NewLine
		throw "Cannot auto-select an image (no common default matches). Pass -ImageName.$([Environment]::NewLine)Available images:$([Environment]::NewLine)$list"
	}

	Write-Host ""
	Write-Host "ISO contains multiple images:" -ForegroundColor Cyan
	for ($i = 0; $i -lt $images.Count; $i++) {
		Write-Host ("  [{0}] {1}" -f ($i + 1), $images[$i].ImageName) -ForegroundColor White
	}
	$chosen = $null
	while (-not $chosen) {
		$answer = Read-Host "Pick one (1-$($images.Count))"
		$n = 0
		if ([int]::TryParse($answer, [ref]$n) -and $n -ge 1 -and $n -le $images.Count) {
			$chosen = $images[$n - 1].ImageName
		}
	}
	Write-Host "Using image: '$chosen'" -ForegroundColor Green
	return $chosen
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
	  Resolution order:
	    1. If $ProvidedRoot is given (from -VmRoot parameter), use it verbatim.
	    2. If C:\Hyper-V exists, use C:\Hyper-V (the VM itself lands in
	       C:\Hyper-V\<VmName> once Bootstrap-VM.ps1 appends $VmName).
	    3. Silent mode: throw — the caller must provide -VmRoot.
	    4. Otherwise open a folder-picker dialog; the chosen folder is returned
	       as-is (Bootstrap-VM.ps1 appends <VmName> to get the VM directory).

	.PARAMETER ProvidedRoot
	  Pre-supplied parent path; returned as-is. The caller (Bootstrap-VM.ps1)
	  appends the VM name to derive the per-VM directory. Pass when invoking
	  from CI/scripts via -VmRoot.

	.PARAMETER Silent
	  Non-interactive mode. Forbid the folder-picker dialog.
	#>
	[CmdletBinding()]
	param(
		[string] $ProvidedRoot,
		[switch] $Silent
	)

	# 1. Explicit override via parameter — use literal path.
	if ($ProvidedRoot) {
		Write-Host "Using VM root: $ProvidedRoot" -ForegroundColor Green
		return $ProvidedRoot
	}

	# 2. Convention: C:\Hyper-V exists → use it as the parent; Bootstrap-VM.ps1
	#    appends $VmName so the VM lives at C:\Hyper-V\<VmName>.
	$default = 'C:\Hyper-V'
	if (Test-Path -LiteralPath $default -PathType Container) {
		Write-Host "Using VM root: $default" -ForegroundColor Green
		return $default
	}

	# 3. Silent mode without an explicit -VmRoot is a usage error.
	if ($Silent) {
		throw "C:\Hyper-V does not exist and -VmRoot was not provided. Pass -VmRoot in silent mode."
	}

	# 4. Interactive folder picker.
	Write-Host "C:\Hyper-V does not exist. Please pick a parent directory for VM storage." -ForegroundColor Yellow
	Add-Type -AssemblyName System.Windows.Forms
	$dialog = New-Object System.Windows.Forms.FolderBrowserDialog
	$dialog.Description = 'Pick a parent directory for VhdxManagerE2E (~40GB free space recommended)'
	$dialog.ShowNewFolderButton = $true
	$result = $dialog.ShowDialog()
	if ($result -ne [System.Windows.Forms.DialogResult]::OK) {
		throw "VM-root selection cancelled."
	}
	$root = $dialog.SelectedPath
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
			Write-Host "  Poll #$poll`: guest not yet reachable — OS install in progress..." -ForegroundColor DarkGray
		}
		Start-Sleep -Seconds $PollIntervalSeconds
	}
	throw "Guest did not become ready within $TimeoutMinutes minutes."
}

# ────────────────────────────────────────────────────────────────────────────
# PowerShell version guard
# ────────────────────────────────────────────────────────────────────────────

function Assert-PowerShellVersion {
	<#
	.SYNOPSIS
	  Aborts with a friendly error if the current PowerShell version is older
	  than $MinimumVersion. Default minimum is 5.1 — the version that ships
	  with Windows 10 1607+ / Windows 11 / Server 2016+.

	.DESCRIPTION
	  Our scripts deliberately avoid PowerShell 7-only syntax (ternary, null
	  coalescing, pipeline chain operators, classes), so PS 5.1 is sufficient
	  and we do NOT force users to install PS 7 to run the test rig.

	  Older PowerShell versions (PS 4.0 on Win 8.1, PS 2.0 on Win 7) lack
	  some of the .NET surface (RandomNumberGenerator.Create + GetBytes
	  patterns) and Hyper-V cmdlets we depend on, so we refuse to run on
	  those rather than fail with a confusing error mid-script.

	.PARAMETER MinimumVersion
	  Minimum acceptable [Version]. Default: 5.1.
	#>
	[CmdletBinding()]
	param(
		[Version] $MinimumVersion = ([Version]'5.1')
	)
	$current = $PSVersionTable.PSVersion
	if ($current -ge $MinimumVersion) { return }

	Write-Host ""
	Write-Host "PowerShell $MinimumVersion or newer is required (current: $current)." -ForegroundColor Red
	Write-Host ""
	Write-Host "Windows 10 (1607+) and Windows 11 ship with PowerShell 5.1 by default." -ForegroundColor Yellow
	Write-Host "If you ended up on an older host, install the latest PowerShell from:" -ForegroundColor Yellow
	Write-Host ""
	Write-Host ("  " + (Format-Hyperlink -Url 'https://github.com/PowerShell/PowerShell/releases/latest')) -ForegroundColor White
	Write-Host ""
	exit 1
}

# ────────────────────────────────────────────────────────────────────────────
# Self-elevation
# ────────────────────────────────────────────────────────────────────────────

function ConvertTo-CmdLineArg {
	<#
	.SYNOPSIS
	  Quotes a single argument for inclusion in a Windows command line so
	  CommandLineToArgvW (the receiving process's parser) reproduces the
	  original token verbatim.

	.DESCRIPTION
	  Critical for Start-Process. Despite the name, PowerShell's
	  `Start-Process -ArgumentList @('a', 'b c')` joins the array with bare
	  spaces — `a b c` — and does NOT add quotes around tokens containing
	  whitespace. The receiving process (wt.exe, pwsh.exe, …) then re-tokenises
	  on whitespace and gets `a`, `b`, `c` as three separate args. We quote
	  here so the round-trip is faithful.

	  Quoting rules (per the standard CRT parser):
	    - empty string  → ""
	    - has whitespace, double-quote, or paren → wrap in "...", escape any
	      internal " as \"
	    - otherwise pass through unchanged
	#>
	[CmdletBinding()]
	param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Value)

	if ([string]::IsNullOrEmpty($Value)) { return '""' }
	# Parens trigger cmd.exe parsing oddities in some scenarios; safest to
	# always quote when any of these appear.
	if ($Value -match '[\s"()]') {
		$escaped = $Value.Replace('"', '\"')
		return "`"$escaped`""
	}
	return $Value
}

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

	  Launch strategy:
	    * If wt.exe is on PATH, launch the elevated PowerShell *inside*
	      Windows Terminal (`wt.exe new-tab pwsh.exe ...`) so the elevated
	      session keeps OSC 8 hyperlinks, true colour, and the rest of the
	      WT UX. UAC kicks in for wt.exe; the resulting WT window is fully
	      elevated.
	    * Otherwise, direct `Start-Process pwsh.exe -Verb RunAs` — opens in
	      legacy ConHost, no hyperlinks, but works on every Windows.

	  Caller pattern, near the top of the entry-point script:

	      . .\lib\Helpers.ps1
	      Request-Elevation -ScriptPath $PSCommandPath -BoundParameters $PSBoundParameters

	.PARAMETER ScriptPath
	  Full path to the script that should be re-launched. Pass $PSCommandPath
	  from the caller (resolves automatically to the script's own path).

	.PARAMETER BoundParameters
	  $PSBoundParameters from the caller. Each entry is forwarded as
	  "-Key Value" (or just "-Key" for switches that are present).

	.PARAMETER Silent
	  When $true, refuse to self-elevate — UAC requires interaction and the
	  caller (e.g. CI agent) must run elevated to begin with. Throws with a
	  clear remediation message. No-op when already elevated.
	#>
	[CmdletBinding()]
	param(
		[Parameter(Mandatory)] [string] $ScriptPath,
		[hashtable] $BoundParameters = @{},
		[switch] $Silent
	)

	if (Test-Elevated) { return }

	if ($Silent) {
		throw "Cannot self-elevate in silent mode (UAC requires interaction). " +
			"Re-launch the script from an already-elevated PowerShell session."
	}

	# Use the same PS host the user invoked us with. (Get-Process -Id $PID).Path
	# returns the actual exe — powershell.exe (5.1) or pwsh.exe (7+).
	$hostExe = (Get-Process -Id $PID).Path
	if (-not $hostExe -or -not (Test-Path -LiteralPath $hostExe)) {
		# Fallback: classic PowerShell (always present on Windows).
		$hostExe = Join-Path $env:WINDIR 'System32\WindowsPowerShell\v1.0\powershell.exe'
	}

	# Inner-process arguments: pwsh switches + -File + forwarded bound params.
	# Every token gets pre-quoted via ConvertTo-CmdLineArg so Start-Process's
	# bare-space join produces a command line CommandLineToArgvW can parse
	# back into the same tokens.
	$innerArgs = @(
		'-NoProfile'
		'-NoExit'                  # keep window open so user can read output
		'-ExecutionPolicy', 'Bypass'
		'-File', (ConvertTo-CmdLineArg $ScriptPath)
	)
	foreach ($key in $BoundParameters.Keys) {
		$val = $BoundParameters[$key]
		if ($val -is [System.Management.Automation.SwitchParameter]) {
			if ($val.IsPresent) { $innerArgs += "-$key" }
		}
		else {
			$innerArgs += "-$key"
			$innerArgs += (ConvertTo-CmdLineArg "$val")
		}
	}

	$wt = Get-Command -Name 'wt.exe' -ErrorAction SilentlyContinue
	if ($wt) {
		Write-Host "Not elevated — re-launching elevated in Windows Terminal..." -ForegroundColor Yellow
		# wt.exe argv: [global-opts] action [action-opts] command [args...]
		# `new-tab` is the action; everything after the title is the command
		# WT runs in the new tab. WT uses a CommandLineToArgvW-compatible
		# parser, so quoted multi-word args (e.g. "VhdxManager E2E (elevated)"
		# or paths under "C:\Program Files\…") survive verbatim.
		$wtArgs = @(
			'new-tab',
			'--title', (ConvertTo-CmdLineArg 'VhdxManager E2E (elevated)'),
			(ConvertTo-CmdLineArg $hostExe)
		) + $innerArgs

		try {
			Start-Process -FilePath $wt.Source -ArgumentList $wtArgs -Verb RunAs -ErrorAction Stop | Out-Null
		}
		catch {
			throw "Elevation via Windows Terminal failed: $($_.Exception.Message)"
		}
	}
	else {
		Write-Host "Not elevated — re-launching with UAC prompt (ConHost)..." -ForegroundColor Yellow
		try {
			Start-Process -FilePath $hostExe -ArgumentList $innerArgs -Verb RunAs -ErrorAction Stop | Out-Null
		}
		catch {
			throw "Elevation failed (UAC declined or denied by policy): $($_.Exception.Message)"
		}
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

#Requires -Version 5.1
<#
.SYNOPSIS
    Bump the patch component of <DefaultVersion> in the WiX installer project,
    write the result back to the .wixproj, and emit the new version on stdout.

.DESCRIPTION
    `installer\VhdxManager.Installer.wixproj` is the single source of truth for
    the release version: its <DefaultVersion> drives $(Version), which in turn
    drives the MSI's ProductVersion, the embedded service & CLI EXE
    FileVersion (via /p:Version on `dotnet publish`), and the output filename
    `VhdxManager-X.Y.Z.msi`.

    Auto-bumping the patch component on every release build is what makes the
    WiX MajorUpgrade flow work end-to-end: Windows Installer's default
    REINSTALLMODE compares file versions, so the new MSI's binaries MUST have
    a strictly higher FileVersion than the on-disk install or upgrade will
    silently keep the old files. Driving everything from /p:Version=<bumped>
    guarantees that.

.PARAMETER WixProjPath
    Absolute or relative path to the .wixproj. Defaults to
    `installer\VhdxManager.Installer.wixproj` relative to this script.

.OUTPUTS
    String. The new version (e.g. "0.2.1") on stdout — `for /f` in
    build.release.cmd captures it. The .wixproj is updated in place.

.NOTES
    File rewrite uses regex on the raw text rather than [xml] casting so the
    diff is a single line change — XmlDocument.Save reflows whitespace and
    can add/remove the XML declaration.
#>
[CmdletBinding()]
param(
    [string]$WixProjPath = (Join-Path $PSScriptRoot '..\installer\VhdxManager.Installer.wixproj')
)
$ErrorActionPreference = 'Stop'

$WixProjPath = [System.IO.Path]::GetFullPath($WixProjPath)
if (-not (Test-Path -LiteralPath $WixProjPath)) {
    throw "wixproj not found at '$WixProjPath'"
}

$content = Get-Content -LiteralPath $WixProjPath -Raw
if ($content -notmatch '<DefaultVersion>(\d+)\.(\d+)\.(\d+)</DefaultVersion>') {
    throw "Could not find Major.Minor.Patch <DefaultVersion>...</DefaultVersion> in $WixProjPath"
}

$major = [int]$Matches[1]
$minor = [int]$Matches[2]
$patch = [int]$Matches[3] + 1
$new   = "$major.$minor.$patch"

# Replace only the FIRST DefaultVersion occurrence — there should only ever be
# one, but be defensive: a misplaced Condition-guarded property elsewhere
# shouldn't get swept along with the bump.
$updated = [regex]::Replace(
    $content,
    '<DefaultVersion>\d+\.\d+\.\d+</DefaultVersion>',
    "<DefaultVersion>$new</DefaultVersion>",
    [System.Text.RegularExpressions.RegexOptions]::None,
    [System.Threading.Timeout]::InfiniteTimeSpan)

# Preserve the file's original EOL convention. -Raw preserved everything as-is;
# -NoNewline + the unchanged string is the closest we get to a no-op write.
Set-Content -LiteralPath $WixProjPath -Value $updated -NoNewline -Encoding utf8

Write-Output $new

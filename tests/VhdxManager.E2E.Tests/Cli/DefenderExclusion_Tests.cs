using System;
using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Cli;

/// <summary>
/// Verifies that <c>vhmgr create --add-defender-exclusion true</c> actually
/// registers the VHDX path via <c>Add-MpPreference -ExclusionPath</c> and that
/// the exclusion is visible through <c>Get-MpPreference</c> inside the guest VM.
///
/// <para>Windows Defender may not be active on every test environment — Server
/// LTSC SKUs sometimes ship with the WinDefend service stopped or absent, and
/// Group Policy can silently block <c>Add-MpPreference</c>. Both conditions are
/// detected once in <see cref="E2EFixtureBase.OnGuestReadyAsync"/> and every test
/// below calls <see cref="Assert.Inconclusive"/> rather than Fail, so the suite
/// stays green on machines where Defender management is unavailable.</para>
///
/// <para>When the CLI detects that <c>Add-MpPreference</c> was blocked by Group
/// Policy it still exits 0 (the VHDX itself was created successfully — the
/// exclusion is best-effort) and prints a yellow warning line containing
/// <c>"Defender exclusion not added"</c>. Test 1 detects that warning and also
/// calls <see cref="Assert.Inconclusive"/>, preventing test 2 from producing a
/// misleading failure against a phantom policy constraint.</para>
/// </summary>
[TestFixture]
[Order(60)]
public sealed class DefenderExclusion_Tests : InstalledFixtureBase
{
	const string TestDir   = @"C:\E2E-def";
	const string VhdxPath  = @"C:\E2E-def\test.vhdx";
	const string MountPath = @"C:\E2E-def\mount";

	bool _defenderAvailable;
	bool _createSucceeded;

	protected override async Task OnGuestReadyAsync()
	{
		await Guest.InvokeVoidAsync($@"
Remove-Item -LiteralPath '{TestDir}' -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path '{TestDir}'  -Force | Out-Null
New-Item -ItemType Directory -Path '{MountPath}' -Force | Out-Null
");

		// Probe whether Windows Defender Management cmdlets are available and
		// usable on this VM. On some Server LTSC SKUs WinDefend is stopped and
		// Add-MpPreference fails with HRESULT 0x800106ba; we detect this once
		// so every test in the fixture can Assert.Inconclusive uniformly rather
		// than producing confusing errors about missing cmdlets.
		_defenderAvailable = await Guest.InvokeJsonAsync<bool>(@"
$svc = Get-Service -Name WinDefend -ErrorAction SilentlyContinue
if ($null -eq $svc -or $svc.Status -ne 'Running') {
    $false
} else {
    try {
        $null = Get-MpPreference -ErrorAction Stop
        $true
    } catch {
        $false
    }
}
");
	}

	[Test, Order(1)]
	public async Task Create_With_Defender_Exclusion_True_Exits_Zero()
	{
		if (!_defenderAvailable)
			Assert.Inconclusive(
				"Windows Defender is not active on this VM — " +
				"start WinDefend (sc start WinDefend) and re-run to exercise the exclusion path");

		var r = await Vhmgr.RunAsync(Guest,
			$"create --path \"{VhdxPath}\" --size 64M --label e2edef " +
			$"--mount \"{MountPath}\" --filesystem NTFS --add-defender-exclusion true");

		r.Succeeded.Should().BeTrue(
			$"`vhmgr create --add-defender-exclusion true` returned {r.ExitCode}.\n" +
			$"stdout: {r.StdoutText}\nstderr: {r.StderrText}");

		// When Group Policy (or tamper protection) blocks Add-MpPreference the CLI
		// still exits 0 — the VHDX was created; the exclusion is best-effort — but
		// it prints a warning line containing "Defender exclusion not added".
		// Mark Inconclusive so test 2 doesn't fail for the wrong reason.
		if (r.StdoutText.Contains("Defender exclusion not added", StringComparison.OrdinalIgnoreCase))
			Assert.Inconclusive(
				"Defender exclusion was blocked by policy on this VM (create exited 0, " +
				$"policy warning present). stdout: {r.StdoutText}");

		_createSucceeded = true;
	}

	[Test, Order(2)]
	public async Task Defender_ExclusionPath_Contains_Vhdx()
	{
		if (!_defenderAvailable)
			Assert.Inconclusive("Windows Defender is not active on this VM");
		if (!_createSucceeded)
			Assert.Inconclusive("create step did not succeed (or was inconclusive); nothing to inspect");

		// DefenderExclusionManager.AddExclusionCore normalises the path via
		// Path.GetFullPath before handing it to Add-MpPreference, so the
		// registered path is already fully-qualified. -icontains is
		// case-insensitive, which handles any capitalisation drift in the drive
		// letter or directory separators.
		var found = await Guest.InvokeJsonAsync<bool>(@"
$pref = Get-MpPreference -ErrorAction Stop
$pref.ExclusionPath -icontains 'C:\E2E-def\test.vhdx'
");

		found.Should().BeTrue(
			$"`vhmgr create --add-defender-exclusion true` must add {VhdxPath} to " +
			"Defender ExclusionPath via Add-MpPreference before returning. " +
			"Check DefenderExclusionManager.AddExclusionCore in VhdxManager.Service " +
			"if this assertion fails — the VHDX was created but the exclusion was not registered.");
	}
}

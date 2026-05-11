using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Cli;

/// <summary>
/// <c>vhmgr convert</c> takes an existing folder, renames it aside, creates
/// a fresh VHDX in its place, formats + mounts the VHDX to the original
/// path, and robocopies the renamed folder's contents back in. The
/// destructive bits are gated by <c>--yes</c> (skip the confirmation
/// prompt) so tests can run unattended.
/// </summary>
[TestFixture]
[Order(40)]
public sealed class Convert_Tests : InstalledFixtureBase
{
	private const string SourceDir   = @"C:\E2E-conv\src";
	private const string VhdxPath    = @"C:\E2E-conv\src.vhdx";
	private const string MarkerFile  = @"C:\E2E-conv\src\hello.txt";

	private bool _convertSucceeded;

	protected override async Task OnGuestReadyAsync()
	{
		// Build a small source folder with one tagged file so we can prove
		// the robocopy step preserved content end-to-end.
		await Guest.InvokeVoidAsync($@"
Remove-Item -LiteralPath 'C:\E2E-conv' -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Path '{SourceDir}' -Force | Out-Null
Set-Content -Path '{MarkerFile}' -Value 'preserve-me' -Encoding utf8 -Force
");
	}

	[Test, Order(1)]
	public async Task Convert_Folder_To_Vhdx_Backed_Mount()
	{
		// convert hardcodes ReFS (no --filesystem flag exposed). ReFS rejects
		// small volumes — 256 MB throws "Size Not Supported", 1 GB throws
		// "operation failed with return code 40000". 4 GB is the smallest
		// size we've observed format reliably. The VHDX is dynamic (default),
		// so actual on-disk footprint is only a few MB until populated —
		// 4 GB is just the logical maximum.
		var r = await Vhmgr.RunAsync(Guest,
			$"convert --folder \"{SourceDir}\" --vhdx \"{VhdxPath}\" --size 4G --label e2econv " +
			$"--yes --add-defender-exclusion false");

		r.Succeeded.Should().BeTrue(
			$"`vhmgr convert` returned {r.ExitCode}.\nstdout: {r.StdoutText}\nstderr: {r.StderrText}");

		_convertSucceeded = true;

		// VHDX file should exist on disk at the location we asked for…
		await GuestFs.AssertFileExistsAsync(Guest, VhdxPath);
	}

	[Test, Order(2)]
	public async Task Original_Path_Is_Now_The_Vhdx_Mount()
	{
		if (!_convertSucceeded) Assert.Inconclusive("convert step failed; nothing to inspect");

		// After convert, the original folder path is now the mount point
		// for the new VHDX, and the marker file we wrote pre-convert
		// should still be readable through that mount.
		await GuestFs.AssertFileExistsAsync(Guest, MarkerFile);

		var content = await GuestFs.ReadAllTextAsync(Guest, MarkerFile);
		content.Should().Contain("preserve-me",
			"convert should robocopy the source folder's contents into the new VHDX " +
			"and remount it at the original path — the marker should still be there");
	}

	[Test, Order(3)]
	public async Task List_Shows_The_Converted_Mount()
	{
		if (!_convertSucceeded) Assert.Inconclusive("convert step failed; nothing to inspect");

		var r = await Vhmgr.RunAsync(Guest, "list");
		r.Succeeded.Should().BeTrue();
		r.StdoutText.Should().Contain("src.vhdx",
			$"convert mounts the new VHDX — it should appear in list. stdout: {r.StdoutText}");
	}
}

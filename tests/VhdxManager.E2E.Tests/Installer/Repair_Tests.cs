using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Installer;

/// <summary>
/// MSI repair: intentionally delete a managed file from the guest, run
/// <c>msiexec /fp &lt;msi&gt; /qn</c>, and assert the file is restored.
///
/// <para>The <c>/fp</c> mode reinstalls any file that is missing — it does
/// not touch files that are present and unmodified. We delete
/// <c>vhmgr.exe</c> (the CLI binary) rather than the service executable
/// to avoid complications from file locks on the running service; the
/// service is stopped and restarted by <c>ServiceControl Stop="both"</c>
/// anyway, but targeting a non-running binary keeps the setup simpler.</para>
///
/// <para>After the repair the service should be running again (ServiceControl
/// <c>Start="install"</c> fires a restart) and the deleted file must exist.</para>
/// </summary>
[TestFixture]
[Order(4)]
public sealed class Repair_Tests : InstalledFixtureBase
{
	private const string CliExe = @"C:\Program Files\VhdxManager\Cli\vhmgr.exe";

	private MsiResult _repairResult = null!;

	protected override async Task OnGuestReadyAsync()
	{
		// Deliberately corrupt the installation by removing the CLI binary.
		await Guest.InvokeVoidAsync($@"Remove-Item -LiteralPath '{CliExe}' -Force");

		// Confirm the file is actually gone before invoking repair so a
		// false-positive in the post-repair assertion is immediately visible.
		var gone = await GuestFs.ExistsAsync(Guest, CliExe);
		Assert.That(gone, Is.False, "pre-repair: vhmgr.exe should be deleted before repair runs");

		var guestMsiPath = InstalledCheckpoint.GuestMsiPath(Msi);
		_repairResult = await MsiInstaller.RepairSilentAsync(Guest, guestMsiPath);

		// ServiceControl Wait="no" fires the start request and lets msiexec exit;
		// give the SCM a moment to bring the process up before the tests check it.
		await Guest.InvokeVoidAsync("Start-Sleep -Seconds 5");
	}

	[Test]
	public void Repair_Exits_Zero()
	{
		_repairResult.Succeeded.Should().BeTrue(
			$"msiexec /fp returned exit code {_repairResult.ExitCode} (expected 0). " +
			$"Log: {_repairResult.LogPath}\nTail:\n{_repairResult.LogTail}");
	}

	[Test]
	public async Task Deleted_Cli_Binary_Restored_After_Repair()
	{
		if (!_repairResult.Succeeded) Assert.Inconclusive("Repair step failed.");
		// The primary assertion: /fp must restore any missing managed file.
		await GuestFs.AssertFileExistsAsync(Guest, CliExe);
	}

	[Test]
	public async Task Service_Running_After_Repair()
	{
		if (!_repairResult.Succeeded) Assert.Inconclusive("Repair step failed.");
		// ServiceControl Stop="both" stops the service; Start="install" restarts it.
		// Assert the service is Running (not just Registered) after the MSI exits.
		await GuestService.AssertRunningAsync(Guest, "VhdxManagerService");
	}
}

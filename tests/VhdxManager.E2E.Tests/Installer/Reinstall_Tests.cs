using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Installer;

/// <summary>
/// Idempotent reinstall: run <c>msiexec /i &lt;msi&gt; /qn</c> against a guest
/// that already has the same version installed, then assert the installation
/// is still fully functional.
///
/// <para>When Windows Installer finds the product code already registered it
/// enters maintenance mode. In silent (<c>/qn</c>) maintenance mode with no
/// feature-selection properties set it performs a default repair (the same
/// effect as <c>/f ocmus</c>). The expected outcome is exit code 0 and an
/// unchanged, functional installation.</para>
///
/// <para>Starting from the <c>installed-clean@&lt;sha8&gt;</c> checkpoint keeps
/// the run cheap: restoring + installing is skipped; we go straight to the
/// second install invocation.</para>
/// </summary>
[TestFixture]
[Order(3)]
public sealed class Reinstall_Tests : InstalledFixtureBase
{
	MsiResult reinstallResult = null!;

	protected override async Task OnGuestReadyAsync()
	{
		// The MSI is already staged at C:\Setup\ by InstalledCleanCheckpointFixture.
		// Run msiexec /i again over the already-installed product — no copy needed.
		var guestMsiPath = InstalledCheckpoint.GuestMsiPath(Msi);
		reinstallResult = await MsiInstaller.InstallSilentAsync(Guest, guestMsiPath);
	}

	[Test]
	public void Reinstall_Over_Existing_Install_Exits_Zero()
	{
		reinstallResult.Succeeded.Should().BeTrue(
			$"msiexec /i over an already-installed product returned exit code " +
			$"{reinstallResult.ExitCode} (expected 0). " +
			$"Log: {reinstallResult.LogPath}\nTail:\n{reinstallResult.LogTail}");
	}

	[Test]
	public async Task Service_Still_Running_After_Reinstall()
	{
		if (!reinstallResult.Succeeded) Assert.Inconclusive("Reinstall step failed.");
		// ServiceControl Stop="both" stops the service during reinstall;
		// Start="install" fires a restart. We assert the service ended up Running.
		await GuestService.AssertRunningAsync(Guest, "VhdxManagerService");
	}

	[Test]
	public async Task Key_Files_Present_After_Reinstall()
	{
		if (!reinstallResult.Succeeded) Assert.Inconclusive("Reinstall step failed.");
		await GuestFs.AssertFileExistsAsync(Guest,
			@"C:\Program Files\VhdxManager\Service\VhdxManager.Service.exe");
		await GuestFs.AssertFileExistsAsync(Guest,
			@"C:\Program Files\VhdxManager\Cli\vhdx.exe");
	}

	[Test]
	public async Task Cli_Still_On_Path_After_Reinstall()
	{
		if (!reinstallResult.Succeeded) Assert.Inconclusive("Reinstall step failed.");
		var onPath = await GuestFs.IsOnPathAsync(Guest, "vhdx.exe");
		onPath.Should().BeTrue(
			"reinstall must leave the CLI directory on the machine PATH");
	}
}

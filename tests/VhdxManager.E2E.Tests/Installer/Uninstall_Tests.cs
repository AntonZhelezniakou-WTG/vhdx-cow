using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Installer;

/// <summary>
/// Starts from the per-MSI <c>installed-clean@&lt;sha8&gt;</c> checkpoint
/// (created by <see cref="Bootstrap.InstalledCleanCheckpointFixture"/>),
/// uninstalls the MSI in <c>[OneTimeSetUp]</c>, then asserts the guest is
/// clean.
///
/// <para>Restoring an already-installed checkpoint is significantly cheaper
/// than restoring pre-install + reinstalling, which is why we maintain
/// two checkpoints rather than one.</para>
/// </summary>
[TestFixture]
[Order(2)]
public sealed class Uninstall_Tests : E2EFixtureBase
{
	private MsiArtefact _msi = null!;
	private MsiResult   _uninstallResult = null!;

	// CheckpointName is computed from the MSI hash, but it's accessed by the
	// base fixture's [OneTimeSetUp] before our [OneTimeSetUp] runs — so we
	// resolve the MSI lazily on first access and cache the name.
	private string? _checkpointName;
	protected override string CheckpointName
	{
		get
		{
			_msi          ??= MsiArtefact.LoadOrSkip(E2EConfig.FindRepoRoot()!);
			_checkpointName ??= InstalledCheckpoint.NameFor(_msi);
			return _checkpointName;
		}
	}

	protected override async Task OnGuestReadyAsync()
	{
		var guestMsiPath = InstalledCheckpoint.GuestMsiPath(_msi);
		_uninstallResult = await MsiInstaller.UninstallSilentAsync(Guest, guestMsiPath);
	}

	[Test]
	public void Msiexec_SilentUninstall_ExitsZero()
	{
		_uninstallResult.Succeeded.Should().BeTrue(
			$"msiexec /x {_msi.FileName} returned {_uninstallResult.ExitCode}. " +
			$"Guest log: {_uninstallResult.LogPath}\nTail:\n{_uninstallResult.LogTail}");
	}

	[Test]
	public async Task Service_Is_Gone()
	{
		if (!_uninstallResult.Succeeded) Assert.Inconclusive("MSI uninstall failed.");
		await GuestService.AssertNotRegisteredAsync(Guest, "VhdxManagerService");
	}

	[Test]
	public async Task Program_Files_Directory_Is_Removed()
	{
		if (!_uninstallResult.Succeeded) Assert.Inconclusive("MSI uninstall failed.");
		var exists = await GuestFs.ExistsAsync(Guest, @"C:\Program Files\VhdxManager");
		exists.Should().BeFalse(
			"the entire Program Files\\VhdxManager tree should be removed by uninstall " +
			"(Service\\ and Cli\\ are the only contents, both owned by the MSI)");
	}
}

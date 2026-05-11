using System;
using System.Threading.Tasks;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Bootstrap;

/// <summary>
/// Lazy creator of the <c>installed-clean@&lt;sha8&gt;</c> checkpoint.
/// Runs first in the assembly (<c>[Order(-1)]</c>). If the checkpoint
/// already exists for the current MSI's hash, the fixture is a fast no-op;
/// otherwise it restores <c>pre-install-clean</c>, copies the MSI, installs
/// it silently, and snapshots the result.
///
/// <para>This separation matters: per-MSI checkpoints turn the installer
/// fixture's <c>[OneTimeSetUp]</c> from "30-60 s boot + 30 s install" into
/// just the boot — every subsequent uninstall fixture (and Phase B verb
/// fixtures) restore to an already-installed VM.</para>
/// </summary>
[TestFixture]
[Order(-1)]
[Category("E2E")]
[Parallelizable(ParallelScope.None)]
public sealed class InstalledCleanCheckpointFixture
{
	[Test]
	public async Task Ensure_Installed_Clean_Checkpoint_Exists()
	{
		var config = E2EConfig.LoadOrSkip();
		var msi    = MsiArtefact.LoadOrSkip(config.RepoRoot);
		var ps     = new PowerShellRunner(config.HelpersScriptPath);
		var vm     = new VmHost(config.VmName, ps);

		var checkpointName = InstalledCheckpoint.NameFor(msi);
		if (await vm.SnapshotExistsAsync(checkpointName))
		{
			TestContext.WriteLine($"Checkpoint '{checkpointName}' already exists — skipping creation.");
			return;
		}

		TestContext.WriteLine($"Creating checkpoint '{checkpointName}' from MSI {msi.FileName}...");

		await vm.RestoreSnapshotAsync("pre-install-clean");
		await vm.StartAsync();
		await vm.WaitGuestReadyAsync(config.GuestUsername, config.GuestPassword,
			TimeSpan.FromMinutes(5));

		try
		{
			var guest = new GuestSession(config.VmName, config.GuestUsername,
				config.GuestPassword, ps);

			// Stage destination dir first — Copy-Item -ToSession with a file
			// destination requires the parent dir to exist.
			await guest.InvokeVoidAsync(@"New-Item -Path 'C:\Setup' -ItemType Directory -Force | Out-Null");

			var guestMsiPath = InstalledCheckpoint.GuestMsiPath(msi);
			await guest.CopyToGuestAsync(msi.Path, guestMsiPath);

			var install = await MsiInstaller.InstallSilentAsync(guest, guestMsiPath);
			Assert.That(install.Succeeded, Is.True,
				$"msiexec /i failed with exit code {install.ExitCode}. Log on guest: {install.LogPath}\n" +
				$"Tail:\n{install.LogTail}");

			// Power off cleanly before snapshotting — Standard checkpoints
			// captured while running pull in saved-state memory pages which
			// bloats the VHDX and slows future restores.
			await vm.StopAsync(turnOff: false);
			await vm.TakeCheckpointAsync(checkpointName);

			TestContext.WriteLine($"Checkpoint '{checkpointName}' created.");
		}
		finally
		{
			await vm.StopAsync(turnOff: true);
		}
	}
}

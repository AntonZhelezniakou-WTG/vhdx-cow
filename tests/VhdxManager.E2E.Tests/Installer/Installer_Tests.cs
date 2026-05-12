using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Installer;

/// <summary>
/// Restores the <c>pre-install-clean</c> snapshot, installs the MSI silently
/// in <see cref="E2EFixtureBase.OnGuestReadyAsync"/>, then runs a handful of
/// parallel-friendly assertions against the post-install guest state. All
/// tests share one install — restoring + installing per test would more
/// than triple the wall-clock time without buying any isolation
/// (assertions are read-only).
/// </summary>
[TestFixture]
[Order(1)]
public sealed class Installer_Tests : E2EFixtureBase
{
	protected override string CheckpointName => "pre-install-clean";

	MsiArtefact msi = null!;
	MsiResult installResult = null!;

	protected override async Task OnGuestReadyAsync()
	{
		msi = MsiArtefact.LoadOrSkip(Config.RepoRoot);

		await Guest.InvokeVoidAsync(@"New-Item -Path 'C:\Setup' -ItemType Directory -Force | Out-Null");
		var guestMsiPath = InstalledCheckpoint.GuestMsiPath(msi);
		await Guest.CopyToGuestAsync(msi.Path, guestMsiPath);

		installResult = await MsiInstaller.InstallSilentAsync(Guest, guestMsiPath);
	}

	[Test]
	public void Msiexec_SilentInstall_ExitsZero()
	{
		installResult.Succeeded.Should().BeTrue(
			$"msiexec /i {msi.FileName} returned {installResult.ExitCode}. Guest log: {installResult.LogPath}\nTail:\n{installResult.LogTail}");
	}

	[Test]
	public async Task Service_Registered_Running_LocalSystem_AutoStart()
	{
		// Skip the sub-assertions if the install itself failed — otherwise
		// every assertion in the fixture parrots the same root-cause failure.
		if (!installResult.Succeeded) Assert.Inconclusive("MSI install failed.");

		await GuestService.AssertRunningAsync(Guest, "VhdxManagerService");
		await GuestService.AssertStartModeAsync(Guest, "VhdxManagerService", "Auto");
		await GuestService.AssertStartNameAsync(Guest, "VhdxManagerService", "LocalSystem");
	}

	[Test]
	public async Task Files_Present_At_Expected_Paths()
	{
		if (!installResult.Succeeded) Assert.Inconclusive("MSI install failed.");

		// Service binaries + their config (appsettings.json ships *with* the
		// service, not in ProgramData — ProgramData holds runtime artefacts).
		await GuestFs.AssertFileExistsAsync(Guest, @"C:\Program Files\VhdxManager\Service\VhdxManager.Service.exe");
		await GuestFs.AssertFileExistsAsync(Guest, @"C:\Program Files\VhdxManager\Service\appsettings.json");
		// CLI binary.
		await GuestFs.AssertFileExistsAsync(Guest, @"C:\Program Files\VhdxManager\Cli\vhmgr.exe");
		// ProgramData layout: logs directory + diagnostics script. We don't
		// assert any specific log file because filenames include the date.
		await GuestFs.AssertDirExistsAsync(Guest, @"C:\ProgramData\VhdxManager\logs");
	}

	[Test]
	public async Task Cli_On_Machine_Path_And_Ping_Succeeds()
	{
		if (!installResult.Succeeded)
			Assert.Inconclusive("MSI install failed.");

		// PATH was just amended by msiexec; a fresh PSSession picks up the
		// updated machine PATH (PSSession spawns a new process which reads
		// the registry fresh). Get-Command resolves Application-type entries.
		var onPath = await GuestFs.IsOnPathAsync(Guest, "vhmgr.exe");
		onPath.Should().BeTrue("the installer is expected to add the CLI directory to the machine PATH");

		// Phase A contract: vhmgr ping exits 0 with non-empty stdout. We
		// don't lock down the exact text yet — that becomes a Phase B test
		// once the output format is stable.
		var ping = await GuestProcess.RunAsync(Guest, "vhmgr.exe", "ping", workingDir: @"C:\");
		ping.Succeeded.Should().BeTrue(
			$"`vhmgr ping` returned {ping.ExitCode}.\nstdout: {ping.StdoutText}\nstderr: {ping.StderrText}");
		ping.StdoutText.Should().NotBeNullOrWhiteSpace(
			"ping should print at least the service version / pipe round-trip confirmation");
	}
}

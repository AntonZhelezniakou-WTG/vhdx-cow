using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Installer;

/// <summary>
/// MajorUpgrade: install version X (via the <c>installed-clean@&lt;sha8&gt;</c>
/// checkpoint), then install version X+1 on top via <c>msiexec /i</c>. WiX's
/// <c>&lt;MajorUpgrade Schedule="afterInstallInitialize"&gt;</c> in
/// <c>Package.wxs</c> detects the older product by <c>UpgradeCode</c> and
/// schedules <c>RemoveExistingProducts</c> in the same MSI transaction, so the
/// observable behaviour from the user's POV is "one msiexec call replaces the
/// old version with the new one".
///
/// <para>The bumped MSI is built on the host in <see cref="OnGuestReadyAsync"/>
/// via <see cref="BumpedMsiBuilder"/> — it produces a fresh MSI whose version
/// is <c>installer/bin/Release/VhdxManager-X.msi</c>'s version with the patch
/// component incremented by one. Total build time ~70-90 s; tests then run in
/// well under a minute.</para>
///
/// <para>The fixture also drops a marker file into <c>C:\ProgramData\VhdxManager\</c>
/// before the upgrade and verifies it survives — ProgramData is outside the
/// MSI's component tree, and MajorUpgrade's uninstall-then-install sequence
/// must not touch arbitrary files there.</para>
/// </summary>
[TestFixture]
[Order(5)]
public sealed class Upgrade_Tests : InstalledFixtureBase
{
	const string MarkerFile = @"C:\ProgramData\VhdxManager\upgrade-marker.txt";
	const string MarkerText = "preserved-across-upgrade";

	BumpedMsi bumpedMsi = null!;
	MsiResult upgradeResult = null!;

	protected override async Task OnGuestReadyAsync()
	{
		// 1) Drop a user-data marker in ProgramData. The folder is owned by the
		//    MSI but the file is ours — it shouldn't be removed by either the
		//    RemoveExistingProducts step or the subsequent install of the new
		//    version. Sanity-check it landed before we proceed.
		await Guest.InvokeVoidAsync(
			$"Set-Content -Path '{MarkerFile}' -Value '{MarkerText}' -Encoding utf8 -Force");
		Assert.That(await GuestFs.ExistsAsync(Guest, MarkerFile), Is.True,
			"pre-upgrade marker file should be writable into ProgramData before the upgrade runs");

		// 2) Build the X+1 MSI on the host. ~70-90 s on a warm cache. The MSI
		//    lands at obj/upgrade-test/msi/VhdxManager-<bumped>.msi, NOT
		//    installer/bin/Release/, so it doesn't fight with MsiArtefact.LoadOrSkip.
		var repoRoot = E2EConfig.FindRepoRoot()!;
		bumpedMsi = await BumpedMsiBuilder.BuildAsync(Msi, repoRoot);

		// 3) Stage the bumped MSI to the guest and fire msiexec /i.
		//    Installing over an older version triggers MajorUpgrade:
		//    FindRelatedProducts → RemoveExistingProducts → install new.
		var guestMsiPath = $@"C:\Setup\{bumpedMsi.FileName}";
		await Guest.CopyToGuestAsync(bumpedMsi.HostPath, guestMsiPath);
		upgradeResult = await MsiInstaller.InstallSilentAsync(Guest, guestMsiPath);

		// ServiceControl Wait="no" — give the SCM a moment to bring the new
		// service version up before tests assert on its state.
		await Guest.InvokeVoidAsync("Start-Sleep -Seconds 5");
	}

	[Test, Order(1)]
	public void Upgrade_Exits_Zero()
	{
		upgradeResult.Succeeded.Should().BeTrue(
			$"msiexec /i {bumpedMsi.FileName} returned exit code {upgradeResult.ExitCode} " +
			$"(expected 0 for a MajorUpgrade from {bumpedMsi.BaseVersion} → {bumpedMsi.BumpedVersion}). " +
			$"Log: {upgradeResult.LogPath}\nTail:\n{upgradeResult.LogTail}");
	}

	[Test, Order(2)]
	public async Task Service_Running_After_Upgrade()
	{
		if (!upgradeResult.Succeeded) Assert.Inconclusive("Upgrade step failed.");
		// ServiceControl Stop="both" tears the old service down during the
		// RemoveExistingProducts phase; Start="install" brings the new one up
		// at the end of the install. Both transitions are wrapped in the same
		// MSI transaction.
		await GuestService.AssertRunningAsync(Guest, "VhdxManagerService");
	}

	[Test, Order(3)]
	public async Task Cli_FileVersion_Reflects_Bumped_Version()
	{
		if (!upgradeResult.Succeeded) Assert.Inconclusive("Upgrade step failed.");

		// The CLI was built with /p:Version=<bumped>, so the EXE's FileVersion
		// is the bumped version padded out to 4 components ("0.2.1" → "0.2.1.0").
		var fileVersion = await Guest.InvokeJsonAsync<string>("""
			(Get-Item 'C:\Program Files\VhdxManager\Cli\vhdx.exe').VersionInfo.FileVersion
			""");
		fileVersion.Should().StartWith(bumpedMsi.BumpedVersion,
			$"vhdx.exe FileVersion should match the upgraded version "+
			$"({bumpedMsi.BumpedVersion}); got '{fileVersion}'. If this fails the upgrade " +
			"didn't actually swap the CLI binary — check the msiexec log.");
	}

	[Test, Order(4)]
	public async Task Service_FileVersion_Reflects_Bumped_Version()
	{
		if (!upgradeResult.Succeeded) Assert.Inconclusive("Upgrade step failed.");

		var fileVersion = await Guest.InvokeJsonAsync<string>("""
			(Get-Item 'C:\Program Files\VhdxManager\Service\VhdxManager.Service.exe').VersionInfo.FileVersion
			""");
		fileVersion.Should().StartWith(bumpedMsi.BumpedVersion,
			$"VhdxManager.Service.exe FileVersion should match the upgraded version " +
			$"({bumpedMsi.BumpedVersion}); got '{fileVersion}'");
	}

	[Test, Order(5)]
	public async Task ProgramData_Marker_Survives_Upgrade()
	{
		if (!upgradeResult.Succeeded)
			Assert.Inconclusive("Upgrade step failed.");

		// Files placed in ProgramData by the user (or by the service at runtime —
		// state.json, logs, etc.) must survive the uninstall-then-install
		// MajorUpgrade sequence. If this fails, user data is being wiped on
		// every version bump and that's a customer-visible regression.
		var exists = await GuestFs.ExistsAsync(Guest, MarkerFile);
		exists.Should().BeTrue(
			$"the marker file at {MarkerFile} (written before upgrade) must " +
			"survive MajorUpgrade — ProgramData is outside the MSI's component tree");

		var content = await GuestFs.ReadAllTextAsync(Guest, MarkerFile);
		content.Should().Contain(MarkerText,
			"marker contents must be intact (not zero-byte / overwritten)");
	}

	[Test, Order(6)]
	public async Task Cli_Still_On_Path_After_Upgrade()
	{
		if (!upgradeResult.Succeeded)
			Assert.Inconclusive("Upgrade step failed.");

		// The PATH entry's CliFolder resolves to the same physical path before
		// and after upgrade, so the registry value should remain stable.
		// A fresh PSSession reads the up-to-date machine PATH from the registry.
		var onPath = await GuestFs.IsOnPathAsync(Guest, "vhdx.exe");
		onPath.Should().BeTrue(
			"upgrade must leave the CLI directory on the machine PATH");
	}

	[Test, Order(7)]
	public async Task Only_One_Vhdx_Manager_Installed_After_Upgrade()
	{
		if (!upgradeResult.Succeeded)
			Assert.Inconclusive("Upgrade step failed.");

		// MajorUpgrade's RemoveExistingProducts uninstalls the old product code
		// before installing the new one, so exactly one VHDX Manager entry
		// should remain in the Add/Remove Programs registry hive. Two entries
		// would indicate the upgrade table didn't match the prior install
		// (typically: UpgradeCode mismatch, or Version comparison broken).
		var count = await Guest.InvokeJsonAsync<int>("""
			$uninstallPaths = @(
			    'HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*',
			    'HKLM:\Software\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
			)
			@(Get-ItemProperty -Path $uninstallPaths -ErrorAction SilentlyContinue |
			  Where-Object { $_.DisplayName -like 'VHDX Manager*' }).Count
			""");
		count.Should().Be(1,
			"after MajorUpgrade exactly one VHDX Manager entry should remain in Add/Remove Programs; " +
			"more than one indicates RemoveExistingProducts didn't match the old version");
	}
}

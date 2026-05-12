namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Base for any fixture that wants to start from "MSI installed, service
/// running, no mounts" — the state captured by the
/// <c>installed-clean@&lt;sha8&gt;</c> checkpoint created in
/// <see cref="Bootstrap.InstalledCleanCheckpointFixture"/>. CLI verb tests
/// in <c>Cli/</c> all derive from this; <c>Installer/Uninstall_Tests</c>
/// also uses it.
///
/// <para>Subclasses don't need to override <see cref="CheckpointName"/> —
/// they get it for free, keyed off the MSI under test. Override
/// <see cref="E2EFixtureBase.OnGuestReadyAsync"/> for per-fixture in-guest
/// setup.</para>
/// </summary>
public abstract class InstalledFixtureBase : E2EFixtureBase
{
	MsiArtefact? _msiCache;
	protected MsiArtefact Msi => _msiCache ??= MsiArtefact.LoadOrSkip(E2EConfig.FindRepoRoot()!);

	string? _checkpointCache;
	protected override string CheckpointName
		// Resolve lazily — the base class accesses this in [OneTimeSetUp] before
		// our overrides have run. MsiArtefact.LoadOrSkip will Assert.Ignore
		// cleanly here if the MSI is missing.
		=> _checkpointCache ??= InstalledCheckpoint.NameFor(Msi);
}

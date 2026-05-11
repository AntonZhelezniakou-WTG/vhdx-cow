namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Naming helpers for the post-install checkpoint. The MSI's SHA-256 is
/// folded into the name so rebuilding the installer (which produces a
/// different binary even at the same version) automatically invalidates
/// the cached snapshot — the next test run rebuilds it.
/// </summary>
public static class InstalledCheckpoint
{
	public static string NameFor(MsiArtefact msi) => $"installed-clean@{msi.Sha8}";

	/// <summary>Guest-absolute path where the MSI is staged before install.</summary>
	public static string GuestMsiPath(MsiArtefact msi) => $@"C:\Setup\{msi.FileName}";
}

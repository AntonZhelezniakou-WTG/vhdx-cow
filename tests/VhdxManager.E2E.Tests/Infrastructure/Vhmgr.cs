namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Convenience wrapper for invoking <c>vhmgr.exe</c> in the guest. Uses the
/// absolute installed path rather than relying on PATH lookup — PATH
/// resolution from a fresh PSSession is sometimes flaky during the first
/// minute or two after MSI install (the registry's machine PATH has been
/// updated but the SYSTEM <c>Environment</c> service hasn't broadcast the
/// change yet), and tests that depend on PATH are already covered in Phase A.
/// </summary>
public static class Vhmgr
{
	public const string ExePath = @"C:\Program Files\VhdxManager\Cli\vhmgr.exe";

	public static Task<ProcResult> RunAsync(
		GuestSession s,
		string verbAndArgs,
		CancellationToken ct = default)
		=> GuestProcess.RunAsync(s, ExePath, verbAndArgs, workingDir: @"C:\", ct: ct);
}

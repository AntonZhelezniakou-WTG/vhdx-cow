using System.Threading;
using System.Threading.Tasks;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Drives <c>msiexec</c> inside the guest. Always uses <c>/qn</c> (silent,
/// no UI) and <c>/l*v</c> verbose logging to a temp file — on failure the
/// log path is returned via stderr so the test can quote it in the
/// assertion message.
/// </summary>
public static class MsiInstaller
{
	/// <summary>
	/// Install the MSI silently. The guest log is left on disk at the
	/// returned path; on failure include it in the assertion's because-clause.
	/// </summary>
	public static Task<MsiResult> InstallSilentAsync(GuestSession s,
		string guestMsiPath, CancellationToken ct = default)
		=> RunMsiExecAsync(s, $"/i \"{guestMsiPath}\"", "install", ct);

	/// <summary>
	/// Uninstall by MSI path (works for unsigned dev MSIs even when the
	/// product code isn't stable across builds). Equivalent to msiexec /x.
	/// </summary>
	public static Task<MsiResult> UninstallSilentAsync(GuestSession s,
		string guestMsiPath, CancellationToken ct = default)
		=> RunMsiExecAsync(s, $"/x \"{guestMsiPath}\"", "uninstall", ct);

	/// <summary>
	/// Repair the installation: restore any missing files (<c>/fp</c> mode).
	/// Stops the service, reinstalls files that are absent, then restarts the
	/// service. Use after intentionally deleting a managed file to verify that
	/// repair brings the installation back to a healthy state.
	/// </summary>
	public static Task<MsiResult> RepairSilentAsync(GuestSession s,
		string guestMsiPath, CancellationToken ct = default)
		=> RunMsiExecAsync(s, $"/fp \"{guestMsiPath}\"", "repair", ct);

	private static async Task<MsiResult> RunMsiExecAsync(GuestSession s,
		string operation, string verb, CancellationToken ct)
	{
		// We can't use GuestProcess.RunAsync directly because msiexec's
		// stdout/stderr are basically empty — it logs everything internally
		// and exits with a code. Provide /l*v so we have something to
		// attach to a failing assertion.
		// NOTE: we build the argument string in PowerShell rather than embedding
		// $log inside a single-quoted literal — single-quoted strings do NOT
		// expand variables, so the previous form silently sent the literal
		// "$log" to msiexec, which wrote the log to a file *named* "$log" in
		// its CWD. The bug was latent because Install/Uninstall/Repair only
		// surface the LogPath/LogTail in failure messages; tests that try to
		// actually read the log file (e.g. Upgrade_Tests) hit the missing path.
		var script = $@"
$log = Join-Path $env:TEMP ('vhmgr-msi-{verb}-' + [Guid]::NewGuid().ToString('N') + '.log')
$argString = '{operation} /qn /norestart /l*v ""' + $log + '""'
$proc = Start-Process -FilePath 'msiexec.exe' `
    -ArgumentList $argString `
    -NoNewWindow -PassThru -Wait
[pscustomobject]@{{
    ExitCode = [int]$proc.ExitCode
    LogPath  = $log
    LogTail  = if (Test-Path -LiteralPath $log) {{
        # Tail the log on failure so the assertion message has something
        # actionable. ~5 KB is enough to capture the last failed action
        # without bloating the test output.
        (Get-Content -LiteralPath $log -Tail 60 -ErrorAction SilentlyContinue) -join ""`n""
    }} else {{ '' }}
}}";
		return await s.InvokeJsonAsync<MsiResult>(script, ct);
	}
}

/// <summary>
/// Result of a single msiexec invocation. <c>LogPath</c> is a guest-absolute
/// path (still present on the guest's filesystem after the call); the
/// <c>LogTail</c> is the last ~60 lines of that log captured at the time of
/// the call, suitable for inclusion in an assertion message without a
/// second round-trip.
/// </summary>
public sealed record MsiResult(int ExitCode, string LogPath, string? LogTail)
{
	public bool Succeeded => ExitCode == 0;
}

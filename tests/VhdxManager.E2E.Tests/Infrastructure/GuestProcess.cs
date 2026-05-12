namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Run an executable inside the guest and capture exit code + stdout + stderr.
/// Uses <c>Start-Process -Wait -PassThru</c> with both standard streams
/// redirected to files (PowerShell Direct doesn't carry child-process streams
/// directly the way <c>Invoke-Command</c> carries cmdlet output).
/// </summary>
/// <summary>
/// Result of a single in-guest process run. Stdout/Stderr default to empty
/// strings when the process produced nothing on that stream, so callers
/// never have to null-check before .Contains / regex matching.
/// </summary>
public sealed record ProcResult(int ExitCode, string? Stdout, string? Stderr)
{
	public string StdoutText => Stdout ?? "";
	public string StderrText => Stderr ?? "";
	public bool   Succeeded  => ExitCode == 0;
}

public static class GuestProcess
{
	/// <param name="exe">Absolute path inside the guest, or a name on PATH.</param>
	/// <param name="args">
	/// Command-line arguments as a single string. Caller is responsible for
	/// quoting — pass it the way you'd type it at <c>cmd.exe</c>.
	/// </param>
	/// <param name="workingDir">
	/// Optional <c>-WorkingDirectory</c>. Default leaves it as wherever
	/// PowerShell decided to drop the user (usually <c>C:\Users\&lt;user&gt;</c>).
	/// </param>
	public static Task<ProcResult> RunAsync(
		GuestSession s,
		string exe,
		string args = "",
		string? workingDir = null,
		CancellationToken ct = default)
	{
		// Stream-redirect to unique temp files per call so concurrent users
		// of the guest (not us, but conceivable) don't clobber each other.
		// The remote script reads both back as -Raw strings and emits a
		// PSCustomObject which Strip-PSRemoting + ConvertTo-Json carry safely.
		var wdParam = workingDir is null
			? ""
			: $"-WorkingDirectory '{Esc(workingDir)}'";

		var script = $$"""
			$stdoutFile = Join-Path $env:TEMP ('vhmgr-stdout-' + [Guid]::NewGuid().ToString('N') + '.log')
			$stderrFile = Join-Path $env:TEMP ('vhmgr-stderr-' + [Guid]::NewGuid().ToString('N') + '.log')
			try {
			    $proc = Start-Process -FilePath '{{Esc(exe)}}' -ArgumentList '{{Esc(args)}}' {{wdParam}} `
			        -NoNewWindow -PassThru -Wait `
			        -RedirectStandardOutput $stdoutFile `
			        -RedirectStandardError  $stderrFile
			    [pscustomobject]@{
			        ExitCode = [int]$proc.ExitCode
			        Stdout   = (Get-Content -LiteralPath $stdoutFile -Raw -ErrorAction SilentlyContinue) -as [string]
			        Stderr   = (Get-Content -LiteralPath $stderrFile -Raw -ErrorAction SilentlyContinue) -as [string]
			    }
			} finally {
			    Remove-Item -LiteralPath $stdoutFile,$stderrFile -ErrorAction SilentlyContinue
			}
			""";
		return s.InvokeJsonAsync<ProcResult>(script, ct);
	}

	static string Esc(string s) => s.Replace("'", "''");
}

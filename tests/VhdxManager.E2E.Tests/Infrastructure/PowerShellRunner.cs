using System.Diagnostics;
using System.Text;
using System.Text.Json;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Spawns Windows PowerShell 5.1 to run scripts and round-trips results
/// as JSON. We deliberately use the legacy <c>powershell.exe</c> rather than
/// PowerShell 7's <c>pwsh.exe</c> or the in-process <c>Microsoft.PowerShell.SDK</c>
/// for three reasons:
/// <list type="bullet">
///   <item><see cref="System.Management.Automation"/> via the SDK pulls ~70 MB
///         of assemblies and is brittle to host-PS-version drift.</item>
///   <item><c>Invoke-Command -VMName</c> (PowerShell Direct) is a Windows
///         PowerShell feature; pwsh 7 supports it but the
///         <c>tests/e2e/lib/Helpers.ps1</c> stack already targets 5.1.</item>
///   <item>JSON over stdout sidesteps CLIXML's quirks (especially around
///         <c>$null</c> and empty collections) and keeps the C# side
///         strongly typed via <see cref="System.Text.Json"/>.</item>
/// </list>
///
/// Every call writes a small wrapper script that:
/// <list type="number">
///   <item>Sets <c>$ErrorActionPreference='Stop'</c>.</item>
///   <item>Dot-sources <c>tests/e2e/lib/Helpers.ps1</c> if a path was given.</item>
///   <item>Runs the user-supplied script body inside a <c>try</c>/<c>catch</c>.</item>
///   <item>Emits a single JSON line on success or a JSON error envelope on
///         failure, so the C# caller never has to parse free-form stderr to
///         tell apart "script wrote nothing" from "script blew up".</item>
/// </list>
/// </summary>
public sealed class PowerShellRunner
{
	// The CLR's startup time for powershell.exe is ~300ms; making this a static
	// path resolver (rather than per-invocation) keeps cold tests snappier.
	static readonly string PowerShellExe = ResolvePowerShellExe();

	readonly string? helpersScriptPath;

	/// <param name="helpersScriptPath">
	/// Optional absolute path to <c>tests/e2e/lib/Helpers.ps1</c>. If supplied,
	/// every script body is preceded by <c>. '&lt;path&gt;'</c> so helpers like
	/// <c>Invoke-InGuest</c> / <c>Wait-VmReady</c> are in scope.
	/// </param>
	public PowerShellRunner(string? helpersScriptPath = null)
	{
		if (helpersScriptPath is not null && !File.Exists(helpersScriptPath))
		{
			throw new FileNotFoundException(
				$"Helpers script not found at: {helpersScriptPath}",
				helpersScriptPath);
		}
		this.helpersScriptPath = helpersScriptPath;
	}

	/// <summary>Returns the raw stdout captured from PowerShell (post-wrapper).</summary>
	public async Task<string> RunRawAsync(string script, CancellationToken ct = default)
	{
		var result = await RunInternalAsync(script, captureForJson: false, ct).ConfigureAwait(false);
		return result.Stdout;
	}

	/// <summary>
	/// Runs the script and deserializes its last JSON output into
	/// <typeparamref name="T"/>. The wrapper appends
	/// <c>| ConvertTo-Json -Depth 8 -Compress</c> automatically, so the script
	/// body should evaluate to the object(s) you want returned.
	/// </summary>
	public async Task<T> RunJsonAsync<T>(string script, CancellationToken ct = default)
	{
		var result = await RunInternalAsync(script, captureForJson: true, ct).ConfigureAwait(false);
		var payload = result.Stdout.Trim();
		if (payload.Length == 0)
		{
			throw new InvalidOperationException(
				$"PowerShell produced no JSON output for script:\n{script}\n" +
				$"stderr: {result.Stderr}");
		}
		try
		{
			return JsonSerializer.Deserialize<T>(payload, JsonOpts)!;
		}
		catch (JsonException ex)
		{
			throw new InvalidOperationException(
				$"Failed to deserialize PowerShell output as {typeof(T).Name}.\n" +
				$"Payload: {payload}\nstderr: {result.Stderr}", ex);
		}
	}

	/// <summary>Runs the script for side effects only. Throws if it errors.</summary>
	public async Task RunVoidAsync(string script, CancellationToken ct = default)
	{
		_ = await RunInternalAsync(script, captureForJson: false, ct).ConfigureAwait(false);
	}

	static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	async Task<(string Stdout, string Stderr)> RunInternalAsync(
		string script, bool captureForJson, CancellationToken ct)
	{
		var wrapped = WrapScript(script, captureForJson);

		// We tried <c>-Command -</c> with stdin redirection first; PowerShell
		// 5.1 sometimes swallowed the stream silently (zero stdout, exit 0).
		// Writing the wrapper to a temp .ps1 and invoking via <c>-File</c> is
		// the documented happy-path for "run this script and give me back its
		// output", with the only downside being an extra file write.
		var scriptFile = Path.Combine(Path.GetTempPath(),
			$"vhmgr-e2e-{Guid.NewGuid():N}.ps1");
		// UTF-8 *with* BOM so Windows PowerShell 5.1 unambiguously detects
		// the encoding (without the BOM 5.1 falls back to the system ANSI
		// codepage on multi-byte characters).
		await File.WriteAllTextAsync(scriptFile, wrapped, new UTF8Encoding(true), ct)
			.ConfigureAwait(false);

		try
		{
			var psi = new ProcessStartInfo
			{
				FileName = PowerShellExe,
				// -NoProfile        : ignore the user's $PROFILE (~150ms speedup + reproducibility)
				// -NonInteractive   : refuse to prompt for input
				// -ExecutionPolicy  : Helpers.ps1 isn't signed; tests must not be blocked by host policy
				// -File             : execute the temp script directly
				Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -File \"{scriptFile}\"",
				RedirectStandardOutput = true,
				RedirectStandardError  = true,
				UseShellExecute        = false,
				CreateNoWindow         = true,
				StandardOutputEncoding = Encoding.UTF8,
				StandardErrorEncoding  = Encoding.UTF8,
			};

			using var proc = new Process { StartInfo = psi };
			var stdout = new StringBuilder();
			var stderr = new StringBuilder();
			proc.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
			proc.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };

			if (!proc.Start())
			{
				throw new InvalidOperationException($"Failed to start {PowerShellExe}.");
			}
			proc.BeginOutputReadLine();
			proc.BeginErrorReadLine();

			await proc.WaitForExitAsync(ct).ConfigureAwait(false);
			// Drain the async output readers — WaitForExitAsync can return
			// before the very last OutputDataReceived events have been delivered.
			proc.WaitForExit();

			var stdoutText = stdout.ToString();
			var stderrText = stderr.ToString();

			if (proc.ExitCode != 0)
			{
				throw new PowerShellInvocationException(proc.ExitCode, script, stdoutText, stderrText);
			}

			// Our wrapper script emits an explicit error sentinel even if the
			// inner script's exception type is one PS converts to a non-terminating
			// error (exit code 0 + sentinel on stdout).
			if (captureForJson && stdoutText.Contains("\"__pwshError\":"))
			{
				throw new PowerShellInvocationException(-1, script, stdoutText, stderrText);
			}

			return (stdoutText, stderrText);
		}
		finally
		{
			try { File.Delete(scriptFile); } catch { /* best-effort cleanup */ }
		}
	}

	string WrapScript(string body, bool emitJson)
	{
		// Inner script runs inside try/catch so a single line failure produces
		// a structured error rather than a process-wide non-zero exit (which
		// is hard to attribute back to the offending statement).
		var sb = new StringBuilder();
		sb.AppendLine("$ErrorActionPreference = 'Stop'");
		sb.AppendLine("$ProgressPreference = 'SilentlyContinue'");
		if (helpersScriptPath is not null)
		{
			sb.Append(". '").Append(helpersScriptPath.Replace("'", "''")).AppendLine("'");
		}
		sb.AppendLine("try {");
		// Wrap user body in a script block so its `return` semantics don't
		// short-circuit our trailing emit.
		sb.AppendLine("    $__pwshResult = & {");
		sb.AppendLine(body);
		sb.AppendLine("    }");
		sb.AppendLine(emitJson
			// Force the result through ConvertTo-Json. -Depth 8 covers the
			// nested objects we hand around (snapshot lists, service info
			// hashtables); -Compress keeps the wire small.
			// Emit the literal JSON keyword "null" (4 chars, no quotes) so
			// C# can deserialize as default(T) for nullable reference types.
			? "    if ($null -eq $__pwshResult) { 'null' | Write-Output } else { $__pwshResult | ConvertTo-Json -Depth 8 -Compress }"

			// Raw mode — write the script's result objects to host stdout via
			// the default formatter (Out-String). Otherwise the result lives
			// in $__pwshResult and is never visible to the C# caller.
			: "    if ($null -ne $__pwshResult) { $__pwshResult | Out-String -Stream }");

		sb.AppendLine("} catch {");
		// Emit a JSON error envelope to stdout so RunJsonAsync can detect it
		// even when PowerShell decides to set exit code 0.
		sb.AppendLine("    $err = @{ '__pwshError' = $true; Message = $_.Exception.Message; ScriptStackTrace = $_.ScriptStackTrace }");
		sb.AppendLine("    $err | ConvertTo-Json -Depth 4 -Compress");
		sb.AppendLine("    exit 1");
		sb.AppendLine("}");
		return sb.ToString();
	}

	static string ResolvePowerShellExe()
	{
		// Always prefer the 64-bit Windows PowerShell. On a 64-bit test host
		// %WINDIR%\System32 *is* the 64-bit copy (32-bit lives in SysWOW64).
		var sysRoot = Environment.GetFolderPath(Environment.SpecialFolder.System);
		var ps51 = Path.Combine(sysRoot, "WindowsPowerShell", "v1.0", "powershell.exe");

		// Fallback: PATH lookup. If even powershell.exe isn't on PATH the
		// developer has bigger problems than these tests.
		return File.Exists(ps51) ? ps51 : "powershell.exe";
	}
}

/// <summary>
/// Thrown when the PowerShell host returns non-zero or our wrapper emits
/// the error sentinel. Carries the original script (already wrapped — useful
/// to copy/paste into a PS window) and both std streams so a test failure
/// message is actionable.
/// </summary>
public sealed class PowerShellInvocationException(int exitCode, string script, string stdout, string stderr)
	: Exception(BuildMessage(exitCode, script, stdout, stderr))
{
	public int    ExitCode { get; } = exitCode;
	public string Script   { get; } = script;
	public string Stdout   { get; } = stdout;
	public string Stderr   { get; } = stderr;

	static string BuildMessage(int exitCode, string script, string stdout, string stderr)
	{
		var sb = new StringBuilder();
		sb.AppendLine($"PowerShell exited with code {exitCode}.");
		sb.AppendLine("--- script ---");
		sb.AppendLine(Trim(script, 2_000));
		sb.AppendLine("--- stdout ---");
		sb.AppendLine(Trim(stdout, 2_000));
		sb.AppendLine("--- stderr ---");
		sb.AppendLine(Trim(stderr, 2_000));
		return sb.ToString();

		// Truncate massive outputs so a single failure doesn't drown the test
		// runner's console. Anyone needing the full payload can grab .Stdout /
		// .Stderr off the exception.
		static string Trim(string s, int cap)
			=> s.Length <= cap ? s : s[..cap] + $"… (+{s.Length - cap} more chars)";
	}
}

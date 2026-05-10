using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Text;
using VhdxManager.Service.Native;

namespace VhdxManager.Service.VhdxOperations;

/// <summary>
/// PowerShell-based implementation of <see cref="IDefenderExclusionManager"/>.
/// Mirrors the <c>Win32DiskInitializer</c> shell-out pattern (process group, redirected
/// stderr, fixed timeout) for consistency.
///
/// Group-policy / access-denied failures are detected by inspecting stderr and
/// re-thrown as <see cref="DefenderPolicyBlockedException"/> with a clean message.
/// </summary>
[SupportedOSPlatform("windows")]
[SuppressMessage("Interoperability", "CA1416", Justification = "Service is windows-only (net10.0-windows).")]
public sealed class DefenderExclusionManager(ILogger<DefenderExclusionManager> logger)
	: IDefenderExclusionManager
{
	public Task AddExclusionAsync(string vhdxPath, CancellationToken ct)
		=> Task.Run(() => AddExclusionCore(vhdxPath, ct), ct);

	void AddExclusionCore(string vhdxPath, CancellationToken ct)
	{
		if (string.IsNullOrWhiteSpace(vhdxPath))
		{
			throw new ArgumentException("VHDX path required.", nameof(vhdxPath));
		}

		// Always use the absolute path; Defender requires fully qualified paths
		// (relative paths are silently ignored / interpreted against the service's
		// working directory which is not what the caller wants).
		var fullPath = Path.GetFullPath(vhdxPath);

		// Single-quote escape for PowerShell string literal: ' -> ''.
		var quoted = fullPath.Replace("'", "''", StringComparison.Ordinal);
		var script =
			$"$ErrorActionPreference='Stop'; Add-MpPreference -ExclusionPath '{quoted}'";

		var psi = new ProcessStartInfo
		{
			FileName = "powershell.exe",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-NoProfile");
		psi.ArgumentList.Add("-NonInteractive");
		psi.ArgumentList.Add("-ExecutionPolicy");
		psi.ArgumentList.Add("Bypass");
		psi.ArgumentList.Add("-Command");
		psi.ArgumentList.Add(script);

		using var processGroup = new ProcessGroup();
		using var process = processGroup.Start(psi);

		var stdoutBuf = new StringBuilder();
		var stderrBuf = new StringBuilder();
		process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
		process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		// Add-MpPreference is normally instant; allow 60s for slow Defender services.
		if (!process.WaitForExit(60_000))
		{
			try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
			throw new InvalidOperationException("Add-MpPreference timed out after 60 seconds.");
		}

		ct.ThrowIfCancellationRequested();

		if (process.ExitCode == 0)
		{
			logger.LogInformation("Defender exclusion added for {Path}", fullPath);
			return;
		}

		var stderr = stderrBuf.ToString().Trim();
		logger.LogWarning(
			"Add-MpPreference failed for {Path} (exit {ExitCode}): {Stderr}",
			fullPath, process.ExitCode, stderr);

		if (IsPolicyBlocked(stderr))
		{
			throw new DefenderPolicyBlockedException(
				"Adding the Defender exclusion was blocked by Windows policy " +
				"(Group Policy or tamper protection). The VHDX was created successfully; " +
				"please ask your administrator to add the exclusion manually if needed.");
		}

		throw new InvalidOperationException(
			$"Add-MpPreference failed (exit {process.ExitCode}). " +
			(stderr.Length == 0 ? "(no stderr)" : stderr));
	}

	/// <summary>
	/// Heuristic: PowerShell errors raised when Defender refuses an exclusion
	/// because of policy include one of these tokens. Tested against:
	/// <list type="bullet">
	/// <item>HRESULT 0x800704EC ("This program is blocked by group policy")</item>
	/// <item>"Operation failed with the following error: 0x800106ba" (tamper protection)</item>
	/// <item>"Access is denied" (RunAsUser without WD MAPS rights)</item>
	/// </list>
	/// </summary>
	internal static bool IsPolicyBlocked(string stderr)
	{
		if (string.IsNullOrEmpty(stderr))
		{
			return false;
		}
		return stderr.Contains("0x800704EC", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("0x800106ba", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("blocked by group policy", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("Access is denied", StringComparison.OrdinalIgnoreCase)
			|| stderr.Contains("tamper protection", StringComparison.OrdinalIgnoreCase);
	}
}

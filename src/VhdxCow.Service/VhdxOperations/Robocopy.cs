using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace VhdxCow.Service.VhdxOperations;

/// <summary>
/// Thin wrapper around <c>robocopy.exe</c> for the Convert workflow.
/// We mirror a staging directory into a freshly mounted VHDX folder; this
/// preserves ACLs/timestamps/symlinks and tolerates locked files.
/// </summary>
public sealed class Robocopy(ILogger<Robocopy> logger)
{
	public sealed record Result(
		int ExitCode,
		long FilesCopied,
		long BytesCopied,
		IReadOnlyList<string> Errors)
	{
		// Robocopy exit codes: 0–7 are success/info, 8+ are real failures.
		public bool IsSuccess => ExitCode < 8;
	}

	public Task<Result> MirrorAsync(string source, string destination, CancellationToken ct)
		=> Task.Run(() => MirrorCore(source, destination, ct), ct);

	Result MirrorCore(string source, string destination, CancellationToken ct)
	{
		logger.LogInformation("Robocopy mirror: {Source} -> {Destination}", source, destination);

		var psi = new ProcessStartInfo
		{
			FileName = "robocopy.exe",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add(source);
		psi.ArgumentList.Add(destination);
		psi.ArgumentList.Add("/MIR");        // mirror tree (incl. delete extras at destination)
		psi.ArgumentList.Add("/COPY:DATSO"); // data, attributes, timestamps, security, owner
		psi.ArgumentList.Add("/SECFIX");     // fix security on existing files
		psi.ArgumentList.Add("/TIMFIX");     // fix timestamps
		psi.ArgumentList.Add("/R:1");        // 1 retry on a file error
		psi.ArgumentList.Add("/W:0");        // 0-second wait between retries
		psi.ArgumentList.Add("/NP");         // no per-file progress %
		psi.ArgumentList.Add("/NFL");        // no per-file list
		psi.ArgumentList.Add("/NDL");        // no per-dir list
		psi.ArgumentList.Add("/NJH");        // no job header (cleaner output)

		using var process = Process.Start(psi)
			?? throw new InvalidOperationException("Failed to start robocopy.exe.");

		var stdout = new System.Text.StringBuilder();
		var errors = new List<string>();

		process.OutputDataReceived += (_, e) =>
		{
			if (e.Data is null) return;
			stdout.AppendLine(e.Data);
			if (e.Data.Contains("ERROR ", StringComparison.Ordinal))
			{
				errors.Add(e.Data.Trim());
				logger.LogWarning("Robocopy error: {Line}", e.Data.Trim());
			}
		};
		process.ErrorDataReceived += (_, e) =>
		{
			if (!string.IsNullOrEmpty(e.Data))
			{
				errors.Add(e.Data!.Trim());
				logger.LogWarning("Robocopy stderr: {Line}", e.Data!.Trim());
			}
		};

		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		try
		{
			process.WaitForExit();
		}
		catch (OperationCanceledException) when (ct.IsCancellationRequested)
		{
			try
			{
				process.Kill(entireProcessTree: true);
			}
			catch
			{
				// ignored
			}

			throw;
		}

		var (filesCopied, bytesCopied) = ParseSummary(stdout.ToString());
		var exitCode = process.ExitCode;

		logger.LogInformation(
			"Robocopy finished: ExitCode={ExitCode}, FilesCopied={Files}, BytesCopied={Bytes}, Errors={ErrorCount}",
				exitCode, filesCopied, bytesCopied, errors.Count);

		return new Result(exitCode, filesCopied, bytesCopied, errors);
	}

	// Parses robocopy's final summary block. Lines look like:
	//   "    Files :         1247         1247            0            0            0            0"
	//   "    Bytes :    402.45 m    402.45 m            0            0            0            0"
	// We pull "Total" (column 1).
	static (long Files, long Bytes) ParseSummary(string output)
	{
		var filesLine = Regex.Match(output, @"^\s*Files\s*:\s*(\d+)", RegexOptions.Multiline);
		var bytesLine = Regex.Match(
			output,
			@"^\s*Bytes\s*:\s*([\d.,]+(?:\s*[kmgtKMGT])?)",
			RegexOptions.Multiline);

		long files = 0;
		if (filesLine.Success && long.TryParse(filesLine.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var f))
		{
			files = f;
		}

		long bytes = 0;
		if (bytesLine.Success)
		{
			bytes = ParseRobocopyBytes(bytesLine.Groups[1].Value);
		}

		return (files, bytes);
	}

	static long ParseRobocopyBytes(string text)
	{
		text = text.Trim();
		if (text.Length == 0) return 0;

		// Robocopy emits numbers like "402.45 m", "1.23 g", or just "402".
		var unit = text[^1];
		var multiplier = char.ToLowerInvariant(unit) switch
		{
			'k' => 1024L,
			'm' => 1024L * 1024,
			'g' => 1024L * 1024 * 1024,
			't' => 1024L * 1024 * 1024 * 1024,
			_ => 1L,
		};
		var numericPart = char.IsLetter(unit) ? text[..^1].Trim() : text;
		return double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
			? (long)(value * multiplier)
			: 0;
	}
}

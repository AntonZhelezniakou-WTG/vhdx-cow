using System.Diagnostics;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using VhdxManager.Contracts;

namespace VhdxManager.Service.Diagnostics;

/// <summary>
/// Writes a self-contained fatal-startup report to disk and the Windows Event Log,
/// independent of any logging framework state. Designed for the catch-all in <c>Program.cs</c>:
/// the host may have failed before Serilog finished configuring sinks, so this code path
/// must not depend on Serilog or DI.
/// </summary>
public static class FatalDiagnostics
{
	const int FatalEventId = 1000;

	/// <summary>
	/// Writes a fatal report and returns the path of the report file (or null if writing failed).
	/// All operations are best-effort and never throw.
	/// </summary>
	public static string? Report(Exception exception)
	{
		var report = BuildReport(exception);
		var reportPath = TryWriteReportFile(report);
		TryWriteEventLog(report, reportPath);
		return reportPath;
	}

	static string BuildReport(Exception exception)
	{
		var sb = new StringBuilder();
		sb.AppendLine("VhdxManager Service — fatal startup failure");
		sb.AppendLine("========================================");
		sb.AppendLine(CultureInfo.InvariantCulture, $"Timestamp (UTC):    {DateTime.UtcNow:O}");
		sb.AppendLine(CultureInfo.InvariantCulture, $"Service name:       {ServiceConstants.ServiceName}");
		sb.AppendLine(CultureInfo.InvariantCulture, $"OS version:         {Environment.OSVersion}");
		sb.AppendLine(CultureInfo.InvariantCulture, $".NET version:       {Environment.Version}");
		sb.AppendLine(CultureInfo.InvariantCulture, $"Process path:       {Environment.ProcessPath}");
		sb.AppendLine(CultureInfo.InvariantCulture, $"Working directory:  {Environment.CurrentDirectory}");
		sb.AppendLine(CultureInfo.InvariantCulture, $"User:               {Environment.UserDomainName}\\{Environment.UserName}");
		sb.AppendLine(CultureInfo.InvariantCulture, $"Interactive:        {Environment.UserInteractive}");
		sb.AppendLine();
		sb.AppendLine("Exception:");
		sb.AppendLine(exception.ToString());
		sb.AppendLine();
		sb.AppendLine("Please send this file to the VhdxManager maintainers (open an issue with this report attached).");
		return sb.ToString();
	}

	static string? TryWriteReportFile(string report)
	{
		try
		{
			var dir = Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				"VhdxManager",
				"logs");
			Directory.CreateDirectory(dir);
			var fileName = $"fatal-{DateTime.UtcNow:yyyyMMdd-HHmmss}.log";
			var path = Path.Combine(dir, fileName);
			File.WriteAllText(path, report);
			return path;
		}
		catch
		{
			return null;
		}
	}

	[SupportedOSPlatform("windows")]
	static void TryWriteEventLog(string report, string? reportPath)
	{
		var message = reportPath is null
			? report
			: $"Fatal report written to: {reportPath}{Environment.NewLine}{Environment.NewLine}{report}";

		// Truncate to Event Log limit (~32 KB safe ceiling).
		const int eventLogLimit = 30_000;
		if (message.Length > eventLogLimit)
		{
			message = string.Concat(message.AsSpan(0, eventLogLimit), "…[truncated]");
		}

		// Skip EventLog.SourceExists — it scans all logs including Security and throws under
		// non-admin tokens. Try the dedicated source first; fall back to the always-writable
		// "Application" source. WriteEntry creates the source on first use when allowed.
		if (TryWriteToSource("VhdxManager", message))
		{
			return;
		}

		_ = TryWriteToSource("Application", $"[VhdxManager] {message}");
	}

	[SupportedOSPlatform("windows")]
	static bool TryWriteToSource(string source, string message)
	{
		try
		{
			EventLog.WriteEntry(source, message, EventLogEntryType.Error, FatalEventId);
			return true;
		}
		catch
		{
			return false;
		}
	}
}

using System.CommandLine;
using System.Diagnostics.Eventing.Reader;
using System.Globalization;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using VhdxCow.Contracts;

namespace VhdxCow.Cli;

/// <summary>
/// `vhmgr logs` — collects Windows Event Log entries related to the VhdxCow service
/// (System log: Service Control Manager events; Application log: VhdxCow / .NET Runtime
/// / Application Error events) since a given start time, and writes them to stdout or a file.
///
/// This is a client-side reader — it does NOT contact the service over the named pipe,
/// so it works even when the service is failing to start (which is exactly when the user
/// most needs the logs).
/// </summary>
[SupportedOSPlatform("windows")]
static class LogsCommand
{
	const string SystemLogName = "System";
	const string ApplicationLogName = "Application";
	const string ScmProvider = "Service Control Manager";

	static readonly string[] ApplicationProvidersOfInterest =
	[
		"VhdxCow",
		".NET Runtime",
		"Application Error",
		"Application Hang",
	];

	public static Command Create()
	{
		var sinceOption = new Option<string>("--since")
		{
			Description =
				"Start time. Accepted forms: " +
				"'install' (since the service binary's install time, default), " +
				"a duration like '15m', '2h', '3d', " +
				"or an ISO datetime like '2026-05-07T12:00:00'.",
			DefaultValueFactory = _ => "install",
		};

		var outputOption = new Option<string?>("--output")
		{
			Description = "Write to file (default: stdout).",
		};

		var command = new Command("logs", "Collect Windows Event Log entries related to the service")
		{
			Options = { sinceOption, outputOption },
		};

		command.SetAction((parseResult, _) =>
		{
			var since = parseResult.GetValue(sinceOption)!;
			var output = parseResult.GetValue(outputOption);
			return Task.FromResult(Execute(since, output));
		});

		return command;
	}

	static int Execute(string since, string? outputPath)
	{
		DateTime startUtc;
		try
		{
			startUtc = ResolveStart(since).ToUniversalTime();
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Failed to resolve --since '{since}': {ex.Message}");
			return 1;
		}

		var events = new List<CollectedEvent>();
		var collectionErrors = new List<string>();

		try
		{
			events.AddRange(ReadSystemScmEvents(startUtc));
		}
		catch (Exception ex)
		{
			collectionErrors.Add($"System log (Service Control Manager): {ex.Message}");
		}

		try
		{
			events.AddRange(ReadApplicationEvents(startUtc));
		}
		catch (Exception ex)
		{
			collectionErrors.Add($"Application log: {ex.Message}");
		}

		events.Sort(static (a, b) => a.TimeCreatedUtc.CompareTo(b.TimeCreatedUtc));

		var report = BuildReport(startUtc, events, collectionErrors);

		try
		{
			if (string.IsNullOrEmpty(outputPath))
			{
				Console.Out.Write(report);
			}
			else
			{
				File.WriteAllText(outputPath, report);
				Console.WriteLine($"Wrote {events.Count} event(s) to {outputPath}");
			}
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Failed to write output: {ex.Message}");
			return 1;
		}

		// Surface collection errors in the exit code so scripted callers notice.
		return collectionErrors.Count > 0 ? 1 : 0;
	}

	static DateTime ResolveStart(string since)
	{
		since = since.Trim();

		if (string.Equals(since, "install", StringComparison.OrdinalIgnoreCase))
		{
			return ResolveServiceInstallTime();
		}

		// Duration: e.g. 15m, 2h, 3d, 30s
		if (TryParseDuration(since, out var duration))
		{
			return DateTime.UtcNow - duration;
		}

		// Absolute datetime
		if (DateTime.TryParse(
				since,
				CultureInfo.InvariantCulture,
				DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal,
				out var absolute))
		{
			return absolute;
		}

		throw new FormatException(
			"expected 'install', duration like '15m'/'2h'/'3d', or ISO datetime");
	}

	static bool TryParseDuration(string text, out TimeSpan duration)
	{
		duration = default;
		if (text.Length < 2) return false;
		var unit = text[^1];
		if (!double.TryParse(
				text[..^1],
				NumberStyles.Float,
				CultureInfo.InvariantCulture,
				out var value))
		{
			return false;
		}

		duration = unit switch
		{
			's' or 'S' => TimeSpan.FromSeconds(value),
			'm' or 'M' => TimeSpan.FromMinutes(value),
			'h' or 'H' => TimeSpan.FromHours(value),
			'd' or 'D' => TimeSpan.FromDays(value),
			_ => TimeSpan.Zero,
		};
		return duration != TimeSpan.Zero;
	}

	static DateTime ResolveServiceInstallTime()
	{
		// The service has no install timestamp anywhere obvious. We use the
		// creation time of its EXE file (set by MSI when files are extracted),
		// which is a reliable proxy.
		var imagePath = TryGetServiceImagePath();
		if (imagePath is not null && File.Exists(imagePath))
		{
			return File.GetCreationTimeUtc(imagePath);
		}

		// Fallback: 24 hours ago. Better than throwing.
		return DateTime.UtcNow.AddDays(-1);
	}

	static string? TryGetServiceImagePath()
	{
		try
		{
			using var key = Registry.LocalMachine.OpenSubKey(
				$@"SYSTEM\CurrentControlSet\Services\{ServiceConstants.ServiceName}");
			if (key?.GetValue("ImagePath") is not string raw)
			{
				return null;
			}

			// ImagePath may be quoted and may include arguments. Extract the EXE.
			raw = raw.Trim();
			if (raw.StartsWith('"'))
			{
				var end = raw.IndexOf('"', 1);
				return end > 0 ? raw[1..end] : null;
			}

			var space = raw.IndexOf(' ');
			return space > 0 ? raw[..space] : raw;
		}
		catch
		{
			return null;
		}
	}

	static IEnumerable<CollectedEvent> ReadSystemScmEvents(DateTime startUtc)
	{
		// XPath filter — Windows interprets SystemTime as UTC in 'O' format.
		var xpath =
			$"*[System[Provider[@Name='{ScmProvider}'] " +
			$"and TimeCreated[@SystemTime>='{startUtc:yyyy-MM-ddTHH:mm:ss.fffffffZ}']]]";

		var query = new EventLogQuery(SystemLogName, PathType.LogName, xpath);
		using var reader = new EventLogReader(query);

		while (reader.ReadEvent() is { } record)
		{
			using (record)
			{
				var message = SafeFormat(record);
				// SCM logs many service events; keep only ones that mention us.
				if (message is null || !MentionsService(message))
				{
					continue;
				}

				yield return ToCollectedEvent(SystemLogName, record, message);
			}
		}
	}

	static IEnumerable<CollectedEvent> ReadApplicationEvents(DateTime startUtc)
	{
		// One XPath per provider keeps the per-query result set small and lets us
		// surface a more useful error if a single provider's read fails.
		foreach (var provider in ApplicationProvidersOfInterest)
		{
			List<CollectedEvent>? batch = null;
			try
			{
				batch = ReadApplicationEventsForProvider(provider, startUtc).ToList();
			}
			catch (EventLogNotFoundException)
			{
				// Application log always exists, but a provider may not be registered.
				continue;
			}

			if (batch is not null)
			{
				foreach (var ev in batch) yield return ev;
			}
		}
	}

	static IEnumerable<CollectedEvent> ReadApplicationEventsForProvider(string provider, DateTime startUtc)
	{
		var xpath =
			$"*[System[Provider[@Name='{provider}'] " +
			$"and TimeCreated[@SystemTime>='{startUtc:yyyy-MM-ddTHH:mm:ss.fffffffZ}']]]";

		var query = new EventLogQuery(ApplicationLogName, PathType.LogName, xpath);
		using var reader = new EventLogReader(query);

		while (reader.ReadEvent() is { } record)
		{
			using (record)
			{
				var message = SafeFormat(record);

				// For non-VhdxCow providers (.NET Runtime, Application Error, etc.) only
				// keep entries that actually reference our service/binary; otherwise the
				// log would drown in unrelated noise.
				if (!string.Equals(provider, "VhdxCow", StringComparison.Ordinal))
				{
					if (message is null || !MentionsService(message)) continue;
				}

				yield return ToCollectedEvent(ApplicationLogName, record, message);
			}
		}
	}

	static bool MentionsService(string message) =>
		message.Contains(ServiceConstants.ServiceName, StringComparison.OrdinalIgnoreCase)
		|| message.Contains("VhdxCow", StringComparison.OrdinalIgnoreCase);

	static string? SafeFormat(EventRecord record)
	{
		try
		{
			return record.FormatDescription();
		}
		catch
		{
			// Some events miss their message resource DLL — return what we can.
			return record.Properties is { Count: > 0 }
				? string.Join(" | ", record.Properties.Select(p => p.Value?.ToString() ?? string.Empty))
				: null;
		}
	}

	static CollectedEvent ToCollectedEvent(string logName, EventRecord record, string? message) =>
		new(
			logName,
			record.ProviderName ?? "(unknown)",
			record.Id,
			record.LevelDisplayName ?? record.Level?.ToString() ?? "(unknown)",
			(record.TimeCreated ?? DateTime.UtcNow).ToUniversalTime(),
			message ?? string.Empty);

	static string BuildReport(
		DateTime startUtc,
		IReadOnlyList<CollectedEvent> events,
		IReadOnlyList<string> collectionErrors)
	{
		var sb = new StringBuilder();
		sb.Append(CultureInfo.InvariantCulture, $"VhdxCow service event log — collected {DateTime.UtcNow:O}");
		sb.AppendLine();
		sb.Append(CultureInfo.InvariantCulture, $"Start (UTC):     {startUtc:O}");
		sb.AppendLine();
		sb.Append(CultureInfo.InvariantCulture, $"Service:         {ServiceConstants.ServiceName}");
		sb.AppendLine();
		sb.Append(CultureInfo.InvariantCulture, $"Events found:    {events.Count}");
		sb.AppendLine();
		sb.AppendLine(new string('=', 72));

		if (collectionErrors.Count > 0)
		{
			sb.AppendLine();
			sb.AppendLine("Collection errors (some sources could not be read):");
			foreach (var err in collectionErrors)
			{
				sb.Append("  - ").AppendLine(err);
			}
			sb.AppendLine(new string('-', 72));
		}

		if (events.Count == 0)
		{
			sb.AppendLine();
			sb.AppendLine("(no matching events)");
			return sb.ToString();
		}

		foreach (var ev in events)
		{
			sb.AppendLine();
			sb.Append(CultureInfo.InvariantCulture,
				$"[{ev.TimeCreatedUtc:O}] {ev.LogName} | {ev.ProviderName} | EventId={ev.EventId} | {ev.Level}");
			sb.AppendLine();
			sb.AppendLine(ev.Message);
			sb.AppendLine(new string('-', 72));
		}

		return sb.ToString();
	}

	readonly record struct CollectedEvent(
		string LogName,
		string ProviderName,
		int EventId,
		string Level,
		DateTime TimeCreatedUtc,
		string Message);
}

using System.CommandLine;
using Spectre.Console;
using VhdxManager.Client;

namespace VhdxManager.Cli;

/// <summary>
/// `vhdx config` — read/write service-side defaults persisted in the
/// service's appsettings.json. Currently, exposes one key:
/// <c>add-defender-exclusion</c> (tri-state: true / false / unset).
/// </summary>
static class ConfigCommand
{
	// Single user-visible key. Adding a new key means: extend the proto with
	// another tri-state pair, extend ServiceSettingsStore, and add a case here.
	const string AddDefenderExclusionKey = "add-defender-exclusion";

	public static Command Build(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory)
	{
		var command = new Command("config", "Read or write service-side defaults.");

		command.Subcommands.Add(BuildShow(pipeNameOption, timeoutOption, clientFactory));
		command.Subcommands.Add(BuildGet(pipeNameOption, timeoutOption, clientFactory));
		command.Subcommands.Add(BuildSet(pipeNameOption, timeoutOption, clientFactory));

		return command;
	}

	static Command BuildShow(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory)
	{
		var cmd = new Command("show", "Print all service-side default settings.");
		cmd.SetAction(async (parseResult, ct) =>
		{
			var (pipeName, timeout) = ReadGlobals(parseResult, pipeNameOption, timeoutOption);
			using var client = clientFactory(pipeName, timeout);
			try
			{
				var reply = await client.GetSettingsAsync(ct);
				AnsiConsole.MarkupLine($"[bold]{AddDefenderExclusionKey}[/] = {FormatTriState(reply.HasDefaultAddDefenderExclusion, reply.DefaultAddDefenderExclusion)}");
				return 0;
			}
			catch (Exception ex) { return HandleError(ex); }
		});
		return cmd;
	}

	static Command BuildGet(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory)
	{
		var keyArg = new Argument<string>("key") { Description = "Setting key (e.g. add-defender-exclusion)" };
		var cmd = new Command("get", "Print one setting value.")
		{
			Arguments = { keyArg },
		};
		cmd.SetAction(async (parseResult, ct) =>
		{
			var (pipeName, timeout) = ReadGlobals(parseResult, pipeNameOption, timeoutOption);
			var key = parseResult.GetValue(keyArg)!;
			using var client = clientFactory(pipeName, timeout);
			try
			{
				if (!IsKnownKey(key))
				{
					AnsiConsole.MarkupLine($"[red]Unknown setting '{key}'.[/] Try '{AddDefenderExclusionKey}'.");
					return 1;
				}
				var reply = await client.GetSettingsAsync(ct);
				AnsiConsole.WriteLine(FormatTriStatePlain(reply.HasDefaultAddDefenderExclusion, reply.DefaultAddDefenderExclusion));
				return 0;
			}
			catch (Exception ex) { return HandleError(ex); }
		});
		return cmd;
	}

	static Command BuildSet(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory)
	{
		var keyArg = new Argument<string>("key") { Description = "Setting key (e.g. add-defender-exclusion)" };
		var valueArg = new Argument<string>("value") { Description = "Setting value: true | false | unset | clear" };
		var cmd = new Command("set", "Persist a setting value (true/false/unset).")
		{
			Arguments = { keyArg, valueArg },
		};
		cmd.SetAction(async (parseResult, ct) =>
		{
			var (pipeName, timeout) = ReadGlobals(parseResult, pipeNameOption, timeoutOption);
			var key = parseResult.GetValue(keyArg)!;
			var rawValue = parseResult.GetValue(valueArg)!;

			if (!IsKnownKey(key))
			{
				AnsiConsole.MarkupLine($"[red]Unknown setting '{key}'.[/] Try '{AddDefenderExclusionKey}'.");
				return 1;
			}

			if (!TryParseTriState(rawValue, out var newValue, out var clear))
			{
				AnsiConsole.MarkupLine($"[red]Invalid value '{rawValue}'.[/] Use 'true', 'false', 'unset' or 'clear'.");
				return 1;
			}

			using var client = clientFactory(pipeName, timeout);
			try
			{
				var reply = await client.SetSettingsAsync(newValue, clear, ct);
				if (!reply.Success)
				{
					AnsiConsole.MarkupLine($"[red]Failed to persist setting:[/] {reply.ErrorMessage}");
					return 1;
				}
				AnsiConsole.MarkupLine($"[green]OK.[/] {AddDefenderExclusionKey} = {(clear ? "(unset)" : (newValue?.ToString() ?? "(unset)"))}");
				return 0;
			}
			catch (Exception ex) { return HandleError(ex); }
		});
		return cmd;
	}

	// ---- helpers ----

	static bool IsKnownKey(string key)
		=> string.Equals(key, AddDefenderExclusionKey, StringComparison.OrdinalIgnoreCase);

	/// <summary>
	/// Parses 'true' / 'false' (set the value) and 'unset' / 'clear' (wipe).
	/// </summary>
	internal static bool TryParseTriState(string raw, out bool? value, out bool clear)
	{
		value = null;
		clear = false;
		switch (raw.Trim().ToLowerInvariant())
		{
			case "true":  value = true;  return true;
			case "false": value = false; return true;
			case "unset":
			case "clear":
			case "null":
				clear = true;
				return true;
			default: return false;
		}
	}

	static string FormatTriState(bool has, bool value)
		=> has ? $"[bold]{value.ToString().ToLowerInvariant()}[/]" : "[grey](unset)[/]";

	static string FormatTriStatePlain(bool has, bool value)
		=> has ? value.ToString().ToLowerInvariant() : "(unset)";

	static (string PipeName, TimeSpan? Timeout) ReadGlobals(
		ParseResult parseResult,
		Option<string> pipeNameOption,
		Option<int?> timeoutOption)
	{
		var pipeName = parseResult.GetValue(pipeNameOption)!;
		var timeoutSeconds = parseResult.GetValue(timeoutOption);
		var timeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;
		return (pipeName, timeout);
	}

	static int HandleError(Exception ex)
	{
		switch (ex)
		{
			case VhdxManagerServiceException:
				AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
				return 2;
			case TimeoutException:
				AnsiConsole.MarkupLine($"[red]Timeout: {ex.Message}[/]");
				return 3;
			default:
				AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
				return 1;
		}
	}
}

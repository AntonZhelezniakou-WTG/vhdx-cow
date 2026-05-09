using System.Globalization;
using Spectre.Console;

namespace VhdxManager.Cli;

/// <summary>
/// Thin wrapper around Spectre.Console prompts. Used by interactive commands
/// (create/convert) to fill in any missing required parameter.
///
/// In non-interactive mode (stdin redirected, e.g. CI) any prompt throws —
/// callers should pass all required options on the command line in that case.
/// </summary>
internal static class InteractivePrompt
{
	static void EnsureInteractive(string promptName)
	{
		if (Console.IsInputRedirected)
		{
			throw new InvalidOperationException(
				$"Required option '{promptName}' is missing and the console is non-interactive. " +
				"Pass the option on the command line.");
		}
	}

	public static string AskString(string label, string? defaultValue = null)
	{
		EnsureInteractive(label);
		var prompt = new TextPrompt<string>(label);
		if (defaultValue is not null) prompt.DefaultValue(defaultValue);
		return AnsiConsole.Prompt(prompt);
	}

	public static string AskOptionalString(string label)
	{
		EnsureInteractive(label);
		var prompt = new TextPrompt<string>(label).AllowEmpty();
		return AnsiConsole.Prompt(prompt);
	}

	public static bool AskBool(string label, bool defaultValue = false)
	{
		EnsureInteractive(label);
		return AnsiConsole.Confirm(label, defaultValue);
	}

	public static bool AskDynamicVsFixed(string label, bool defaultDynamic = true)
	{
		EnsureInteractive(label);
		var choice = AnsiConsole.Prompt(
			new SelectionPrompt<string>()
				.Title(label)
				.AddChoices(defaultDynamic
					? ["dynamic", "fixed"]
					: ["fixed", "dynamic"]));
		return choice == "dynamic";
	}

	public static long AskSize(string label, long? defaultValue = null)
	{
		EnsureInteractive(label);
		var prompt = new TextPrompt<string>($"{label} (e.g. 50G, 500M, 1T):")
			.Validate(input =>
				TryParseSize(input, out _)
					? ValidationResult.Success()
					: ValidationResult.Error("[red]Invalid size — use a number with K/M/G/T suffix (e.g. 100M, 50G).[/]"));
		if (defaultValue is { } dv)
		{
			prompt.DefaultValue(FormatSize(dv));
		}
		var raw = AnsiConsole.Prompt(prompt);
		_ = TryParseSize(raw, out var bytes);
		return bytes;
	}

	public static bool TryParseSize(string text, out long bytes)
	{
		bytes = 0;
		if (string.IsNullOrWhiteSpace(text)) return false;
		text = text.Trim();
		var unit = text[^1];
		long multiplier;
		string numericPart;
		if (char.IsLetter(unit))
		{
			multiplier = char.ToUpperInvariant(unit) switch
			{
				'K' => 1024L,
				'M' => 1024L * 1024,
				'G' => 1024L * 1024 * 1024,
				'T' => 1024L * 1024 * 1024 * 1024,
				_ => 0L,
			};
			if (multiplier == 0L) return false;
			numericPart = text[..^1].Trim();
		}
		else
		{
			multiplier = 1;
			numericPart = text;
		}
		if (!double.TryParse(numericPart, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)) return false;
		var result = (long)(value * multiplier);
		if (result <= 0) return false;
		bytes = result;
		return true;
	}

	public static string FormatSize(long bytes)
	{
		if (bytes >= 1024L * 1024 * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024 * 1024):0.##}T";
		if (bytes >= 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024 * 1024):0.##}G";
		if (bytes >= 1024L * 1024) return $"{bytes / (1024.0 * 1024):0.##}M";
		if (bytes >= 1024) return $"{bytes / 1024.0:0.##}K";
		return bytes.ToString(CultureInfo.InvariantCulture);
	}
}

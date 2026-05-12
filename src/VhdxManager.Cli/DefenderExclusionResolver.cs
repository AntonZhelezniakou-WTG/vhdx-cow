using Spectre.Console;
using VhdxManager.Client;

namespace VhdxManager.Cli;

/// <summary>
/// Resolves the effective value of <c>--add-defender-exclusion</c> for a single
/// command invocation, using the standard precedence:
/// <list type="number">
/// <item>explicit CLI flag wins</item>
/// <item>else service-side persisted default (set via <c>vhdx config set</c>)</item>
/// <item>else interactive prompt (<see cref="InteractivePrompt.AskBool"/>)</item>
/// </list>
/// In non-interactive mode the prompt throws — callers are then expected to
/// either set a service default or pass the flag explicitly. This matches the
/// behaviour of every other interactive option in the CLI.
/// </summary>
static class DefenderExclusionResolver
{
	public static async Task<bool> ResolveAsync(
		bool? cliValue,
		IVhdxManagerClient client,
		CancellationToken ct)
	{
		if (cliValue.HasValue)
		{
			return cliValue.Value;
		}

		// We avoid re-prompting once a default is configured. The GetSettings RPC
		// is cheap (unary, in-process pipe) — same cost as a Ping.
		var settings = await client.GetSettingsAsync(ct);
		if (settings.HasDefaultAddDefenderExclusion)
		{
			AnsiConsole.MarkupLine(
				$"[grey]Defender exclusion default = {settings.DefaultAddDefenderExclusion} (from service config).[/]");
			return settings.DefaultAddDefenderExclusion;
		}

		return InteractivePrompt.AskBool(
			"Add the new VHDX file to Windows Defender exclusions?",
			defaultValue: false);
	}
}

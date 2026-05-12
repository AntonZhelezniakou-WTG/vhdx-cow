using Spectre.Console;
using VhdxManager.Client;

namespace VhdxManager.Cli;

/// <summary>
/// Resolves the effective value of <c>--add-defender-exclusion</c> for a single
/// command invocation, using the standard precedence:
/// <list type="number">
/// <item>explicit CLI flag wins</item>
/// <item>else service-side persisted default (set via <c>vhmgr config set</c>)</item>
/// <item>else <see langword="false"/> — no prompt</item>
/// </list>
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

		return false;
	}
}

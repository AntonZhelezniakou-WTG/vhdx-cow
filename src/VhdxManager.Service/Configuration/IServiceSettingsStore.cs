namespace VhdxManager.Service.Configuration;

/// <summary>
/// Read/write persistence for the service-side defaults exposed to the CLI
/// via the GetSettings/SetSettings RPCs. Backed by appsettings.json.
///
/// Tri-state: <c>null</c> (or missing key) means "no default — CLI will prompt
/// the user". Concrete <c>true</c>/<c>false</c> is honoured silently.
/// </summary>
public interface IServiceSettingsStore
{
	/// <summary>Returns the persisted default for --add-defender-exclusion, or null if unset.</summary>
	bool? GetDefaultAddDefenderExclusion();

	/// <summary>
	/// Persists the default for --add-defender-exclusion. <paramref name="value"/>=null
	/// clears the override (key written as JSON null) so future reads return "unset".
	/// </summary>
	Task SetDefaultAddDefenderExclusionAsync(bool? value, CancellationToken ct);
}

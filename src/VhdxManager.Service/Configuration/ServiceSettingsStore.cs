using System.Text.Json;
using System.Text.Json.Nodes;

namespace VhdxManager.Service.Configuration;

/// <summary>
/// JSON-backed implementation of <see cref="IServiceSettingsStore"/>. Edits the
/// in-place <c>appsettings.json</c> next to the running service binary so the
/// override survives restarts.
///
/// Writes are serialised through a process-wide semaphore and committed atomically
/// (write-temp + replace) so concurrent <c>SetSettings</c> calls cannot corrupt
/// the file.
/// </summary>
public sealed class ServiceSettingsStore(ILogger<ServiceSettingsStore> logger) : IServiceSettingsStore
{
	const string Section = "VhdxManager";
	const string DefaultsKey = "Defaults";
	const string AddDefenderExclusionKey = "AddDefenderExclusion";

	static readonly SemaphoreSlim writeLock = new(1, 1);

	static readonly JsonSerializerOptions writeOptions = new()
	{
		WriteIndented = true,
	};

	/// <summary>
	/// Path to <c>appsettings.json</c> alongside the service executable.
	/// Resolved at first use so unit tests that swap <see cref="AppContext.BaseDirectory"/>
	/// pick the right file.
	/// </summary>
	static string SettingsPath =>
		Path.Combine(AppContext.BaseDirectory, "appsettings.json");

	public bool? GetDefaultAddDefenderExclusion()
	{
		var path = SettingsPath;
		if (!File.Exists(path))
		{
			return null;
		}

		try
		{
			using var fs = File.OpenRead(path);
			var root = JsonNode.Parse(fs);
			var node = root?[Section]?[DefaultsKey]?[AddDefenderExclusionKey];
			if (node is null)
			{
				return null;
			}
			// JsonNode for a JSON `null` literal still parses non-null; prefer the
			// underlying JsonValue check.
			if (node is JsonValue v && v.TryGetValue(out bool b))
			{
				return b;
			}
			return null;
		}
		catch (Exception ex)
		{
			// Bad JSON / IO error: surface as "unset" so the CLI falls through to
			// prompting the user; never throw across the gRPC boundary.
			logger.LogWarning(ex,
				"Failed to read AddDefenderExclusion from {Path}; treating as unset.", path);
			return null;
		}
	}

	public async Task SetDefaultAddDefenderExclusionAsync(bool? value, CancellationToken ct)
	{
		await writeLock.WaitAsync(ct);
		try
		{
			var path = SettingsPath;

			JsonObject root;
			if (File.Exists(path))
			{
				await using var fs = File.OpenRead(path);
				root = JsonNode.Parse(fs) as JsonObject
					?? throw new InvalidOperationException(
						$"Settings file {path} did not parse as a JSON object.");
			}
			else
			{
				root = new JsonObject();
			}

			// Ensure VhdxManager.Defaults section exists.
			if (root[Section] is not JsonObject section)
			{
				section = new JsonObject();
				root[Section] = section;
			}
			if (section[DefaultsKey] is not JsonObject defaults)
			{
				defaults = new JsonObject();
				section[DefaultsKey] = defaults;
			}

			// `value=null` writes JSON null (explicit "unset"). The reader treats both
			// missing and null as "unset", but explicit null preserves user intent
			// when the file is hand-edited.
			defaults[AddDefenderExclusionKey] = value.HasValue
				? JsonValue.Create(value.Value)
				: null;

			// Atomic replace — write to a sibling temp file, then File.Move(replace).
			var tempPath = path + ".tmp";
			await using (var outFs = File.Create(tempPath))
			{
				await JsonSerializer.SerializeAsync(outFs, root, writeOptions, ct);
			}
			File.Move(tempPath, path, overwrite: true);

			logger.LogInformation(
				"Persisted AddDefenderExclusion={Value} to {Path}",
				value?.ToString() ?? "(unset)", path);
		}
		finally
		{
			writeLock.Release();
		}
	}
}

using System.Text.Json;

namespace VhdxManager.Service.State;

/// <summary>
/// Persists mount state as a JSON file at a configured path.
/// Thread-safe via SemaphoreSlim.
/// </summary>
public sealed class JsonStateStore : IStateStore
{
	static readonly JsonSerializerOptions JsonOptions = new()
	{
		WriteIndented = true,
		PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
	};

	readonly string filePath;
	readonly SemaphoreSlim @lock = new(1, 1);
	readonly ILogger<JsonStateStore> logger;
	List<MountedDiskState> cache = [];

	public JsonStateStore(IConfiguration configuration, ILogger<JsonStateStore> logger)
	{
		this.logger = logger;

		var configuredPath = configuration.GetValue<string>("VhdxManager:StatePath")
			?? Path.Combine(
				Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
				"VhdxManager",
				"state.json");

		filePath = Environment.ExpandEnvironmentVariables(configuredPath);

		_ = LoadAsync(CancellationToken.None);
	}

	public int GetActiveMountCount() => cache.Count;

	public async Task<IReadOnlyList<MountedDiskState>> GetAllAsync(CancellationToken ct)
	{
		await @lock.WaitAsync(ct);
		try
		{
			return cache.AsReadOnly();
		}
		finally
		{
			@lock.Release();
		}
	}

	public async Task<MountedDiskState?> GetAsync(string childVhdxPath, CancellationToken ct)
	{
		await @lock.WaitAsync(ct);
		try
		{
			return cache.Find(s =>
				string.Equals(s.ChildVhdxPath, childVhdxPath, StringComparison.OrdinalIgnoreCase));
		}
		finally
		{
			@lock.Release();
		}
	}

	public async Task AddAsync(MountedDiskState state, CancellationToken ct)
	{
		await @lock.WaitAsync(ct);
		try
		{
			cache.RemoveAll(s => string.Equals(s.ChildVhdxPath, state.ChildVhdxPath, StringComparison.OrdinalIgnoreCase));
			cache.Add(state);
			await SaveAsync(ct);
		}
		finally
		{
			@lock.Release();
		}
	}

	public async Task RemoveAsync(string childVhdxPath, CancellationToken ct)
	{
		await @lock.WaitAsync(ct);
		try
		{
			cache.RemoveAll(s => string.Equals(s.ChildVhdxPath, childVhdxPath, StringComparison.OrdinalIgnoreCase));
			await SaveAsync(ct);
		}
		finally
		{
			@lock.Release();
		}
	}

	async Task LoadAsync(CancellationToken ct)
	{
		if (!File.Exists(filePath))
		{
			logger.LogInformation("State file not found at {Path}, starting with empty state", filePath);
			return;
		}

		try
		{
			var json = await File.ReadAllTextAsync(filePath, ct);
			cache = JsonSerializer.Deserialize<List<MountedDiskState>>(json, JsonOptions) ?? [];
			logger.LogInformation("Loaded {Count} mount states from {Path}", cache.Count, filePath);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to load state from {Path}", filePath);
			cache = [];
		}
	}

	async Task SaveAsync(CancellationToken ct)
	{
		var directory = Path.GetDirectoryName(filePath);
		if (directory is not null && !Directory.Exists(directory))
		{
			Directory.CreateDirectory(directory);
		}

		var json = JsonSerializer.Serialize(cache, JsonOptions);
		await File.WriteAllTextAsync(filePath, json, ct);
		logger.LogDebug("Saved {Count} mount states to {Path}", cache.Count, filePath);
	}
}

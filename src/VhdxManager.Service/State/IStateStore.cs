namespace VhdxManager.Service.State;

/// <summary>
/// Persists information about active VHDX mounts across service restarts.
/// </summary>
public interface IStateStore
{
	Task<IReadOnlyList<MountedDiskState>> GetAllAsync(CancellationToken ct = default);

	Task<MountedDiskState?> GetAsync(string childVhdxPath, CancellationToken ct = default);

	Task AddAsync(MountedDiskState state, CancellationToken ct = default);

	Task RemoveAsync(string childVhdxPath, CancellationToken ct = default);

	int GetActiveMountCount();
}

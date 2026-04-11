namespace VhdxCow.Service.VhdxOperations;

/// <summary>
/// Volume mount point operations via P/Invoke (SetVolumeMountPoint, etc.).
/// Stub implementation — actual P/Invoke calls will be added in Phase 3.
/// </summary>
public sealed class VolumeManager(ILogger<VolumeManager> logger) : IVolumeManager
{
	public Task<string> GetVolumeGuidPathAsync(string physicalDiskPath, CancellationToken ct)
	{
		logger.LogInformation("GetVolumeGuidPath: {PhysicalDiskPath}", physicalDiskPath);
		throw new NotImplementedException("Volume P/Invoke not yet implemented");
	}

	public Task MountToFolderAsync(string volumeGuidPath, string mountPath, CancellationToken ct)
	{
		logger.LogInformation("MountToFolder: {VolumeGuidPath} -> {MountPath}", volumeGuidPath, mountPath);
		throw new NotImplementedException("Volume P/Invoke not yet implemented");
	}

	public Task UnmountFolderAsync(string mountPath, CancellationToken ct)
	{
		logger.LogInformation("UnmountFolder: {MountPath}", mountPath);
		throw new NotImplementedException("Volume P/Invoke not yet implemented");
	}
}

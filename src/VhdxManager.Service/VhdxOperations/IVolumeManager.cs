namespace VhdxManager.Service.VhdxOperations;

/// <summary>
/// Manages volume mount points: discovers volume GUIDs from physical disks
/// and mounts/unmounts volumes to NTFS folders.
/// </summary>
public interface IVolumeManager
{
	/// <summary>
	/// Discovers the volume GUID path for a partition on the given physical disk.
	/// </summary>
	Task<string> GetVolumeGuidPathAsync(
		string physicalDiskPath,
		CancellationToken ct = default);

	/// <summary>
	/// Mounts a volume (identified by GUID path) to an empty NTFS folder.
	/// </summary>
	Task MountToFolderAsync(
		string volumeGuidPath,
		string mountPath,
		CancellationToken ct = default);

	/// <summary>
	/// Removes a volume mount point from a folder.
	/// </summary>
	Task UnmountFolderAsync(
		string mountPath,
		CancellationToken ct = default);
}

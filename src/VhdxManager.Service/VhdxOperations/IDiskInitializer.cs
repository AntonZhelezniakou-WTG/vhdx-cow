namespace VhdxManager.Service.VhdxOperations;

/// <summary>
/// Initializes a freshly attached (raw) physical disk: writes a GPT partition
/// table, creates one primary partition that spans the whole disk, formats it
/// with the requested filesystem. After this completes, the disk has a single
/// mountable volume.
/// </summary>
public interface IDiskInitializer
{
	/// <summary>
	/// Initialize + partition + format the given physical disk.
	/// </summary>
	/// <param name="physicalDiskPath">Path returned by <see cref="IVirtDiskManager.AttachAsync"/> (e.g. <c>\\.\PhysicalDrive4</c>).</param>
	/// <param name="label">Volume label (max 32 chars).</param>
	/// <param name="filesystem">Filesystem name accepted by PowerShell <c>Format-Volume -FileSystem</c>: <c>ReFS</c> or <c>NTFS</c>.</param>
	Task InitializeAndFormatAsync(
		string physicalDiskPath,
		string label,
		string filesystem,
		CancellationToken ct = default);
}

namespace VhdxManager.Service.VhdxOperations;

/// <summary>
/// Initializes a freshly attached (raw) physical disk: writes a GPT partition
/// table, creates one primary partition that spans the whole disk, formats it
/// as NTFS. After this completes, the disk has a single mountable volume.
/// </summary>
public interface IDiskInitializer
{
	/// <summary>
	/// Initialize + partition + format the given physical disk.
	/// </summary>
	/// <param name="physicalDiskPath">Path returned by <see cref="IVhdxManager.AttachAsync"/> (e.g. <c>\\.\PhysicalDrive4</c>).</param>
	/// <param name="ntfsLabel">NTFS volume label (max 32 chars).</param>
	Task InitializeAndFormatAsync(
		string physicalDiskPath,
		string ntfsLabel,
		CancellationToken ct = default);
}

namespace VhdxCow.Service.VhdxOperations;

/// <summary>
/// Manages VHDX virtual disk lifecycle: create, attach, detach, merge.
/// All operations require admin privileges (SE_MANAGE_VOLUME_PRIVILEGE).
/// </summary>
public interface IVhdxManager
{
	/// <summary>
	/// Creates a differencing (child) VHDX that references the given parent.
	/// </summary>
	Task CreateDifferencingDiskAsync(
		string parentVhdxPath,
		string childVhdxPath,
		CancellationToken ct = default);

	/// <summary>
	/// Creates a fresh standalone (non-differencing) VHDX file. The disk is empty
	/// and unpartitioned; partition + format must be performed separately
	/// (see <see cref="IDiskInitializer"/>).
	/// </summary>
	/// <param name="vhdxPath">Path of the new VHDX file. Must not exist.</param>
	/// <param name="sizeBytes">Logical disk size, rounded up by VirtDisk to MB.</param>
	/// <param name="dynamic">true → dynamic (sparse); false → fixed (full preallocation).</param>
	Task CreateStandaloneVhdxAsync(
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		CancellationToken ct = default);

	/// <summary>
	/// Attaches a VHDX without assigning a drive letter.
	/// Uses PERMANENT_LIFETIME so the disk survives handle close and service restart.
	/// Returns the physical disk path (e.g. \\.\PhysicalDrive3).
	/// </summary>
	Task<string> AttachAsync(
		string vhdxPath,
		CancellationToken ct = default);

	/// <summary>
	/// Detaches a previously attached VHDX.
	/// </summary>
	Task DetachAsync(
		string vhdxPath,
		CancellationToken ct = default);

	/// <summary>
	/// Merges a child (overlay) VHDX into its parent, then deletes the child.
	/// The parent must not be attached during merge.
	/// </summary>
	Task MergeAsync(
		string childVhdxPath,
		CancellationToken ct = default);

	/// <summary>
	/// Queries information about a VHDX file (attached state, parent path, size).
	/// </summary>
	Task<VhdxInfo> GetInfoAsync(
		string vhdxPath,
		CancellationToken ct = default);
}

public readonly record struct VhdxInfo(
	bool IsAttached,
	string? ParentPath,
	ulong VirtualSize,
	ulong PhysicalSize);

using System.ComponentModel;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Storage.Vhd;
using Microsoft.Win32.SafeHandles;

namespace VhdxManager.Service.VhdxOperations;

/// <summary>
/// VHDX operations via P/Invoke (CsWin32) against VirtDisk.dll.
/// All operations are synchronous Win32 calls wrapped in Task.Run for async interface.
/// </summary>
public sealed class VirtDiskManager(ILogger<VirtDiskManager> logger) : IVirtDiskManager
{
	static readonly Guid VendorMicrosoft = new("EC984AEC-A0F9-47e9-901F-71415A66345B");
	const uint DeviceVhdx = 3;

	public Task CreateDifferencingDiskAsync(string parentVhdxPath, string childVhdxPath, CancellationToken ct)
		=> Task.Run(()
		=> CreateDifferencingDiskCore(parentVhdxPath, childVhdxPath),
			ct);

	public Task CreateStandaloneVhdxAsync(string vhdxPath, long sizeBytes, bool dynamic, CancellationToken ct)
		=> Task.Run(()
		=> CreateStandaloneVhdxCore(vhdxPath, sizeBytes, dynamic),
			ct);

	void CreateStandaloneVhdxCore(string vhdxPath, long sizeBytes, bool dynamic)
	{
		if (sizeBytes < 3 * 1024 * 1024)
		{
			throw new ArgumentOutOfRangeException(nameof(sizeBytes), sizeBytes, "Minimum supported VHDX size is 3 MB.");
		}

		logger.LogInformation(
			"Creating standalone VHDX: Path={Path}, SizeBytes={SizeBytes}, Dynamic={Dynamic}",
				vhdxPath, sizeBytes, dynamic);

		var storageType = new VIRTUAL_STORAGE_TYPE
		{
			DeviceId = DeviceVhdx,
			VendorId = VendorMicrosoft,
		};

		var createParams = new CREATE_VIRTUAL_DISK_PARAMETERS
		{
			Version = CREATE_VIRTUAL_DISK_VERSION.CREATE_VIRTUAL_DISK_VERSION_2,
		};
		createParams.Anonymous.Version2.UniqueId = Guid.NewGuid();
		createParams.Anonymous.Version2.MaximumSize = (ulong)sizeBytes;
		createParams.Anonymous.Version2.BlockSizeInBytes = 0;   // VirtDisk default
		createParams.Anonymous.Version2.SectorSizeInBytes = 0;  // VirtDisk default (logical 512)

		// Fixed disk preallocates the full physical extent; dynamic uses default (sparse VHDX).
		var flags = dynamic
			? CREATE_VIRTUAL_DISK_FLAG.CREATE_VIRTUAL_DISK_FLAG_NONE
			: CREATE_VIRTUAL_DISK_FLAG.CREATE_VIRTUAL_DISK_FLAG_FULL_PHYSICAL_ALLOCATION;

		var result = PInvoke.CreateVirtualDisk(
			in storageType,
			vhdxPath,
			VirtualDiskAccessMask: VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_NONE,
			SecurityDescriptor: default,
			flags,
			ProviderSpecificFlags: 0,
			in createParams,
			Overlapped: null,
			out var handle);

		if (result is not WIN32_ERROR.ERROR_SUCCESS)
		{
			logger.LogError(
				"CreateVirtualDisk (standalone) failed: {ErrorCode} ({ErrorMessage})",
					(uint)result, new Win32Exception((int)result).Message);
			throw new Win32Exception((int)result, $"CreateVirtualDisk failed for '{vhdxPath}'");
		}

		handle.Dispose();
		logger.LogInformation("Standalone VHDX created: {Path}", vhdxPath);
	}

	unsafe void CreateDifferencingDiskCore(string parentVhdxPath, string childVhdxPath)
	{
		logger.LogInformation(
			"Creating differencing disk: Parent={ParentPath}, Child={ChildPath}",
				parentVhdxPath, childVhdxPath);

		var storageType = new VIRTUAL_STORAGE_TYPE
		{
			DeviceId = DeviceVhdx,
			VendorId = VendorMicrosoft,
		};

		fixed (char* pParentPath = parentVhdxPath)
		{
			var createParams = new CREATE_VIRTUAL_DISK_PARAMETERS
			{
				Version = CREATE_VIRTUAL_DISK_VERSION.CREATE_VIRTUAL_DISK_VERSION_2,
			};
			createParams.Anonymous.Version2.UniqueId = Guid.NewGuid();
			createParams.Anonymous.Version2.MaximumSize = 0; // inherited from parent
			createParams.Anonymous.Version2.BlockSizeInBytes = 0; // inherited from parent
			createParams.Anonymous.Version2.SectorSizeInBytes = 0; // inherited from parent
			createParams.Anonymous.Version2.ParentPath = pParentPath;

			var result = PInvoke.CreateVirtualDisk(
				in storageType,
				childVhdxPath,
				VirtualDiskAccessMask: VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_NONE,
				SecurityDescriptor: default,
				CREATE_VIRTUAL_DISK_FLAG.CREATE_VIRTUAL_DISK_FLAG_NONE,
				ProviderSpecificFlags: 0,
				in createParams,
				Overlapped: null,
				out var handle);

			if (result is not WIN32_ERROR.ERROR_SUCCESS)
			{
				logger.LogError(
					"CreateVirtualDisk failed: {ErrorCode} ({ErrorMessage})",
						(uint)result, new Win32Exception((int)result).Message);
				throw new Win32Exception((int)result, $"CreateVirtualDisk failed for '{childVhdxPath}'");
			}

			handle.Dispose();
		}

		logger.LogInformation("Differencing disk created: {ChildPath}", childVhdxPath);
	}

	public Task<string> AttachAsync(string vhdxPath, CancellationToken ct) => Task.Run(() =>
	{
		logger.LogInformation("Attaching VHDX: {VhdxPath}", vhdxPath);

		using var handle = OpenDisk(
			vhdxPath,
			accessMask: VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_ATTACH_RW
				| VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_GET_INFO
				| VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_DETACH);

		var attachParams = new ATTACH_VIRTUAL_DISK_PARAMETERS
		{
			Version = ATTACH_VIRTUAL_DISK_VERSION.ATTACH_VIRTUAL_DISK_VERSION_1,
		};

		var result = PInvoke.AttachVirtualDisk(
			handle,
			SecurityDescriptor: default,
			Flags: ATTACH_VIRTUAL_DISK_FLAG.ATTACH_VIRTUAL_DISK_FLAG_NO_DRIVE_LETTER
				| ATTACH_VIRTUAL_DISK_FLAG.ATTACH_VIRTUAL_DISK_FLAG_PERMANENT_LIFETIME,
			ProviderSpecificFlags: 0,
			attachParams,
			Overlapped: null);

		if (result is not WIN32_ERROR.ERROR_SUCCESS)
		{
			logger.LogError(
				"AttachVirtualDisk failed for {VhdxPath}: {ErrorCode} ({ErrorMessage})",
					vhdxPath, (uint)result, new Win32Exception((int)result).Message);
			throw new Win32Exception((int)result, $"AttachVirtualDisk failed for '{vhdxPath}'");
		}

		var physicalPath = GetPhysicalPath(handle);
		logger.LogInformation(
			"VHDX attached: {VhdxPath} -> {PhysicalPath}", vhdxPath, physicalPath);

		return physicalPath;
	}, ct);

	public Task DetachAsync(string vhdxPath, CancellationToken ct) => Task.Run(() =>
	{
		logger.LogInformation("Detaching VHDX: {VhdxPath}", vhdxPath);

		using var handle = OpenDisk(
			vhdxPath,
			accessMask: VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_DETACH);

		var result = PInvoke.DetachVirtualDisk(
			handle,
			Flags: DETACH_VIRTUAL_DISK_FLAG.DETACH_VIRTUAL_DISK_FLAG_NONE,
			ProviderSpecificFlags: 0);

		if (result is not WIN32_ERROR.ERROR_SUCCESS)
		{
			logger.LogError(
				"DetachVirtualDisk failed for {VhdxPath}: {ErrorCode} ({ErrorMessage})",
					vhdxPath, (uint)result, new Win32Exception((int)result).Message);
			throw new Win32Exception((int)result, $"DetachVirtualDisk failed for '{vhdxPath}'");
		}

		logger.LogInformation("VHDX detached: {VhdxPath}", vhdxPath);
	}, ct);

	public Task MergeAsync(string childVhdxPath, CancellationToken ct) => Task.Run(() =>
	{
		logger.LogInformation("Merging child into parent: {ChildPath}", childVhdxPath);

		// Open with RWDepth=2 for parent-child merge
		var storageType = new VIRTUAL_STORAGE_TYPE
		{
			DeviceId = DeviceVhdx,
			VendorId = VendorMicrosoft,
		};

		var openParams = new OPEN_VIRTUAL_DISK_PARAMETERS
		{
			Version = OPEN_VIRTUAL_DISK_VERSION.OPEN_VIRTUAL_DISK_VERSION_1,
		};
		openParams.Anonymous.Version1.RWDepth = 2;

		var openResult = PInvoke.OpenVirtualDisk(
			in storageType,
			childVhdxPath,
			VirtualDiskAccessMask: VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_METAOPS,
			Flags: OPEN_VIRTUAL_DISK_FLAG.OPEN_VIRTUAL_DISK_FLAG_NONE,
			openParams,
			out var handle);

		if (openResult is not WIN32_ERROR.ERROR_SUCCESS)
		{
			logger.LogError(
				"OpenVirtualDisk for merge failed: {ErrorCode} ({ErrorMessage})",
					(uint)openResult, new Win32Exception((int)openResult).Message);
			throw new Win32Exception((int)openResult, $"OpenVirtualDisk for merge failed for '{childVhdxPath}'");
		}

		using (handle)
		{
			var mergeParams = new MERGE_VIRTUAL_DISK_PARAMETERS
			{
				Version = MERGE_VIRTUAL_DISK_VERSION.MERGE_VIRTUAL_DISK_VERSION_2,
			};
			mergeParams.Anonymous.Version2.MergeSourceDepth = 1; // child (leaf)
			mergeParams.Anonymous.Version2.MergeTargetDepth = 2; // parent (one level up)

			var mergeResult = PInvoke.MergeVirtualDisk(
				handle,
				Flags: MERGE_VIRTUAL_DISK_FLAG.MERGE_VIRTUAL_DISK_FLAG_NONE,
				in mergeParams,
				Overlapped: null);

			if (mergeResult is not WIN32_ERROR.ERROR_SUCCESS)
			{
				logger.LogError(
					"MergeVirtualDisk failed for {ChildPath}: {ErrorCode} ({ErrorMessage})",
						childVhdxPath, (uint)mergeResult, new Win32Exception((int)mergeResult).Message);
				throw new Win32Exception((int)mergeResult, $"MergeVirtualDisk failed for '{childVhdxPath}'");
			}
		}

		// Delete the child VHDX file after successful merge
		File.Delete(childVhdxPath);
		logger.LogInformation("Merge completed and child deleted: {ChildPath}", childVhdxPath);
	}, ct);

	public Task<VhdxInfo> GetInfoAsync(string vhdxPath, CancellationToken ct) => Task.Run(() =>
	{
		logger.LogDebug("Getting info for: {VhdxPath}", vhdxPath);

		bool isAttached;
		string? parentPath = null;
		try
		{
			using var handle = OpenDisk(
				vhdxPath,
				accessMask: VIRTUAL_DISK_ACCESS_MASK.VIRTUAL_DISK_ACCESS_GET_INFO);

			// Try to get the physical path — if it succeeds, the disk is attached
			try
			{
				GetPhysicalPath(handle);
				isAttached = true;
			}
			catch (Win32Exception)
			{
				isAttached = false;
			}
		}
		catch (Win32Exception)
		{
			// Can't even open the disk
			return new VhdxInfo(IsAttached: false, ParentPath: null, VirtualSize: 0, PhysicalSize: 0);
		}

		// Get file sizes
		ulong physicalSize = 0;
		if (File.Exists(vhdxPath))
		{
			physicalSize = (ulong)new FileInfo(vhdxPath).Length;
		}

		return new VhdxInfo(isAttached, parentPath, 0, physicalSize);
	}, ct);

	SafeFileHandle OpenDisk(
		string vhdxPath,
		VIRTUAL_DISK_ACCESS_MASK accessMask,
		OPEN_VIRTUAL_DISK_FLAG flags = OPEN_VIRTUAL_DISK_FLAG.OPEN_VIRTUAL_DISK_FLAG_NONE)
	{
		var storageType = new VIRTUAL_STORAGE_TYPE
		{
			DeviceId = DeviceVhdx,
			VendorId = VendorMicrosoft,
		};

		// VERSION_1 with explicit access mask. VERSION_2 requires AccessMask=VIRTUAL_DISK_ACCESS_NONE
		// and uses ReadOnly/GetInfoOnly flags instead — none of our call sites need that semantics,
		// and mixing VERSION_2 with a non-NONE mask returns ERROR_INVALID_PARAMETER (87).
		var openParams = new OPEN_VIRTUAL_DISK_PARAMETERS
		{
			Version = OPEN_VIRTUAL_DISK_VERSION.OPEN_VIRTUAL_DISK_VERSION_1,
		};
		openParams.Anonymous.Version1.RWDepth = 1;

		var result = PInvoke.OpenVirtualDisk(
			in storageType,
			vhdxPath,
			accessMask,
			flags,
			openParams,
			out var handle);

		if (result is not WIN32_ERROR.ERROR_SUCCESS)
		{
			logger.LogError(
				"OpenVirtualDisk failed for {VhdxPath}: {ErrorCode} ({ErrorMessage})",
					vhdxPath, (uint)result, new Win32Exception((int)result).Message);
			throw new Win32Exception((int)result, $"OpenVirtualDisk failed for '{vhdxPath}'");
		}

		return handle;
	}

	static string GetPhysicalPath(SafeFileHandle handle)
	{
		uint pathSize = Win32PathLimits * sizeof(char);
		Span<char> pathBuffer = stackalloc char[Win32PathLimits];

		var result = PInvoke.GetVirtualDiskPhysicalPath(handle, ref pathSize, pathBuffer);
		if (result is not WIN32_ERROR.ERROR_SUCCESS)
		{
			throw new Win32Exception((int)result, "GetVirtualDiskPhysicalPath failed");
		}

		// pathBuffer is null-terminated, pathSize includes null terminator bytes
		var charsUsed = (int)(pathSize / 2) - 1;
		return charsUsed > 0 ? new string(pathBuffer[..charsUsed]) : string.Empty;
	}
}

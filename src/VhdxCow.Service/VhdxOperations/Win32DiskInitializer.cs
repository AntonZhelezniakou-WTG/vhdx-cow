using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Microsoft.Win32.SafeHandles;
using VhdxCow.Service.Native;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace VhdxCow.Service.VhdxOperations;

/// <summary>
/// Initializes a freshly attached raw VHDX disk: GPT partition table, one
/// primary basic-data partition, NTFS quick format. Pure Win32 P/Invoke,
/// no external processes.
/// </summary>
[SupportedOSPlatform("windows5.1.2600")]
[SuppressMessage("Interoperability", "CA1416", Justification = "Service is windows-only (net10.0-windows).")]
public sealed class Win32DiskInitializer(
	IVolumeManager volumeManager,
	ILogger<Win32DiskInitializer> logger) : IDiskInitializer
{
	const long PartitionAlignment = 1L * 1024 * 1024;        // align partition start to 1 MB
	const long GptReservedTail = 1L * 1024 * 1024;           // leave 1 MB at the end for GPT backup header

	public Task InitializeAndFormatAsync(string physicalDiskPath, string ntfsLabel, CancellationToken ct)
		=> Task.Run(() => InitializeAndFormatCore(physicalDiskPath, ntfsLabel, ct), ct);

	void InitializeAndFormatCore(string physicalDiskPath, string ntfsLabel, CancellationToken ct)
	{
		var diskNumber = ParseDiskNumber(physicalDiskPath);
		if (diskNumber == 0)
		{
			throw new InvalidOperationException(
				$"Refusing to operate on physical disk 0 (system disk). Got '{physicalDiskPath}'.");
		}

		logger.LogInformation(
			"Initializing physical disk {DiskNumber} (path={Path}) with GPT + NTFS (label={Label})",
				diskNumber, physicalDiskPath, ntfsLabel);

		using var diskHandle = OpenPhysicalDisk(physicalDiskPath);

		// Sanity check — device number from IOCTL must match the parsed path
		// to avoid initializing the wrong disk.
		var actualNumber = ReadDeviceNumber(diskHandle);
		if (actualNumber != diskNumber)
		{
			throw new InvalidOperationException(
				$"Device number mismatch: path '{physicalDiskPath}' parses to {diskNumber} " +
				$"but IOCTL_STORAGE_GET_DEVICE_NUMBER reports {actualNumber}.");
		}

		var diskSize = ReadDiskLength(diskHandle);
		logger.LogInformation("Disk {DiskNumber} length: {Bytes} bytes", diskNumber, diskSize);

		ct.ThrowIfCancellationRequested();
		CreateGptDisk(diskHandle);
		ct.ThrowIfCancellationRequested();
		WriteSinglePartitionLayout(diskHandle, diskSize);
		ct.ThrowIfCancellationRequested();
		UpdateProperties(diskHandle);

		// Allow PnP / Volume Manager a moment to surface the new volume.
		Thread.Sleep(500);

		// Discover the new volume + format it.
		var volumeGuid = volumeManager
			.GetVolumeGuidPathAsync(physicalDiskPath, ct)
			.GetAwaiter()
			.GetResult();

		FormatNtfs(volumeGuid, ntfsLabel);
		logger.LogInformation("Disk {DiskNumber} initialized and formatted.", diskNumber);
	}

	// ------------------------------------------------------------------ helpers

	static uint ParseDiskNumber(string physicalDiskPath)
	{
		const string prefix = @"\\.\PhysicalDrive";
		if (!physicalDiskPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
			|| !uint.TryParse(physicalDiskPath[prefix.Length..], out var n))
		{
			throw new ArgumentException(
				$"Expected path like '{prefix}N', got '{physicalDiskPath}'.",
				nameof(physicalDiskPath));
		}
		return n;
	}

	const uint GENERIC_READ = 0x80000000;
	const uint GENERIC_WRITE = 0x40000000;

	static SafeFileHandle OpenPhysicalDisk(string physicalDiskPath)
	{
		var handle = PInvoke.CreateFile(
			physicalDiskPath,
			GENERIC_READ | GENERIC_WRITE,
			FILE_SHARE_MODE.FILE_SHARE_READ | FILE_SHARE_MODE.FILE_SHARE_WRITE,
			lpSecurityAttributes: null,
			FILE_CREATION_DISPOSITION.OPEN_EXISTING,
			FILE_FLAGS_AND_ATTRIBUTES.FILE_ATTRIBUTE_NORMAL,
			hTemplateFile: null);

		if (handle.IsInvalid)
		{
			var err = Marshal.GetLastWin32Error();
			throw new Win32Exception(err, $"CreateFile failed for '{physicalDiskPath}'.");
		}
		return handle;
	}

	static unsafe uint ReadDeviceNumber(SafeFileHandle handle)
	{
		// IOCTL_STORAGE_GET_DEVICE_NUMBER returns STORAGE_DEVICE_NUMBER (12 bytes layout):
		//   DEVICE_TYPE DeviceType  (4)
		//   ULONG       DeviceNumber (4)
		//   ULONG       PartitionNumber (4)
		const uint IOCTL_STORAGE_GET_DEVICE_NUMBER = 0x002D1080;
		var buffer = stackalloc byte[12];
		uint bytesReturned;
		if (!PInvoke.DeviceIoControl(
				handle,
				IOCTL_STORAGE_GET_DEVICE_NUMBER,
				lpInBuffer: null, nInBufferSize: 0,
				lpOutBuffer: buffer, nOutBufferSize: 12,
				&bytesReturned,
				lpOverlapped: null))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(),
				"IOCTL_STORAGE_GET_DEVICE_NUMBER failed.");
		}
		return *(uint*)(buffer + 4);
	}

	static unsafe long ReadDiskLength(SafeFileHandle handle)
	{
		var lengthInfo = default(DiskLayoutStructs.GET_LENGTH_INFORMATION);
		uint bytesReturned;
		if (!PInvoke.DeviceIoControl(
				handle,
				DiskLayoutStructs.IOCTL_DISK_GET_LENGTH_INFO,
				lpInBuffer: null, nInBufferSize: 0,
				lpOutBuffer: &lengthInfo, nOutBufferSize: (uint)sizeof(DiskLayoutStructs.GET_LENGTH_INFORMATION),
				&bytesReturned,
				lpOverlapped: null))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "IOCTL_DISK_GET_LENGTH_INFO failed.");
		}
		return lengthInfo.Length;
	}

	static unsafe void CreateGptDisk(SafeFileHandle handle)
	{
		var createDisk = new DiskLayoutStructs.CREATE_DISK_GPT_VARIANT
		{
			PartitionStyle = DiskLayoutStructs.PARTITION_STYLE_GPT,
			DiskId = Guid.NewGuid(),
			MaxPartitionCount = 128,
		};

		uint bytesReturned;
		if (!PInvoke.DeviceIoControl(
				handle,
				DiskLayoutStructs.IOCTL_DISK_CREATE_DISK,
				lpInBuffer: &createDisk,
				nInBufferSize: (uint)sizeof(DiskLayoutStructs.CREATE_DISK_GPT_VARIANT),
				lpOutBuffer: null, nOutBufferSize: 0,
				&bytesReturned,
				lpOverlapped: null))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(),
				"IOCTL_DISK_CREATE_DISK failed.");
		}
	}

	static unsafe void WriteSinglePartitionLayout(SafeFileHandle handle, long diskSize)
	{
		var headerSize = sizeof(DiskLayoutStructs.DRIVE_LAYOUT_INFORMATION_EX_GPT_HEADER);
		var entrySize = sizeof(DiskLayoutStructs.PARTITION_INFORMATION_EX_GPT);
		var totalSize = headerSize + entrySize;

		var buffer = stackalloc byte[totalSize];
		// Zero the buffer (stackalloc isn't required to be zeroed in older specs).
		new Span<byte>(buffer, totalSize).Clear();

		const long startingOffset = PartitionAlignment;
		var partitionLength = diskSize - startingOffset - GptReservedTail;
		if (partitionLength <= 0)
		{
			throw new InvalidOperationException(
				$"Disk too small ({diskSize} bytes) — no usable partition space after GPT reserve.");
		}

		var header = (DiskLayoutStructs.DRIVE_LAYOUT_INFORMATION_EX_GPT_HEADER*)buffer;
		header->PartitionStyle = DiskLayoutStructs.PARTITION_STYLE_GPT;
		header->PartitionCount = 1;
		header->DiskId = Guid.NewGuid();
		header->StartingUsableOffset = startingOffset;
		header->UsableLength = partitionLength;
		header->MaxPartitionCount = 128;

		var entry = (DiskLayoutStructs.PARTITION_INFORMATION_EX_GPT*)(buffer + headerSize);
		entry->PartitionStyle = DiskLayoutStructs.PARTITION_STYLE_GPT;
		entry->StartingOffset = startingOffset;
		entry->PartitionLength = partitionLength;
		entry->PartitionNumber = 1;
		entry->RewritePartition = 1;
		entry->IsServicePartition = 0;
		entry->PartitionType = DiskLayoutStructs.PartitionBasicDataGuid;
		entry->PartitionId = Guid.NewGuid();
		entry->Attributes = 0;
		// Name[] left as zero bytes — Windows accepts no name.

		uint bytesReturned;
		if (!PInvoke.DeviceIoControl(
				handle,
				DiskLayoutStructs.IOCTL_DISK_SET_DRIVE_LAYOUT_EX,
				lpInBuffer: buffer, nInBufferSize: (uint)totalSize,
				lpOutBuffer: null, nOutBufferSize: 0,
				&bytesReturned,
				lpOverlapped: null))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(),
				"IOCTL_DISK_SET_DRIVE_LAYOUT_EX failed.");
		}
	}

	static unsafe void UpdateProperties(SafeFileHandle handle)
	{
		uint bytesReturned;
		if (!PInvoke.DeviceIoControl(
				handle,
				DiskLayoutStructs.IOCTL_DISK_UPDATE_PROPERTIES,
				lpInBuffer: null, nInBufferSize: 0,
				lpOutBuffer: null, nOutBufferSize: 0,
				&bytesReturned,
				lpOverlapped: null))
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(),
				"IOCTL_DISK_UPDATE_PROPERTIES failed.");
		}
	}

	void FormatNtfs(string volumeGuidPath, string label)
	{
		// FormatEx wants "\\?\Volume{...}\" — i.e. ending with backslash.
		var driveRoot = volumeGuidPath.TrimEnd('\\') + "\\";
		var safeLabel = label.Length > 32
			? label[..32]
			: label;

		FmIfsImports.FmIfsPacketType? finalStatus = null;

		FmIfsImports.FormatEx(
			driveRoot,
			FmIfsImports.FmIfsMediaType.FmMediaFixed,
			"NTFS",
			safeLabel,
			quickFormat: true,
			clusterSize: 0,
			Callback);

		if (finalStatus is not FmIfsImports.FmIfsPacketType.FmIfsFinished)
		{
			throw new InvalidOperationException(
				$"FormatEx did not finish successfully (last status: {finalStatus?.ToString() ?? "no callback"}).");
		}
		logger.LogInformation(
			"NTFS quick format completed for {Volume} (label={Label})",
				driveRoot, safeLabel);
		return;

		bool Callback(
			FmIfsImports.FmIfsPacketType packetType,
			uint packetLength,
			IntPtr packetData)
		{
			switch (packetType)
			{
				case FmIfsImports.FmIfsPacketType.FmIfsPercentCompleted:
					if (packetLength >= 4)
					{
						var pct = Marshal.ReadInt32(packetData);
						logger.LogDebug("Format progress: {Percent}%", pct);
					}
					break;
				case FmIfsImports.FmIfsPacketType.FmIfsFinished:
					finalStatus = packetType;
					break;
				case FmIfsImports.FmIfsPacketType.FmIfsAccessDenied:
				case FmIfsImports.FmIfsPacketType.FmIfsCantQuickFormat:
				case FmIfsImports.FmIfsPacketType.FmIfsIoError:
				case FmIfsImports.FmIfsPacketType.FmIfsBadLabel:
				case FmIfsImports.FmIfsPacketType.FmIfsIncompatibleFileSystem:
				case FmIfsImports.FmIfsPacketType.FmIfsClusterSizeTooSmall:
				case FmIfsImports.FmIfsPacketType.FmIfsClusterSizeTooBig:
				case FmIfsImports.FmIfsPacketType.FmIfsVolumeTooSmall:
				case FmIfsImports.FmIfsPacketType.FmIfsVolumeTooBig:
				case FmIfsImports.FmIfsPacketType.FmIfsMediaWriteProtected:
				case FmIfsImports.FmIfsPacketType.FmIfsNoMediaInDevice:
					finalStatus = packetType;
					logger.LogError("FormatEx error packet: {Packet}", packetType);
					break;
			}
			return true;
		}
	}
}

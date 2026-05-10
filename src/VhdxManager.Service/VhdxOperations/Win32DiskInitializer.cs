using System.ComponentModel;
using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32.SafeHandles;
using VhdxManager.Service.Native;
using Windows.Win32;
using Windows.Win32.Storage.FileSystem;

namespace VhdxManager.Service.VhdxOperations;

/// <summary>
/// Initializes a freshly attached raw VHDX disk: GPT partition table, one
/// primary basic-data partition, then a quick filesystem format (ReFS or NTFS).
/// Win32 P/Invoke for partitioning, PowerShell shell-out for the format pass.
/// </summary>
[SupportedOSPlatform("windows5.1.2600")]
[SuppressMessage("Interoperability", "CA1416", Justification = "Service is windows-only (net10.0-windows).")]
public sealed class Win32DiskInitializer(
	IVolumeManager volumeManager,
	ILogger<Win32DiskInitializer> logger) : IDiskInitializer
{
	const long PartitionAlignment = 1L * 1024 * 1024;        // align partition start to 1 MB
	const long GptReservedTail = 1L * 1024 * 1024;           // leave 1 MB at the end for GPT backup header

	public Task InitializeAndFormatAsync(string physicalDiskPath, string label, string filesystem, CancellationToken ct)
		=> Task.Run(() => InitializeAndFormatCore(physicalDiskPath, label, filesystem, ct), ct);

	void InitializeAndFormatCore(string physicalDiskPath, string label, string filesystem, CancellationToken ct)
	{
		var fs = NormalizeFilesystem(filesystem);

		var diskNumber = ParseDiskNumber(physicalDiskPath);
		if (diskNumber == 0)
		{
			throw new InvalidOperationException(
				$"Refusing to operate on physical disk 0 (system disk). Got '{physicalDiskPath}'.");
		}

		logger.LogInformation(
			"Initializing physical disk {DiskNumber} (path={Path}) with GPT + {Filesystem} (label={Label})",
				diskNumber, physicalDiskPath, fs, label);

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

		FormatVolume(volumeGuid, label, fs);
		logger.LogInformation("Disk {DiskNumber} initialized and formatted as {Filesystem}.", diskNumber, fs);
	}

	/// <summary>
	/// Validates and canonicalises the requested filesystem name. Empty string
	/// resolves to <c>ReFS</c> (project default). Anything outside the small
	/// allow-list is rejected here rather than at PowerShell time so we get a
	/// crisp error before any destructive disk work.
	/// </summary>
	static string NormalizeFilesystem(string filesystem)
	{
		if (string.IsNullOrWhiteSpace(filesystem))
		{
			return "ReFS";
		}
		return filesystem.Trim().ToUpperInvariant() switch
		{
			"REFS" => "ReFS",
			"NTFS" => "NTFS",
			_ => throw new ArgumentException(
				$"Unsupported filesystem '{filesystem}'. Use 'ReFS' or 'NTFS'.",
				nameof(filesystem)),
		};
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

	void FormatVolume(string volumeGuidPath, string label, string filesystem)
	{
		// Format-Volume wants "\\?\Volume{...}\" — must end with backslash.
		var driveRoot = volumeGuidPath.TrimEnd('\\') + "\\";
		var safeLabel = label.Length > 32 ? label[..32] : label;

		// Why shell out to PowerShell instead of FormatEx (fmifs.dll) directly?
		// fmifs has a notoriously fragile P/Invoke contract: subtle calling-convention
		// or bool/BOOLEAN size mismatches caused the service to FAIL_FAST with
		// STATUS_STACK_BUFFER_OVERRUN (0xc0000409). PowerShell's Format-Volume cmdlet
		// (from the Storage module, ships with Windows) accepts a volume GUID path
		// directly and is rock solid.
		// `filesystem` has already been validated by NormalizeFilesystem to be one of
		// the small allow-list {ReFS, NTFS}, so it is safe to inline here.
		var script = string.Format(
			System.Globalization.CultureInfo.InvariantCulture,
			"$ErrorActionPreference='Stop'; Format-Volume -Path '{0}' -FileSystem {1} -NewFileSystemLabel '{2}' -Confirm:$false -Force | Out-Null",
			driveRoot.Replace("'", "''", StringComparison.Ordinal),
			filesystem,
			safeLabel.Replace("'", "''", StringComparison.Ordinal));

		var psi = new ProcessStartInfo
		{
			FileName = "powershell.exe",
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
			CreateNoWindow = true,
		};
		psi.ArgumentList.Add("-NoProfile");
		psi.ArgumentList.Add("-NonInteractive");
		psi.ArgumentList.Add("-ExecutionPolicy");
		psi.ArgumentList.Add("Bypass");
		psi.ArgumentList.Add("-Command");
		psi.ArgumentList.Add(script);

		// Wrap powershell in a Job Object so it dies with the service if the host
		// crashes mid-format. Otherwise an orphaned Format-Volume could continue
		// against an attached VHDX with no service to track its state.
		using var processGroup = new ProcessGroup();
		using var process = processGroup.Start(psi);

		var stdoutBuf = new StringBuilder();
		var stderrBuf = new StringBuilder();
		process.OutputDataReceived += (_, e) => { if (e.Data is not null) stdoutBuf.AppendLine(e.Data); };
		process.ErrorDataReceived += (_, e) => { if (e.Data is not null) stderrBuf.AppendLine(e.Data); };
		process.BeginOutputReadLine();
		process.BeginErrorReadLine();

		// Quick NTFS format on a small VHDX is normally seconds; allow up to 5 min for
		// huge fixed disks. Format-Volume itself is bounded by the underlying API.
		if (!process.WaitForExit(5 * 60_000))
		{
			try { process.Kill(entireProcessTree: true); } catch { /* best-effort */ }
			throw new InvalidOperationException("Format-Volume timed out after 5 minutes.");
		}

		if (process.ExitCode != 0)
		{
			throw new InvalidOperationException(
				$"Format-Volume failed (exit {process.ExitCode}). Stderr: {stderrBuf.ToString().Trim()}");
		}

		logger.LogInformation(
			"{Filesystem} quick format completed for {Volume} (label={Label})", filesystem, driveRoot, safeLabel);
	}
}

using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace VhdxCow.Service.VhdxOperations;

/// <summary>
/// Volume mount point operations via Win32 APIs.
/// Discovers volume GUIDs by matching physical disk numbers via IOCTL,
/// then uses SetVolumeMountPoint/DeleteVolumeMountPoint for folder mounting.
/// </summary>
public sealed class VolumeManager(ILogger<VolumeManager> logger) : IVolumeManager
{
	// IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = CTL_CODE(IOCTL_VOLUME_BASE, 0, METHOD_BUFFERED, FILE_ANY_ACCESS)
	// = (0x56 << 16) | 0 = 0x00560000
	// ReSharper disable InconsistentNaming
	const uint IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS = 0x00560000;
	// ReSharper restore InconsistentNaming

	public Task<string> GetVolumeGuidPathAsync(string physicalDiskPath, CancellationToken ct) => Task.Run(() =>
	{
		logger.LogInformation("Discovering volume GUID for {PhysicalDiskPath}", physicalDiskPath);

		var targetDiskNumber = ParseDiskNumber(physicalDiskPath);
		if (FindVolumeForDisk(targetDiskNumber) is not {} volumeGuid)
		{
			throw new InvalidOperationException(
				$"No volume found on physical disk {targetDiskNumber}. The VHDX may not contain a formatted partition.");
		}

		logger.LogInformation(
			"Volume GUID discovered: {PhysicalDiskPath} -> {VolumeGuid}",
			physicalDiskPath, volumeGuid);

		return volumeGuid;
	}, ct);

	public Task MountToFolderAsync(string volumeGuidPath, string mountPath, CancellationToken ct) => Task.Run(() =>
	{
		logger.LogInformation(
			"Mounting volume {VolumeGuid} to {MountPath}", volumeGuidPath, mountPath);

		if (!Directory.Exists(mountPath))
		{
			Directory.CreateDirectory(mountPath);
		}

		// Both paths must end with a backslash for Win32
		var normalizedPath = mountPath.TrimEnd('\\') + "\\";
		var normalizedVolume = volumeGuidPath.TrimEnd('\\') + "\\";
		if (!PInvoke.SetVolumeMountPoint(normalizedPath, normalizedVolume))
		{
			var error = Marshal.GetLastWin32Error();
			logger.LogError(
				"SetVolumeMountPoint failed: {MountPath} -> {VolumeGuid}, Error={ErrorCode} ({ErrorMessage})",
					normalizedPath, normalizedVolume, error, new Win32Exception(error).Message);
			throw new Win32Exception(error, $"SetVolumeMountPoint failed for '{mountPath}'");
		}

		logger.LogInformation("Volume mounted: {VolumeGuid} -> {MountPath}", volumeGuidPath, mountPath);
	}, ct);

	public Task UnmountFolderAsync(string mountPath, CancellationToken ct) => Task.Run(() =>
	{
		logger.LogInformation("Unmounting volume from {MountPath}", mountPath);

		var normalizedPath = mountPath.TrimEnd('\\') + "\\";
		if (!PInvoke.DeleteVolumeMountPoint(normalizedPath))
		{
			var error = Marshal.GetLastWin32Error();
			logger.LogError(
				"DeleteVolumeMountPoint failed: {MountPath}, Error={ErrorCode} ({ErrorMessage})",
					normalizedPath, error, new Win32Exception(error).Message);
			throw new Win32Exception(error, $"DeleteVolumeMountPoint failed for '{mountPath}'");
		}

		logger.LogInformation("Volume unmounted from {MountPath}", mountPath);
	}, ct);

	static uint ParseDiskNumber(string physicalDiskPath)
	{
		const string prefix = @"\\.\PhysicalDrive";
		if (!physicalDiskPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
		{
			throw new ArgumentException($"Expected path like '{prefix}N', got '{physicalDiskPath}'", nameof(physicalDiskPath));
		}

		if (!uint.TryParse(physicalDiskPath[prefix.Length..], out var diskNumber))
		{
			throw new ArgumentException(
				$"Cannot parse disk number from '{physicalDiskPath}'",
				nameof(physicalDiskPath));
		}

		return diskNumber;
	}

	/// <summary>
	/// Enumerates all volumes via FindFirstVolume/FindNextVolume,
	/// checks which physical disk each belongs to via IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
	/// and returns the volume GUID path matching the target disk number.
	/// </summary>
	static string? FindVolumeForDisk(uint targetDiskNumber)
	{
		var volumeName = new char[Win32PathLimits];

		using var findHandle = PInvoke.FindFirstVolume(volumeName);
		if (findHandle.IsInvalid)
		{
			throw new Win32Exception(Marshal.GetLastWin32Error(), "FindFirstVolume failed");
		}

		do
		{
			var volumeGuid = new string(volumeName.AsSpan().SliceAtNull());
			if (TryGetDiskNumberViaIoctl(volumeGuid, out var diskNumber) && diskNumber == targetDiskNumber)
			{
				return volumeGuid;
			}

			Array.Clear(volumeName);
		}
		while (PInvoke.FindNextVolume(new HANDLE(findHandle.DangerousGetHandle()), volumeName));

		return null;
	}

	/// <summary>
	/// Opens the volume and uses IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS to determine
	/// which physical disk the volume belongs to.
	/// </summary>
	static unsafe bool TryGetDiskNumberViaIoctl(string volumeGuidPath, out uint diskNumber)
	{
		diskNumber = 0;

		// Remove trailing backslash for CreateFile
		var volumePath = volumeGuidPath.TrimEnd('\\');

		SafeHandle handle;
		try
		{
			handle = File.OpenHandle(
				volumePath,
				FileMode.Open,
				FileAccess.Read,
				FileShare.ReadWrite | FileShare.Delete);
		}
		catch
		{
			return false;
		}

		using (handle)
		{
			if (handle.IsInvalid)
				return false;

			// VOLUME_DISK_EXTENTS structure:
			//   DWORD NumberOfDiskExtents     (offset 0)
			//   padding                       (offset 4, for 8-byte alignment)
			//   DISK_EXTENT[]:
			//     DWORD DiskNumber            (offset 8)
			//     padding                     (offset 12)
			//     LONGLONG StartingOffset     (offset 16)
			//     LONGLONG ExtentLength       (offset 24)
			var buffer = new byte[256];
			uint bytesReturned = 0;
			bool success;

			fixed (byte* pBuffer = buffer)
			{
				success = PInvoke.DeviceIoControl(
					hDevice: handle,
					dwIoControlCode: IOCTL_VOLUME_GET_VOLUME_DISK_EXTENTS,
					lpInBuffer: null,
					nInBufferSize: 0,
					lpOutBuffer: pBuffer,
					nOutBufferSize: (uint)buffer.Length,
					lpBytesReturned: &bytesReturned,
					lpOverlapped: null);
			}

			if (!success || bytesReturned < 12)
				return false;

			var numberOfExtents = BitConverter.ToUInt32(buffer, 0);
			if (numberOfExtents == 0)
				return false;

			// First DISK_EXTENT.DiskNumber at offset 8
			diskNumber = BitConverter.ToUInt32(buffer, 8);
			return true;
		}
	}
}

file static class SpanExtensions
{
	public static ReadOnlySpan<char> SliceAtNull(this ReadOnlySpan<char> span)
	{
		var nullIndex = span.IndexOf('\0');
		return nullIndex >= 0 ? span[..nullIndex] : span;
	}
}

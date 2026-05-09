using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VhdxCow.Service.Native;

/// <summary>
/// Win32 disk-layout structs and IOCTLs we need for partition initialization.
/// CsWin32 generates these as untagged unions which are awkward to populate
/// from C#; defining them by hand gives a stable, sequential layout we can
/// stackalloc and write into directly.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class DiskLayoutStructs
{
	// CTL_CODE(IOCTL_DISK_BASE=0x07, function, METHOD_BUFFERED=0, FILE_READ_ACCESS|FILE_WRITE_ACCESS=0x3 << 14)
	// IOCTL_DISK_CREATE_DISK = CTL_CODE(0x07, 0x16, 0, 3)
	public const uint IOCTL_DISK_CREATE_DISK = 0x0007C058;

	// IOCTL_DISK_SET_DRIVE_LAYOUT_EX = CTL_CODE(0x07, 0x15, 0, 3)
	public const uint IOCTL_DISK_SET_DRIVE_LAYOUT_EX = 0x0007C054;

	// IOCTL_DISK_UPDATE_PROPERTIES       = CTL_CODE(0x07, 0x50, 0, 0)   (any access)
	public const uint IOCTL_DISK_UPDATE_PROPERTIES = 0x00070140;

	// IOCTL_DISK_GET_LENGTH_INFO = CTL_CODE(0x07, 0x17, 0, 1)   (read access)
	public const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

	public const int PARTITION_STYLE_MBR = 0;
	public const int PARTITION_STYLE_GPT = 1;
	public const int PARTITION_STYLE_RAW = 2;

	public static readonly Guid PartitionBasicDataGuid = new("EBD0A0A2-B9E5-4433-87C0-68B6B72699C7");

	[StructLayout(LayoutKind.Sequential)]
	public struct GET_LENGTH_INFORMATION
	{
		public long Length;
	}

	// CREATE_DISK with GPT body. (MBR variant unused.)
	[StructLayout(LayoutKind.Sequential)]
	public struct CREATE_DISK_GPT_VARIANT
	{
		public int PartitionStyle; // PARTITION_STYLE_GPT
		public Guid DiskId;
		public uint MaxPartitionCount;
	}

	// Header of DRIVE_LAYOUT_INFORMATION_EX with GPT body.
	[StructLayout(LayoutKind.Sequential)]
	public struct DRIVE_LAYOUT_INFORMATION_EX_GPT_HEADER
	{
		public int PartitionStyle; // PARTITION_STYLE_GPT
		public uint PartitionCount;
		// DRIVE_LAYOUT_INFORMATION_GPT inline:
		public Guid DiskId;
		public long StartingUsableOffset;
		public long UsableLength;
		public uint MaxPartitionCount;
		// 4-byte padding before PARTITION_INFORMATION_EX which has long-aligned fields
		public uint _pad;
	}

	// PARTITION_INFORMATION_EX with GPT body. Total size: 144 bytes (verified
	// against Windows SDK headers — DDK winioctl.h).
	[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode, Size = 144)]
	public unsafe struct PARTITION_INFORMATION_EX_GPT
	{
		public int PartitionStyle; // PARTITION_STYLE_GPT
		public long StartingOffset;
		public long PartitionLength;
		public uint PartitionNumber;
		public byte RewritePartition; // BOOLEAN
		public byte IsServicePartition; // BOOLEAN
		public byte _pad1, _pad2;
		// PARTITION_INFORMATION_GPT inline:
		public Guid PartitionType;
		public Guid PartitionId;
		public ulong Attributes;
		// Name[36] inline as fixed buffer of WCHARs.
		public fixed char Name[36];
	}
}

using System.Runtime.InteropServices;
using System.Runtime.Versioning;

namespace VhdxCow.Service.Native;

/// <summary>
/// FormatEx via fmifs.dll. The function is not in the public Win32 metadata
/// (no CsWin32 binding), but its signature is well-known and stable since
/// Windows 2000 — it's what format.com itself uses internally.
///
/// Callback delivers progress and final status as a string-typed enum.
/// </summary>
[SupportedOSPlatform("windows")]
internal static class FmIfsImports
{
	public enum FmIfsPacketType
	{
		FmIfsPercentCompleted = 0,
		FmIfsFormatReport = 1,
		FmIfsDoneWithStructure = 2,
		FmIfsUnknown2 = 3,
		FmIfsUnknown3 = 4,
		FmIfsUnknown4 = 5,
		FmIfsUnknown5 = 6,
		FmIfsInsertDisk = 7,
		FmIfsUnknown7 = 8,
		FmIfsFormattingDestination = 9,
		FmIfsIncompatibleFileSystem = 10,
		FmIfsFormattingFileSystem = 11,
		FmIfsAccessDenied = 12,
		FmIfsMediaWriteProtected = 13,
		FmIfsCantQuickFormat = 14,
		FmIfsIoError = 15,
		FmIfsFinished = 16,
		FmIfsBadLabel = 17,
		FmIfsUnknown15 = 18,
		FmIfsCheckOnReboot = 19,
		FmIfsTextMessage = 20,
		FmIfsHiddenStatus = 21,
		FmIfsClusterSizeTooSmall = 22,
		FmIfsClusterSizeTooBig = 23,
		FmIfsVolumeTooSmall = 24,
		FmIfsVolumeTooBig = 25,
		FmIfsNoMediaInDevice = 26,
		FmIfsClustersCountBeyond32Bits = 27,
		FmIfsCantChkMultiVolumeOfSameFS = 28,
		FmIfsFormatFatUsing64KCluster = 29,
		FmIfsDeviceOffLine = 30,
	}

	public enum FmIfsMediaType
	{
		FmMediaUnknown = 0,
		FmMediaF5_160_512 = 1,
		FmMediaF5_180_512 = 2,
		FmMediaF5_320_512 = 3,
		FmMediaF5_320_1024 = 4,
		FmMediaF5_360_512 = 5,
		FmMediaF3_720_512 = 6,
		FmMediaF5_1Pt2_512 = 7,
		FmMediaF3_1Pt44_512 = 8,
		FmMediaF3_2Pt88_512 = 9,
		FmMediaF3_20Pt8_512 = 10,
		FmMediaRemovable = 11,
		FmMediaFixed = 12,
		FmMediaF3_120M_512 = 13,
		FmMediaF3_640_512 = 14,
		FmMediaF5_640_512 = 15,
		FmMediaF5_720_512 = 16,
		FmMediaF3_1Pt2_512 = 17,
		FmMediaF3_1Pt23_1024 = 18,
		FmMediaF5_1Pt23_1024 = 19,
		FmMediaF3_128Mb_512 = 20,
		FmMediaF3_230Mb_512 = 21,
		FmMediaEndOfData = 22,
	}

	[UnmanagedFunctionPointer(CallingConvention.StdCall, CharSet = CharSet.Unicode)]
	public delegate bool FmIfsCallback(
		FmIfsPacketType packetType,
		uint packetLength,
		IntPtr packetData);

	[DllImport("fmifs.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
	public static extern void FormatEx(
		string driveRoot,
		FmIfsMediaType mediaType,
		string fileSystemName,
		string label,
		[MarshalAs(UnmanagedType.Bool)] bool quickFormat,
		uint clusterSize,
		FmIfsCallback callback);
}

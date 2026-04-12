global using static VhdxCow.Service.Globals;

namespace VhdxCow.Service;

/// <summary>
/// Global constants and statics for the VhdxCow Service.
/// These are shared across multiple classes and don't belong to any specific class.
/// </summary>
static class Globals
{
	/// <summary>
	/// Win32PathLimits — maximum length of a traditional Win32 path, including null terminator.
	/// Applies to paths passed to most Win32 APIs: CreateFile, GetVirtualDiskPhysicalPath,
	/// FindFirstVolume/FindNextVolume, SetVolumeMountPoint, etc.
	/// </summary>
	public const int Win32PathLimits = 260;
}

namespace VhdxManager.Contracts;

/// <summary>
/// Shared constants for the VhdxManager service identity.
/// Single source of truth used by both the service host and its clients.
/// </summary>
public static class ServiceConstants
{
	/// <summary>
	/// Name of the Windows named pipe used for gRPC communication.
	/// </summary>
	public const string PipeName = "VhdxManagerService";

	/// <summary>
	/// Windows service registry name (used with sc.exe and Service Control Manager).
	/// </summary>
	public const string ServiceName = "VhdxManagerService";
}

namespace VhdxCow.Contracts;

/// <summary>
/// Shared constants for the VhdxCow service identity.
/// Single source of truth used by both the service host and its clients.
/// </summary>
public static class ServiceConstants
{
	/// <summary>
	/// Name of the Windows named pipe used for gRPC communication.
	/// </summary>
	public const string PipeName = "VhdxCowService";

	/// <summary>
	/// Windows service registry name (used with sc.exe and Service Control Manager).
	/// </summary>
	public const string ServiceName = "VhdxCowService";
}

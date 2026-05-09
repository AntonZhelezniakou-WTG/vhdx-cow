namespace VhdxManager.Client;

/// <summary>
/// Thrown when the VhdxManager service is unreachable (not running, pipe not found, etc.).
/// </summary>
public sealed class VhdxManagerServiceException(string message, Exception? innerException = null)
	: Exception(message, innerException);

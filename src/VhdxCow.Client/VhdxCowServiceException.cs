namespace VhdxCow.Client;

/// <summary>
/// Thrown when the VhdxCow service is unreachable (not running, pipe not found, etc.).
/// </summary>
public sealed class VhdxCowServiceException(string message, Exception? innerException = null)
	: Exception(message, innerException);

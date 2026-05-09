namespace VhdxManager.Service.VhdxOperations;

/// <summary>
/// Orchestrates the "convert folder → VHDX-mounted folder" workflow:
/// rename source aside (staging), create + mount a fresh VHDX in its place,
/// robocopy contents back, optionally delete the staging directory.
/// All steps are best-effort revertible while the original data is still on disk.
/// </summary>
public interface IFolderTransferOrchestrator
{
	Task<ConvertFolderResult> ConvertFolderAsync(
		string folderPath,
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string ntfsLabel,
		bool deleteStaging,
		CancellationToken ct = default);
}

public sealed record ConvertFolderResult(
	bool Success,
	string? ErrorMessage,
	string StagingFolderPath,
	long FilesCopied,
	long BytesCopied,
	string VolumeGuidPath);

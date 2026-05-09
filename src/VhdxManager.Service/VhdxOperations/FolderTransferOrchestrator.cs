using System.Globalization;

namespace VhdxManager.Service.VhdxOperations;

public sealed class FolderTransferOrchestrator(
	IVirtDiskManager virtDiskManager,
	IDiskInitializer diskInitializer,
	IVolumeManager volumeManager,
	Robocopy robocopy,
	ILogger<FolderTransferOrchestrator> logger)
	: IFolderTransferOrchestrator
{
	public async Task<ConvertFolderResult> ConvertFolderAsync(
		string folderPath,
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string ntfsLabel,
		bool deleteStaging,
		CancellationToken ct)
	{
		// 1. Validate inputs.
		if (!Directory.Exists(folderPath))
		{
			return Failed("(none)", $"Source folder does not exist: {folderPath}");
		}
		if (File.Exists(vhdxPath))
		{
			return Failed("(none)", $"Destination VHDX already exists: {vhdxPath}");
		}

		var stagingPath = ComputeStagingPath(folderPath);
		logger.LogInformation(
			"Convert folder: {Folder} -> VHDX {Vhdx}, staging={Staging}",
				folderPath, vhdxPath, stagingPath);

		var stage = ConvertStage.Initial;
		string? physicalPath = null;

		try
		{
			// 2. Rename source out of the way.
			Directory.Move(folderPath, stagingPath);
			stage = ConvertStage.Renamed;
			ct.ThrowIfCancellationRequested();

			// 3. Recreate empty mount target (must exist for SetVolumeMountPoint).
			Directory.CreateDirectory(folderPath);
			stage = ConvertStage.MountTargetCreated;
			ct.ThrowIfCancellationRequested();

			// 4. Create + attach + initialize + mount VHDX.
			await virtDiskManager.CreateStandaloneVhdxAsync(vhdxPath, sizeBytes, dynamic, ct);
			stage = ConvertStage.VhdxCreated;
			ct.ThrowIfCancellationRequested();

			physicalPath = await virtDiskManager.AttachAsync(vhdxPath, ct);
			stage = ConvertStage.VhdxAttached;
			ct.ThrowIfCancellationRequested();

			await diskInitializer.InitializeAndFormatAsync(physicalPath, ntfsLabel, ct);
			stage = ConvertStage.DiskInitialized;
			ct.ThrowIfCancellationRequested();

			var volumeGuid = await volumeManager.GetVolumeGuidPathAsync(physicalPath, ct);
			await volumeManager.MountToFolderAsync(volumeGuid, folderPath, ct);
			stage = ConvertStage.Mounted;
			ct.ThrowIfCancellationRequested();

			// 5. Robocopy staged content back into the now-mounted folder.
			var copyResult = await robocopy.MirrorAsync(stagingPath, folderPath, ct);
			if (!copyResult.IsSuccess)
			{
				logger.LogError(
					"Robocopy failed (exit code {ExitCode}); leaving staging at {Staging}",
						copyResult.ExitCode, stagingPath);
				return new ConvertFolderResult(
					Success: false,
					ErrorMessage:
						$"Robocopy reported errors (exit={copyResult.ExitCode}); " +
						$"original data preserved at {stagingPath}",
					StagingFolderPath: stagingPath,
					FilesCopied: copyResult.FilesCopied,
					BytesCopied: copyResult.BytesCopied,
					VolumeGuidPath: volumeGuid);
			}

			// 6. Optional cleanup of staging.
			if (deleteStaging)
			{
				try
				{
					Directory.Delete(stagingPath, recursive: true);
					logger.LogInformation("Staging deleted: {Staging}", stagingPath);
				}
				catch (Exception ex)
				{
					logger.LogWarning(ex,
						"Failed to delete staging folder {Staging}; left in place for manual cleanup",
							stagingPath);
				}
			}

			return new ConvertFolderResult(
				Success: true,
				ErrorMessage: null,
				StagingFolderPath: stagingPath,
				FilesCopied: copyResult.FilesCopied,
				BytesCopied: copyResult.BytesCopied,
				VolumeGuidPath: volumeGuid);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Convert folder failed at stage {Stage}", stage);
			TryRollback(stage, folderPath, stagingPath, vhdxPath, physicalPath);
			return Failed(stagingPath, $"Convert failed at stage {stage}: {ex.Message}");
		}
	}

	void TryRollback(
		ConvertStage stage,
		string folderPath,
		string stagingPath,
		string vhdxPath,
		string? physicalPath)
	{
		// Best-effort. We try to undo work in reverse order; failures are logged
		// but never thrown — we'd otherwise mask the original error.
		try
		{
			if (stage >= ConvertStage.Mounted)
			{
				volumeManager.UnmountFolderAsync(folderPath, CancellationToken.None).GetAwaiter().GetResult();
			}
		}
		catch (Exception ex) { logger.LogWarning(ex, "Rollback unmount failed"); }

		try
		{
			if (stage >= ConvertStage.VhdxAttached)
			{
				virtDiskManager.DetachAsync(vhdxPath, CancellationToken.None).GetAwaiter().GetResult();
			}
		}
		catch (Exception ex) { logger.LogWarning(ex, "Rollback detach failed"); }

		try
		{
			if (stage >= ConvertStage.VhdxCreated && File.Exists(vhdxPath))
			{
				File.Delete(vhdxPath);
			}
		}
		catch (Exception ex) { logger.LogWarning(ex, "Rollback VHDX-file delete failed"); }

		try
		{
			if (stage >= ConvertStage.MountTargetCreated && Directory.Exists(folderPath))
			{
				// Should be empty (or the now-unmounted mount-point folder).
				if (Directory.EnumerateFileSystemEntries(folderPath).Any())
				{
					logger.LogWarning(
						"Mount-target folder {Folder} not empty after unmount; leaving for manual review",
							folderPath);
				}
				else
				{
					Directory.Delete(folderPath);
				}
			}
		}
		catch (Exception ex) { logger.LogWarning(ex, "Rollback delete-target failed"); }

		try
		{
			if (stage >= ConvertStage.Renamed
				&& Directory.Exists(stagingPath)
				&& !Directory.Exists(folderPath))
			{
				Directory.Move(stagingPath, folderPath);
				logger.LogInformation("Rollback restored {Staging} -> {Folder}", stagingPath, folderPath);
			}
		}
		catch (Exception ex) { logger.LogWarning(ex, "Rollback rename-back failed"); }

		_ = physicalPath; // unused in current rollback strategy; reserved for future reuse
	}

	static string ComputeStagingPath(string folderPath)
	{
		var timestamp = DateTime.UtcNow.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
		var candidate = $"{folderPath}.vhdxcow-staging-{timestamp}";
		var index = 0;
		while (Directory.Exists(candidate) || File.Exists(candidate))
		{
			candidate = $"{folderPath}.vhdxcow-staging-{timestamp}-{++index}";
		}
		return candidate;
	}

	static ConvertFolderResult Failed(string staging, string message) =>
		new(Success: false, ErrorMessage: message, StagingFolderPath: staging, FilesCopied: 0, BytesCopied: 0, VolumeGuidPath: string.Empty);

	enum ConvertStage
	{
		Initial,
		Renamed,
		MountTargetCreated,
		VhdxCreated,
		VhdxAttached,
		DiskInitialized,
		Mounted,
	}
}

using System.Diagnostics;
using Grpc.Core;
using VhdxCow.Contracts;
using VhdxCow.Service.Security;
using VhdxCow.Service.State;
using VhdxCow.Service.VhdxOperations;

namespace VhdxCow.Service.Services;

public sealed class VhdxGrpcService(
	IVhdxManager vhdxManager,
	IVolumeManager volumeManager,
	IDiskInitializer diskInitializer,
	IFolderTransferOrchestrator folderTransferOrchestrator,
	IStateStore stateStore,
	PathValidator pathValidator,
	ILogger<VhdxGrpcService> logger) : VhdxService.VhdxServiceBase
{
	public override Task<PingReply> Ping(PingRequest request, ServerCallContext context)
	{
		logger.LogInformation("Ping received");

		var reply = new PingReply
		{
			Version = typeof(VhdxGrpcService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
			ActiveMounts = (uint)stateStore.GetActiveMountCount(),
		};

		return Task.FromResult(reply);
	}

	public override async Task<CreateChildReply> CreateChild(CreateChildRequest request, ServerCallContext context)
	{
		logger.LogInformation(
			"CreateChild: Parent={ParentPath}, Child={ChildPath}, Mount={MountPath}",
				request.ParentVhdxPath, request.ChildVhdxPath, request.MountPath);

		if (!pathValidator.ValidateParentPath(request.ParentVhdxPath, out var parentError))
			return new CreateChildReply { Success = false, ErrorMessage = parentError };

		if (!pathValidator.ValidateChildPath(request.ChildVhdxPath, out var childError))
			return new CreateChildReply { Success = false, ErrorMessage = childError };

		if (!pathValidator.ValidateMountPath(request.MountPath, out var mountError))
			return new CreateChildReply { Success = false, ErrorMessage = mountError };

		if (!File.Exists(request.ParentVhdxPath))
			return new CreateChildReply { Success = false, ErrorMessage = $"Parent VHDX not found: {request.ParentVhdxPath}" };

		try
		{
			var sw = Stopwatch.StartNew();

			// 1. Create differencing disk
			await vhdxManager.CreateDifferencingDiskAsync(
				request.ParentVhdxPath, request.ChildVhdxPath, context.CancellationToken);

			// 2. Attach without drive letter, with permanent lifetime
			var physicalPath = await vhdxManager.AttachAsync(
				request.ChildVhdxPath, context.CancellationToken);

			// 3. Discover volume GUID on the attached disk
			var volumeGuidPath = await volumeManager.GetVolumeGuidPathAsync(
				physicalPath, context.CancellationToken);

			// 4. Mount volume to the target folder
			await volumeManager.MountToFolderAsync(
				volumeGuidPath, request.MountPath, context.CancellationToken);

			// 5. Persist state
			await stateStore.AddAsync(new MountedDiskState
			{
				ChildVhdxPath = request.ChildVhdxPath,
				ParentVhdxPath = request.ParentVhdxPath,
				MountPath = request.MountPath,
				VolumeGuidPath = volumeGuidPath,
			}, context.CancellationToken);

			sw.Stop();
			logger.LogInformation(
				"CreateChild completed in {Duration}ms: {ChildPath} -> {MountPath}",
					sw.ElapsedMilliseconds, request.ChildVhdxPath, request.MountPath);

			return new CreateChildReply
			{
				Success = true,
				VolumeGuidPath = volumeGuidPath,
			};
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "CreateChild failed for {ChildPath}", request.ChildVhdxPath);
			return new CreateChildReply { Success = false, ErrorMessage = ex.Message };
		}
	}

	public override async Task<ResetChildReply> ResetChild(ResetChildRequest request, ServerCallContext context)
	{
		logger.LogInformation("ResetChild: {ChildPath}", request.ChildVhdxPath);

		if (await stateStore.GetAsync(request.ChildVhdxPath, context.CancellationToken) is not {} state)
			return new ResetChildReply { Success = false, ErrorMessage = $"No tracked mount for '{request.ChildVhdxPath}'" };

		try
		{
			var sw = Stopwatch.StartNew();

			// 1. Unmount from folder
			await volumeManager.UnmountFolderAsync(state.MountPath, context.CancellationToken);

			// 2. Detach the VHDX
			await vhdxManager.DetachAsync(request.ChildVhdxPath, context.CancellationToken);

			// 3. Delete the child VHDX file
			File.Delete(request.ChildVhdxPath);

			// 4. Recreate differencing disk from the same parent
			await vhdxManager.CreateDifferencingDiskAsync(
				state.ParentVhdxPath, request.ChildVhdxPath, context.CancellationToken);

			// 5. Reattach
			var physicalPath = await vhdxManager.AttachAsync(
				request.ChildVhdxPath, context.CancellationToken);

			// 6. Rediscover volume GUID (may differ after recreate)
			var volumeGuidPath = await volumeManager.GetVolumeGuidPathAsync(
				physicalPath, context.CancellationToken);

			// 7. Remount to same folder
			await volumeManager.MountToFolderAsync(
				volumeGuidPath, state.MountPath, context.CancellationToken);

			// 8. Update state
			await stateStore.AddAsync(new MountedDiskState
			{
				ChildVhdxPath = request.ChildVhdxPath,
				ParentVhdxPath = state.ParentVhdxPath,
				MountPath = state.MountPath,
				VolumeGuidPath = volumeGuidPath,
			}, context.CancellationToken);

			sw.Stop();
			logger.LogInformation(
				"ResetChild completed in {Duration}ms: {ChildPath}",
					sw.ElapsedMilliseconds, request.ChildVhdxPath);

			return new ResetChildReply { Success = true };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "ResetChild failed for {ChildPath}", request.ChildVhdxPath);
			return new ResetChildReply { Success = false, ErrorMessage = ex.Message };
		}
	}

	public override async Task<DetachReply> Detach(DetachRequest request, ServerCallContext context)
	{
		logger.LogInformation("Detach: {ChildPath}", request.ChildVhdxPath);

		var state = await stateStore.GetAsync(request.ChildVhdxPath, context.CancellationToken);
		try
		{
			// 1. Unmount if we know the mount path
			if (state is not null)
			{
				await volumeManager.UnmountFolderAsync(state.MountPath, context.CancellationToken);
			}

			// 2. Detach VHDX
			await vhdxManager.DetachAsync(request.ChildVhdxPath, context.CancellationToken);

			// 3. Delete the child file
			if (File.Exists(request.ChildVhdxPath))
			{
				File.Delete(request.ChildVhdxPath);
			}

			// 4. Remove from state
			await stateStore.RemoveAsync(request.ChildVhdxPath, context.CancellationToken);

			logger.LogInformation("Detach completed: {ChildPath}", request.ChildVhdxPath);
			return new DetachReply { Success = true };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Detach failed for {ChildPath}", request.ChildVhdxPath);
			return new DetachReply { Success = false, ErrorMessage = ex.Message };
		}
	}

	public override async Task<GetStatusReply> GetStatus(GetStatusRequest request, ServerCallContext context)
	{
		logger.LogDebug("GetStatus: {ChildPath}", request.ChildVhdxPath);

		if (await stateStore.GetAsync(request.ChildVhdxPath, context.CancellationToken) is not {} state)
		{
			return new GetStatusReply { IsAttached = false };
		}

		var info = await vhdxManager.GetInfoAsync(request.ChildVhdxPath, context.CancellationToken);
		return new GetStatusReply
		{
			IsAttached = info.IsAttached,
			MountPath = state.MountPath,
			ParentVhdxPath = state.ParentVhdxPath,
			VolumeGuidPath = state.VolumeGuidPath,
			ChildSizeBytes = info.PhysicalSize,
		};
	}

	public override async Task<PublishReply> Publish(PublishRequest request, ServerCallContext context)
	{
		logger.LogInformation("Publish: merging overlay {OverlayPath}", request.OverlayVhdxPath);

		try
		{
			var sw = Stopwatch.StartNew();

			// 1. Get all active mounts — they'll need to be recreated
			var allMounts = await stateStore.GetAllAsync(context.CancellationToken);

			// 2. Detach all children (they reference the parent that will change)
			foreach (var mount in allMounts)
			{
				await volumeManager.UnmountFolderAsync(mount.MountPath, context.CancellationToken);
				await vhdxManager.DetachAsync(mount.ChildVhdxPath, context.CancellationToken);
				File.Delete(mount.ChildVhdxPath);
			}

			// 3. Detach the overlay itself
			await vhdxManager.DetachAsync(request.OverlayVhdxPath, context.CancellationToken);

			// 4. Merge overlay into parent
			await vhdxManager.MergeAsync(request.OverlayVhdxPath, context.CancellationToken);

			// 5. Recreate overlay (it was deleted by merge)
			// The caller is responsible for re-creating and re-attaching the overlay

			// 6. Recreate and reattach all children
			uint recreated = 0;
			foreach (var mount in allMounts)
			{
				await vhdxManager.CreateDifferencingDiskAsync(
					mount.ParentVhdxPath, mount.ChildVhdxPath, context.CancellationToken);

				var physicalPath = await vhdxManager.AttachAsync(
					mount.ChildVhdxPath, context.CancellationToken);

				var volumeGuidPath = await volumeManager.GetVolumeGuidPathAsync(
					physicalPath, context.CancellationToken);

				await volumeManager.MountToFolderAsync(
					volumeGuidPath, mount.MountPath, context.CancellationToken);

				await stateStore.AddAsync(new MountedDiskState
				{
					ChildVhdxPath = mount.ChildVhdxPath,
					ParentVhdxPath = mount.ParentVhdxPath,
					MountPath = mount.MountPath,
					VolumeGuidPath = volumeGuidPath,
				}, context.CancellationToken);

				recreated++;
			}

			sw.Stop();
			logger.LogInformation(
				"Publish completed in {Duration}ms: {ChildrenRecreated} children recreated",
					sw.ElapsedMilliseconds, recreated);

			return new PublishReply
			{
				Success = true,
				ChildrenRecreated = recreated,
			};
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Publish failed for overlay {OverlayPath}", request.OverlayVhdxPath);
			return new PublishReply { Success = false, ErrorMessage = ex.Message };
		}
	}

	public override async Task<ListMountsReply> ListMounts(ListMountsRequest request, ServerCallContext context)
	{
		logger.LogDebug("ListMounts requested");

		var allMounts = await stateStore.GetAllAsync(context.CancellationToken);
		var reply = new ListMountsReply();

		foreach (var mount in allMounts)
		{
			var info = new MountInfo
			{
				ChildVhdxPath = mount.ChildVhdxPath,
				ParentVhdxPath = mount.ParentVhdxPath,
				MountPath = mount.MountPath,
				VolumeGuidPath = mount.VolumeGuidPath,
			};

			try
			{
				var vhdxInfo = await vhdxManager.GetInfoAsync(mount.ChildVhdxPath, context.CancellationToken);
				info.IsAttached = vhdxInfo.IsAttached;
				info.ChildSizeBytes = vhdxInfo.PhysicalSize;
			}
			catch (Exception ex)
			{
				logger.LogWarning(ex, "Failed to query info for {ChildPath}, marking as not attached", mount.ChildVhdxPath);
				info.IsAttached = false;
			}

			reply.Mounts.Add(info);
		}

		logger.LogDebug("ListMounts returning {Count} mounts", reply.Mounts.Count);
		return reply;
	}

	public override async Task<CreateVhdxReply> CreateVhdx(CreateVhdxRequest request, ServerCallContext context)
	{
		logger.LogInformation(
			"CreateVhdx: Path={Path}, Size={Size}, Dynamic={Dynamic}, Mount={Mount}",
				request.VhdxPath, request.SizeBytes, request.Dynamic, request.MountPath);

		if (!pathValidator.ValidateChildPath(request.VhdxPath, out var pathError))
			return new CreateVhdxReply { Success = false, ErrorMessage = pathError };

		var hasMount = !string.IsNullOrEmpty(request.MountPath);
		if (hasMount && !pathValidator.ValidateMountPath(request.MountPath, out var mountError))
			return new CreateVhdxReply { Success = false, ErrorMessage = mountError };

		if (File.Exists(request.VhdxPath))
			return new CreateVhdxReply { Success = false, ErrorMessage = $"VHDX already exists: {request.VhdxPath}" };

		try
		{
			var sw = Stopwatch.StartNew();

			await vhdxManager.CreateStandaloneVhdxAsync(
				request.VhdxPath, request.SizeBytes, request.Dynamic, context.CancellationToken);

			var physicalPath = await vhdxManager.AttachAsync(
				request.VhdxPath, context.CancellationToken);

			var label = string.IsNullOrEmpty(request.NtfsLabel) ? "data" : request.NtfsLabel;
			await diskInitializer.InitializeAndFormatAsync(
				physicalPath, label, context.CancellationToken);

			var volumeGuidPath = string.Empty;
			if (hasMount)
			{
				volumeGuidPath = await volumeManager.GetVolumeGuidPathAsync(
					physicalPath, context.CancellationToken);
				await volumeManager.MountToFolderAsync(
					volumeGuidPath, request.MountPath, context.CancellationToken);

				await stateStore.AddAsync(new MountedDiskState
				{
					ChildVhdxPath = request.VhdxPath,
					ParentVhdxPath = string.Empty, // standalone — no parent
					MountPath = request.MountPath,
					VolumeGuidPath = volumeGuidPath,
				}, context.CancellationToken);
			}
			else
			{
				// Not mounted — detach so we leave the system clean. The user
				// can mount later via AttachAndMount.
				await vhdxManager.DetachAsync(request.VhdxPath, context.CancellationToken);
			}

			sw.Stop();
			logger.LogInformation(
				"CreateVhdx completed in {Duration}ms: {Path}",
					sw.ElapsedMilliseconds, request.VhdxPath);

			return new CreateVhdxReply
			{
				Success = true,
				VolumeGuidPath = volumeGuidPath,
			};
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "CreateVhdx failed for {Path}", request.VhdxPath);
			return new CreateVhdxReply { Success = false, ErrorMessage = ex.Message };
		}
	}

	public override async Task<AttachAndMountReply> AttachAndMount(AttachAndMountRequest request, ServerCallContext context)
	{
		logger.LogInformation("AttachAndMount: {Path} -> {Mount}", request.VhdxPath, request.MountPath);

		if (!pathValidator.ValidateChildPath(request.VhdxPath, out var pathError))
			return new AttachAndMountReply { Success = false, ErrorMessage = pathError };
		if (!pathValidator.ValidateMountPath(request.MountPath, out var mountError))
			return new AttachAndMountReply { Success = false, ErrorMessage = mountError };
		if (!File.Exists(request.VhdxPath))
			return new AttachAndMountReply { Success = false, ErrorMessage = $"VHDX not found: {request.VhdxPath}" };

		try
		{
			var physicalPath = await vhdxManager.AttachAsync(request.VhdxPath, context.CancellationToken);
			var volumeGuid = await volumeManager.GetVolumeGuidPathAsync(physicalPath, context.CancellationToken);
			await volumeManager.MountToFolderAsync(volumeGuid, request.MountPath, context.CancellationToken);

			await stateStore.AddAsync(new MountedDiskState
			{
				ChildVhdxPath = request.VhdxPath,
				ParentVhdxPath = string.Empty,
				MountPath = request.MountPath,
				VolumeGuidPath = volumeGuid,
			}, context.CancellationToken);

			return new AttachAndMountReply { Success = true, VolumeGuidPath = volumeGuid };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "AttachAndMount failed for {Path}", request.VhdxPath);
			return new AttachAndMountReply { Success = false, ErrorMessage = ex.Message };
		}
	}

	public override async Task<UnmountAndDetachReply> UnmountAndDetach(UnmountAndDetachRequest request, ServerCallContext context)
	{
		logger.LogInformation("UnmountAndDetach: {Path}", request.VhdxPath);

		var state = await stateStore.GetAsync(request.VhdxPath, context.CancellationToken);
		try
		{
			if (state is not null)
			{
				await volumeManager.UnmountFolderAsync(state.MountPath, context.CancellationToken);
			}
			await vhdxManager.DetachAsync(request.VhdxPath, context.CancellationToken);
			await stateStore.RemoveAsync(request.VhdxPath, context.CancellationToken);

			return new UnmountAndDetachReply { Success = true };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "UnmountAndDetach failed for {Path}", request.VhdxPath);
			return new UnmountAndDetachReply { Success = false, ErrorMessage = ex.Message };
		}
	}

	public override async Task<ConvertFolderReply> ConvertFolder(ConvertFolderRequest request, ServerCallContext context)
	{
		logger.LogInformation(
			"ConvertFolder: Folder={Folder}, Vhdx={Vhdx}, Size={Size}",
				request.FolderPath, request.VhdxPath, request.SizeBytes);

		if (!pathValidator.ValidateConvertSourcePath(request.FolderPath, out var folderError))
			return new ConvertFolderReply { Success = false, ErrorMessage = folderError };
		if (!pathValidator.ValidateChildPath(request.VhdxPath, out var vhdxError))
			return new ConvertFolderReply { Success = false, ErrorMessage = vhdxError };
		if (!pathValidator.ValidateMountPath(request.FolderPath, out var mountError))
			return new ConvertFolderReply { Success = false, ErrorMessage = mountError };

		var label = string.IsNullOrEmpty(request.NtfsLabel) ? "data" : request.NtfsLabel;

		var result = await folderTransferOrchestrator.ConvertFolderAsync(
			request.FolderPath,
			request.VhdxPath,
			request.SizeBytes,
			request.Dynamic,
			label,
			request.DeleteStaging,
			context.CancellationToken);

		if (result.Success)
		{
			await stateStore.AddAsync(new MountedDiskState
			{
				ChildVhdxPath = request.VhdxPath,
				ParentVhdxPath = string.Empty,
				MountPath = request.FolderPath,
				VolumeGuidPath = result.VolumeGuidPath,
			}, context.CancellationToken);
		}

		return new ConvertFolderReply
		{
			Success = result.Success,
			ErrorMessage = result.ErrorMessage ?? string.Empty,
			StagingFolderPath = result.StagingFolderPath,
			FilesCopied = result.FilesCopied,
			BytesCopied = result.BytesCopied,
		};
	}
}

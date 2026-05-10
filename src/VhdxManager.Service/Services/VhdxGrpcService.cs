using System.Diagnostics;
using Grpc.Core;
using VhdxManager.Contracts;
using VhdxManager.Service.Configuration;
using VhdxManager.Service.Security;
using VhdxManager.Service.State;
using VhdxManager.Service.VhdxOperations;

namespace VhdxManager.Service.Services;

public sealed class VhdxGrpcService(
	IVirtDiskManager virtDiskManager,
	IVolumeManager volumeManager,
	IDiskInitializer diskInitializer,
	IFolderTransferOrchestrator folderTransferOrchestrator,
	IStateStore stateStore,
	PathValidator pathValidator,
	IDefenderExclusionManager defenderExclusionManager,
	IServiceSettingsStore settingsStore,
	ILogger<VhdxGrpcService> logger) : VhdxService.VhdxServiceBase
{
	// ─── Read-only RPCs (no streaming) ─────────────────────────────────────

	public override Task<PingReply> Ping(PingRequest request, ServerCallContext context)
	{
		logger.LogInformation("Ping received");
		return Task.FromResult(new PingReply
		{
			Version = typeof(VhdxGrpcService).Assembly.GetName().Version?.ToString() ?? "0.0.0",
			ActiveMounts = (uint)stateStore.GetActiveMountCount(),
		});
	}

	public override async Task<GetStatusReply> GetStatus(GetStatusRequest request, ServerCallContext context)
	{
		logger.LogDebug("GetStatus: {ChildPath}", request.ChildVhdxPath);

		if (await stateStore.GetAsync(request.ChildVhdxPath, context.CancellationToken) is not { } state)
		{
			return new GetStatusReply { IsAttached = false };
		}

		var info = await virtDiskManager.GetInfoAsync(request.ChildVhdxPath, context.CancellationToken);
		return new GetStatusReply
		{
			IsAttached = info.IsAttached,
			MountPath = state.MountPath,
			ParentVhdxPath = state.ParentVhdxPath,
			VolumeGuidPath = state.VolumeGuidPath,
			ChildSizeBytes = info.PhysicalSize,
		};
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
				var vhdxInfo = await virtDiskManager.GetInfoAsync(mount.ChildVhdxPath, context.CancellationToken);
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

	public override Task<GetSettingsReply> GetSettings(GetSettingsRequest request, ServerCallContext context)
	{
		var current = settingsStore.GetDefaultAddDefenderExclusion();
		return Task.FromResult(new GetSettingsReply
		{
			HasDefaultAddDefenderExclusion = current.HasValue,
			DefaultAddDefenderExclusion = current ?? false,
		});
	}

	public override async Task<SetSettingsReply> SetSettings(SetSettingsRequest request, ServerCallContext context)
	{
		try
		{
			// Tri-state collapse:
			//   clear=true        → null (unset)
			//   has=true, val=X   → X
			//   has=false         → null (also clears)
			bool? newValue = request.ClearDefaultAddDefenderExclusion
				? null
				: request.HasDefaultAddDefenderExclusion
					? request.DefaultAddDefenderExclusion
					: null;

			await settingsStore.SetDefaultAddDefenderExclusionAsync(newValue, context.CancellationToken);
			return new SetSettingsReply { Success = true };
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "SetSettings failed");
			return new SetSettingsReply { Success = false, ErrorMessage = ex.Message };
		}
	}

	// ─── Streaming RPCs ────────────────────────────────────────────────────

	public override async Task CreateChild(
		CreateChildRequest request,
		IServerStreamWriter<CreateChildStream> responseStream,
		ServerCallContext context)
	{
		var ct = context.CancellationToken;
		var reporter = new ProgressReporter<CreateChildStream>(
			responseStream, p => new CreateChildStream { Progress = p });

		logger.LogInformation(
			"CreateChild: Parent={ParentPath}, Child={ChildPath}, Mount={MountPath}",
				request.ParentVhdxPath, request.ChildVhdxPath, request.MountPath);

		if (!pathValidator.ValidateParentPath(request.ParentVhdxPath, out var parentError))
		{ await EmitFinal(responseStream, new CreateChildReply { Success = false, ErrorMessage = parentError }, ct); return; }
		if (!pathValidator.ValidateChildPath(request.ChildVhdxPath, out var childError))
		{ await EmitFinal(responseStream, new CreateChildReply { Success = false, ErrorMessage = childError }, ct); return; }
		if (!pathValidator.ValidateMountPath(request.MountPath, out var mountError))
		{ await EmitFinal(responseStream, new CreateChildReply { Success = false, ErrorMessage = mountError }, ct); return; }
		if (!File.Exists(request.ParentVhdxPath))
		{ await EmitFinal(responseStream, new CreateChildReply { Success = false, ErrorMessage = $"Parent VHDX not found: {request.ParentVhdxPath}" }, ct); return; }

		try
		{
			var sw = Stopwatch.StartNew();

			await reporter.StepAsync("Creating differencing VHDX",
				() => virtDiskManager.CreateDifferencingDiskAsync(request.ParentVhdxPath, request.ChildVhdxPath, ct),
				request.ChildVhdxPath, ct);

			var physicalPath = await reporter.StepAsync("Attaching",
				() => virtDiskManager.AttachAsync(request.ChildVhdxPath, ct),
				request.ChildVhdxPath, ct);

			var volumeGuidPath = await reporter.StepAsync("Resolving volume",
				() => volumeManager.GetVolumeGuidPathAsync(physicalPath, ct),
				physicalPath, ct);

			await reporter.StepAsync("Mounting",
				() => volumeManager.MountToFolderAsync(volumeGuidPath, request.MountPath, ct),
				request.MountPath, ct);

			await stateStore.AddAsync(new MountedDiskState
			{
				ChildVhdxPath = request.ChildVhdxPath,
				ParentVhdxPath = request.ParentVhdxPath,
				MountPath = request.MountPath,
				VolumeGuidPath = volumeGuidPath,
			}, ct);

			sw.Stop();
			logger.LogInformation("CreateChild completed in {Duration}ms", sw.ElapsedMilliseconds);

			var defenderWarning = string.Empty;
			if (request.AddDefenderExclusion)
			{
				defenderWarning = await TryAddDefenderExclusionAsync(reporter, request.ChildVhdxPath, ct);
			}

			await EmitFinal(responseStream, new CreateChildReply
			{
				Success = true,
				VolumeGuidPath = volumeGuidPath,
				DefenderWarning = defenderWarning,
			}, ct);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "CreateChild failed for {ChildPath}", request.ChildVhdxPath);
			await EmitFinal(responseStream, new CreateChildReply { Success = false, ErrorMessage = ex.Message }, ct);
		}
	}

	public override async Task ResetChild(
		ResetChildRequest request,
		IServerStreamWriter<ResetChildStream> responseStream,
		ServerCallContext context)
	{
		var ct = context.CancellationToken;
		var reporter = new ProgressReporter<ResetChildStream>(
			responseStream, p => new ResetChildStream { Progress = p });

		logger.LogInformation("ResetChild: {ChildPath}", request.ChildVhdxPath);

		if (await stateStore.GetAsync(request.ChildVhdxPath, ct) is not { } state)
		{
			await EmitFinal(responseStream, new ResetChildReply { Success = false, ErrorMessage = $"No tracked mount for '{request.ChildVhdxPath}'" }, ct);
			return;
		}

		try
		{
			var sw = Stopwatch.StartNew();

			await reporter.StepAsync("Unmounting",
				() => volumeManager.UnmountFolderAsync(state.MountPath, ct),
				state.MountPath, ct);

			await reporter.StepAsync("Detaching",
				() => virtDiskManager.DetachAsync(request.ChildVhdxPath, ct),
				request.ChildVhdxPath, ct);

			await reporter.StepAsync("Deleting old child VHDX",
				() => Task.Run(() => File.Delete(request.ChildVhdxPath), ct),
				request.ChildVhdxPath, ct);

			await reporter.StepAsync("Recreating differencing VHDX",
				() => virtDiskManager.CreateDifferencingDiskAsync(state.ParentVhdxPath, request.ChildVhdxPath, ct),
				request.ChildVhdxPath, ct);

			var physicalPath = await reporter.StepAsync("Attaching",
				() => virtDiskManager.AttachAsync(request.ChildVhdxPath, ct),
				request.ChildVhdxPath, ct);

			var volumeGuidPath = await reporter.StepAsync("Resolving volume",
				() => volumeManager.GetVolumeGuidPathAsync(physicalPath, ct),
				physicalPath, ct);

			await reporter.StepAsync("Mounting",
				() => volumeManager.MountToFolderAsync(volumeGuidPath, state.MountPath, ct),
				state.MountPath, ct);

			await stateStore.AddAsync(new MountedDiskState
			{
				ChildVhdxPath = request.ChildVhdxPath,
				ParentVhdxPath = state.ParentVhdxPath,
				MountPath = state.MountPath,
				VolumeGuidPath = volumeGuidPath,
			}, ct);

			sw.Stop();
			logger.LogInformation("ResetChild completed in {Duration}ms", sw.ElapsedMilliseconds);

			await EmitFinal(responseStream, new ResetChildReply { Success = true }, ct);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "ResetChild failed for {ChildPath}", request.ChildVhdxPath);
			await EmitFinal(responseStream, new ResetChildReply { Success = false, ErrorMessage = ex.Message }, ct);
		}
	}

	public override async Task Detach(
		DetachRequest request,
		IServerStreamWriter<DetachStream> responseStream,
		ServerCallContext context)
	{
		var ct = context.CancellationToken;
		var reporter = new ProgressReporter<DetachStream>(
			responseStream, p => new DetachStream { Progress = p });

		logger.LogInformation("Detach: {ChildPath}", request.ChildVhdxPath);

		var state = await stateStore.GetAsync(request.ChildVhdxPath, ct);
		try
		{
			if (state is not null)
			{
				await reporter.StepAsync("Unmounting",
					() => volumeManager.UnmountFolderAsync(state.MountPath, ct),
					state.MountPath, ct);
			}

			await reporter.StepAsync("Detaching",
				() => virtDiskManager.DetachAsync(request.ChildVhdxPath, ct),
				request.ChildVhdxPath, ct);

			if (File.Exists(request.ChildVhdxPath))
			{
				await reporter.StepAsync("Deleting VHDX file",
					() => Task.Run(() => File.Delete(request.ChildVhdxPath), ct),
					request.ChildVhdxPath, ct);
			}

			await stateStore.RemoveAsync(request.ChildVhdxPath, ct);

			logger.LogInformation("Detach completed: {ChildPath}", request.ChildVhdxPath);
			await EmitFinal(responseStream, new DetachReply { Success = true }, ct);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Detach failed for {ChildPath}", request.ChildVhdxPath);
			await EmitFinal(responseStream, new DetachReply { Success = false, ErrorMessage = ex.Message }, ct);
		}
	}

	public override async Task Publish(
		PublishRequest request,
		IServerStreamWriter<PublishStream> responseStream,
		ServerCallContext context)
	{
		var ct = context.CancellationToken;
		var reporter = new ProgressReporter<PublishStream>(
			responseStream, p => new PublishStream { Progress = p });

		logger.LogInformation("Publish: merging overlay {OverlayPath}", request.OverlayVhdxPath);

		try
		{
			var sw = Stopwatch.StartNew();

			var allMounts = await stateStore.GetAllAsync(ct);

			await reporter.StepAsync($"Detaching {allMounts.Count} child mount(s)",
				async () =>
				{
					foreach (var mount in allMounts)
					{
						await volumeManager.UnmountFolderAsync(mount.MountPath, ct);
						await virtDiskManager.DetachAsync(mount.ChildVhdxPath, ct);
						File.Delete(mount.ChildVhdxPath);
					}
				}, ct: ct);

			await reporter.StepAsync("Detaching overlay",
				() => virtDiskManager.DetachAsync(request.OverlayVhdxPath, ct),
				request.OverlayVhdxPath, ct);

			await reporter.StepAsync("Merging overlay into parent",
				() => virtDiskManager.MergeAsync(request.OverlayVhdxPath, ct),
				request.OverlayVhdxPath, ct);

			uint recreated = 0;
			await reporter.StepAsync($"Recreating {allMounts.Count} child mount(s)",
				async () =>
				{
					foreach (var mount in allMounts)
					{
						await virtDiskManager.CreateDifferencingDiskAsync(mount.ParentVhdxPath, mount.ChildVhdxPath, ct);
						var physicalPath = await virtDiskManager.AttachAsync(mount.ChildVhdxPath, ct);
						var volumeGuidPath = await volumeManager.GetVolumeGuidPathAsync(physicalPath, ct);
						await volumeManager.MountToFolderAsync(volumeGuidPath, mount.MountPath, ct);

						await stateStore.AddAsync(new MountedDiskState
						{
							ChildVhdxPath = mount.ChildVhdxPath,
							ParentVhdxPath = mount.ParentVhdxPath,
							MountPath = mount.MountPath,
							VolumeGuidPath = volumeGuidPath,
						}, ct);
						recreated++;
					}
				}, ct: ct);

			sw.Stop();
			logger.LogInformation("Publish completed in {Duration}ms: {N} children recreated", sw.ElapsedMilliseconds, recreated);

			await EmitFinal(responseStream, new PublishReply { Success = true, ChildrenRecreated = recreated }, ct);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Publish failed for overlay {OverlayPath}", request.OverlayVhdxPath);
			await EmitFinal(responseStream, new PublishReply { Success = false, ErrorMessage = ex.Message }, ct);
		}
	}

	public override async Task CreateVhdx(
		CreateVhdxRequest request,
		IServerStreamWriter<CreateVhdxStream> responseStream,
		ServerCallContext context)
	{
		var ct = context.CancellationToken;
		var reporter = new ProgressReporter<CreateVhdxStream>(
			responseStream, p => new CreateVhdxStream { Progress = p });

		logger.LogInformation(
			"CreateVhdx: Path={Path}, Size={Size}, Dynamic={Dynamic}, Mount={Mount}",
				request.VhdxPath, request.SizeBytes, request.Dynamic, request.MountPath);

		if (!pathValidator.ValidateChildPath(request.VhdxPath, out var pathError))
		{ await EmitFinal(responseStream, new CreateVhdxReply { Success = false, ErrorMessage = pathError }, ct); return; }

		var hasMount = !string.IsNullOrEmpty(request.MountPath);
		if (hasMount && !pathValidator.ValidateMountPath(request.MountPath, out var mountError))
		{ await EmitFinal(responseStream, new CreateVhdxReply { Success = false, ErrorMessage = mountError }, ct); return; }

		if (File.Exists(request.VhdxPath))
		{ await EmitFinal(responseStream, new CreateVhdxReply { Success = false, ErrorMessage = $"VHDX already exists: {request.VhdxPath}" }, ct); return; }

		// Track progress for rollback. Each flag flips after the corresponding step
		// succeeds, so the catch handler knows exactly what to undo.
		var fileCreated = false;
		var attached = false;
		var mounted = false;

		try
		{
			var sw = Stopwatch.StartNew();

			await reporter.StepAsync("Creating VHDX file",
				() => virtDiskManager.CreateStandaloneVhdxAsync(request.VhdxPath, request.SizeBytes, request.Dynamic, ct),
				request.VhdxPath, ct);
			fileCreated = true;

			var physicalPath = await reporter.StepAsync("Attaching",
				() => virtDiskManager.AttachAsync(request.VhdxPath, ct),
				request.VhdxPath, ct);
			attached = true;

			var label = string.IsNullOrEmpty(request.VolumeLabel) ? "data" : request.VolumeLabel;
			// Empty filesystem string in the request means "use the service default";
			// IDiskInitializer normalizes this to "ReFS".
			var filesystem = request.Filesystem;
			await reporter.StepAsync($"Initializing partition + formatting {(string.IsNullOrEmpty(filesystem) ? "ReFS" : filesystem)}",
				() => diskInitializer.InitializeAndFormatAsync(physicalPath, label, filesystem, ct),
				$"label={label}", ct);

			var volumeGuidPath = string.Empty;
			if (hasMount)
			{
				volumeGuidPath = await reporter.StepAsync("Resolving volume",
					() => volumeManager.GetVolumeGuidPathAsync(physicalPath, ct),
					physicalPath, ct);

				await reporter.StepAsync("Mounting",
					() => volumeManager.MountToFolderAsync(volumeGuidPath, request.MountPath, ct),
					request.MountPath, ct);
				mounted = true;

				await stateStore.AddAsync(new MountedDiskState
				{
					ChildVhdxPath = request.VhdxPath,
					ParentVhdxPath = string.Empty,
					MountPath = request.MountPath,
					VolumeGuidPath = volumeGuidPath,
				}, ct);
			}
			else
			{
				// Not mounted — detach so we leave the system clean. The user
				// can mount later via AttachAndMount.
				await reporter.StepAsync("Detaching (no mount requested)",
					() => virtDiskManager.DetachAsync(request.VhdxPath, ct),
					request.VhdxPath, ct);
				attached = false;
			}

			sw.Stop();
			logger.LogInformation("CreateVhdx completed in {Duration}ms: {Path}", sw.ElapsedMilliseconds, request.VhdxPath);

			var defenderWarning = string.Empty;
			if (request.AddDefenderExclusion)
			{
				defenderWarning = await TryAddDefenderExclusionAsync(reporter, request.VhdxPath, ct);
			}

			await EmitFinal(responseStream,
				new CreateVhdxReply
				{
					Success = true,
					VolumeGuidPath = volumeGuidPath,
					DefenderWarning = defenderWarning,
				}, ct);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "CreateVhdx failed for {Path} — rolling back", request.VhdxPath);

			// Best-effort rollback. The reporter has already emitted FAILED for the
			// step that threw; rollback steps are emitted as their own progress so
			// the user sees what we cleaned up.
			if (mounted)
			{
				try { await reporter.StepAsync("Rollback: unmounting",
					() => volumeManager.UnmountFolderAsync(request.MountPath, CancellationToken.None),
					ct: CancellationToken.None); }
				catch (Exception undo) { logger.LogWarning(undo, "Rollback unmount failed"); }
			}
			if (attached)
			{
				try { await reporter.StepAsync("Rollback: detaching",
					() => virtDiskManager.DetachAsync(request.VhdxPath, CancellationToken.None),
					ct: CancellationToken.None); }
				catch (Exception undo) { logger.LogWarning(undo, "Rollback detach failed"); }
			}
			if (fileCreated)
			{
				try { await reporter.StepAsync("Rollback: deleting VHDX file",
					() => Task.Run(() => { if (File.Exists(request.VhdxPath)) File.Delete(request.VhdxPath); }),
					ct: CancellationToken.None); }
				catch (Exception undo) { logger.LogWarning(undo, "Rollback delete failed"); }
			}

			await EmitFinal(responseStream, new CreateVhdxReply { Success = false, ErrorMessage = ex.Message }, CancellationToken.None);
		}
	}

	public override async Task AttachAndMount(
		AttachAndMountRequest request,
		IServerStreamWriter<AttachAndMountStream> responseStream,
		ServerCallContext context)
	{
		var ct = context.CancellationToken;
		var reporter = new ProgressReporter<AttachAndMountStream>(
			responseStream, p => new AttachAndMountStream { Progress = p });

		logger.LogInformation("AttachAndMount: {Path} -> {Mount}", request.VhdxPath, request.MountPath);

		if (!pathValidator.ValidateChildPath(request.VhdxPath, out var pathError))
		{ await EmitFinal(responseStream, new AttachAndMountReply { Success = false, ErrorMessage = pathError }, ct); return; }
		if (!pathValidator.ValidateMountPath(request.MountPath, out var mountError))
		{ await EmitFinal(responseStream, new AttachAndMountReply { Success = false, ErrorMessage = mountError }, ct); return; }
		if (!File.Exists(request.VhdxPath))
		{ await EmitFinal(responseStream, new AttachAndMountReply { Success = false, ErrorMessage = $"VHDX not found: {request.VhdxPath}" }, ct); return; }

		try
		{
			var physicalPath = await reporter.StepAsync("Attaching",
				() => virtDiskManager.AttachAsync(request.VhdxPath, ct),
				request.VhdxPath, ct);

			var volumeGuid = await reporter.StepAsync("Resolving volume",
				() => volumeManager.GetVolumeGuidPathAsync(physicalPath, ct),
				physicalPath, ct);

			await reporter.StepAsync("Mounting",
				() => volumeManager.MountToFolderAsync(volumeGuid, request.MountPath, ct),
				request.MountPath, ct);

			await stateStore.AddAsync(new MountedDiskState
			{
				ChildVhdxPath = request.VhdxPath,
				ParentVhdxPath = string.Empty,
				MountPath = request.MountPath,
				VolumeGuidPath = volumeGuid,
			}, ct);

			await EmitFinal(responseStream, new AttachAndMountReply { Success = true, VolumeGuidPath = volumeGuid }, ct);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "AttachAndMount failed for {Path}", request.VhdxPath);
			await EmitFinal(responseStream, new AttachAndMountReply { Success = false, ErrorMessage = ex.Message }, ct);
		}
	}

	public override async Task UnmountAndDetach(
		UnmountAndDetachRequest request,
		IServerStreamWriter<UnmountAndDetachStream> responseStream,
		ServerCallContext context)
	{
		var ct = context.CancellationToken;
		var reporter = new ProgressReporter<UnmountAndDetachStream>(
			responseStream, p => new UnmountAndDetachStream { Progress = p });

		logger.LogInformation("UnmountAndDetach: {Path}", request.VhdxPath);

		var state = await stateStore.GetAsync(request.VhdxPath, ct);
		try
		{
			if (state is not null)
			{
				await reporter.StepAsync("Unmounting",
					() => volumeManager.UnmountFolderAsync(state.MountPath, ct),
					state.MountPath, ct);
			}

			await reporter.StepAsync("Detaching",
				() => virtDiskManager.DetachAsync(request.VhdxPath, ct),
				request.VhdxPath, ct);

			await stateStore.RemoveAsync(request.VhdxPath, ct);
			await EmitFinal(responseStream, new UnmountAndDetachReply { Success = true }, ct);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "UnmountAndDetach failed for {Path}", request.VhdxPath);
			await EmitFinal(responseStream, new UnmountAndDetachReply { Success = false, ErrorMessage = ex.Message }, ct);
		}
	}

	public override async Task ConvertFolder(
		ConvertFolderRequest request,
		IServerStreamWriter<ConvertFolderStream> responseStream,
		ServerCallContext context)
	{
		var ct = context.CancellationToken;
		var reporter = new ProgressReporter<ConvertFolderStream>(
			responseStream, p => new ConvertFolderStream { Progress = p });

		logger.LogInformation("ConvertFolder: Folder={Folder}, Vhdx={Vhdx}, Size={Size}",
			request.FolderPath, request.VhdxPath, request.SizeBytes);

		if (!pathValidator.ValidateConvertSourcePath(request.FolderPath, out var folderError))
		{ await EmitFinal(responseStream, new ConvertFolderReply { Success = false, ErrorMessage = folderError }, ct); return; }
		if (!pathValidator.ValidateChildPath(request.VhdxPath, out var vhdxError))
		{ await EmitFinal(responseStream, new ConvertFolderReply { Success = false, ErrorMessage = vhdxError }, ct); return; }
		if (!pathValidator.ValidateMountPath(request.FolderPath, out var mountError))
		{ await EmitFinal(responseStream, new ConvertFolderReply { Success = false, ErrorMessage = mountError }, ct); return; }

		var label = string.IsNullOrEmpty(request.VolumeLabel) ? "data" : request.VolumeLabel;
		var filesystem = request.Filesystem;  // empty → default to ReFS in initializer

		// Convert is one composite step from the protocol's point of view; the
		// orchestrator emits its own structured logs for the substeps. If finer-
		// grained progress is needed later, IFolderTransferOrchestrator can take
		// an IStepReporter and call back into ProgressReporter.
		await reporter.StartedAsync("Converting folder", request.FolderPath, ct);

		var result = await folderTransferOrchestrator.ConvertFolderAsync(
			request.FolderPath, request.VhdxPath, request.SizeBytes,
			request.Dynamic, label, filesystem, request.DeleteStaging, ct);

		var defenderWarning = string.Empty;

		if (result.Success)
		{
			await reporter.CompletedAsync("Converting folder",
				$"{result.FilesCopied} files, {result.BytesCopied} bytes", ct);

			await stateStore.AddAsync(new MountedDiskState
			{
				ChildVhdxPath = request.VhdxPath,
				ParentVhdxPath = string.Empty,
				MountPath = request.FolderPath,
				VolumeGuidPath = result.VolumeGuidPath,
			}, ct);

			if (request.AddDefenderExclusion)
			{
				defenderWarning = await TryAddDefenderExclusionAsync(reporter, request.VhdxPath, ct);
			}
		}
		else
		{
			await reporter.FailedAsync("Converting folder", result.ErrorMessage ?? "(unknown)", ct);
		}

		await EmitFinal(responseStream, new ConvertFolderReply
		{
			Success = result.Success,
			ErrorMessage = result.ErrorMessage ?? string.Empty,
			StagingFolderPath = result.StagingFolderPath,
			FilesCopied = result.FilesCopied,
			BytesCopied = result.BytesCopied,
			DefenderWarning = defenderWarning,
		}, ct);
	}

	// ─── Helpers ───────────────────────────────────────────────────────────

	/// <summary>
	/// Best-effort Defender exclusion step. Emits its own STARTED/COMPLETED/FAILED
	/// progress events but never propagates the exception — Defender failures must
	/// not fail the parent pipeline (the VHDX itself is already on disk).
	/// Returns the warning text to embed in the final reply (empty on success).
	/// </summary>
	async Task<string> TryAddDefenderExclusionAsync<TStream>(
		ProgressReporter<TStream> reporter,
		string vhdxPath,
		CancellationToken ct)
	{
		const string Step = "Adding Defender exclusion";
		await reporter.StartedAsync(Step, vhdxPath, ct);
		try
		{
			await defenderExclusionManager.AddExclusionAsync(vhdxPath, ct);
			await reporter.CompletedAsync(Step, ct: ct);
			return string.Empty;
		}
		catch (DefenderPolicyBlockedException ex)
		{
			logger.LogWarning("Defender exclusion blocked by policy for {Path}: {Message}", vhdxPath, ex.Message);
			await reporter.FailedAsync(Step, ex.Message, ct);
			return ex.Message;
		}
		catch (Exception ex)
		{
			logger.LogWarning(ex, "Defender exclusion failed for {Path}", vhdxPath);
			await reporter.FailedAsync(Step, ex.Message, ct);
			return ex.Message;
		}
	}

	static Task EmitFinal(IServerStreamWriter<CreateChildStream> w, CreateChildReply r, CancellationToken ct)
		=> w.WriteAsync(new CreateChildStream { Final = r }, ct);

	static Task EmitFinal(IServerStreamWriter<ResetChildStream> w, ResetChildReply r, CancellationToken ct)
		=> w.WriteAsync(new ResetChildStream { Final = r }, ct);

	static Task EmitFinal(IServerStreamWriter<DetachStream> w, DetachReply r, CancellationToken ct)
		=> w.WriteAsync(new DetachStream { Final = r }, ct);

	static Task EmitFinal(IServerStreamWriter<PublishStream> w, PublishReply r, CancellationToken ct)
		=> w.WriteAsync(new PublishStream { Final = r }, ct);

	static Task EmitFinal(IServerStreamWriter<CreateVhdxStream> w, CreateVhdxReply r, CancellationToken ct)
		=> w.WriteAsync(new CreateVhdxStream { Final = r }, ct);

	static Task EmitFinal(IServerStreamWriter<AttachAndMountStream> w, AttachAndMountReply r, CancellationToken ct)
		=> w.WriteAsync(new AttachAndMountStream { Final = r }, ct);

	static Task EmitFinal(IServerStreamWriter<UnmountAndDetachStream> w, UnmountAndDetachReply r, CancellationToken ct)
		=> w.WriteAsync(new UnmountAndDetachStream { Final = r }, ct);

	static Task EmitFinal(IServerStreamWriter<ConvertFolderStream> w, ConvertFolderReply r, CancellationToken ct)
		=> w.WriteAsync(new ConvertFolderStream { Final = r }, ct);
}

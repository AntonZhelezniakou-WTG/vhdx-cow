using Grpc.Core;
using Grpc.Net.Client;
using VhdxManager.Contracts;

namespace VhdxManager.Client;

/// <summary>
/// High-level client for communicating with the VhdxManager Windows Service.
/// Connects via named pipe using gRPC.
/// </summary>
public sealed class VhdxManagerClient : IVhdxManagerClient
{
	readonly GrpcChannel channel;
	readonly VhdxService.VhdxServiceClient client;
	readonly TimeSpan? timeout;

	public VhdxManagerClient(string pipeName = ServiceConstants.PipeName, TimeSpan? timeout = null)
	{
		channel = NamedPipeChannelFactory.Create(pipeName);
		client = new VhdxService.VhdxServiceClient(channel);
		this.timeout = timeout;
	}

	public void Dispose() => channel.Dispose();

	// ─── Read-only / unary ─────────────────────────────────────────────────

	public Task<PingReply> PingAsync(CancellationToken ct = default)
		=> SafeUnaryAsync(t => client.PingAsync(new PingRequest(), cancellationToken: t).ResponseAsync, ct);

	public Task<GetStatusReply> GetStatusAsync(string childVhdxPath, CancellationToken ct = default)
		=> SafeUnaryAsync(t => client.GetStatusAsync(
			new GetStatusRequest { ChildVhdxPath = childVhdxPath }, cancellationToken: t).ResponseAsync, ct);

	public Task<ListMountsReply> ListMountsAsync(CancellationToken ct = default)
		=> SafeUnaryAsync(t => client.ListMountsAsync(new ListMountsRequest(), cancellationToken: t).ResponseAsync, ct);

	// ─── Streaming mutating operations ─────────────────────────────────────

	public Task<CreateChildReply> CreateChildAsync(
		string parentVhdxPath, string childVhdxPath, string mountPath,
		Action<ProgressEvent>? onProgress = null, CancellationToken ct = default)
		=> ConsumeStreamAsync(
			t => client.CreateChild(new CreateChildRequest
			{
				ParentVhdxPath = parentVhdxPath,
				ChildVhdxPath = childVhdxPath,
				MountPath = mountPath,
			}, cancellationToken: t),
			s => s.EventCase == CreateChildStream.EventOneofCase.Progress ? s.Progress : null,
			s => s.EventCase == CreateChildStream.EventOneofCase.Final ? s.Final : null,
			onProgress, ct);

	public Task<ResetChildReply> ResetChildAsync(
		string childVhdxPath,
		Action<ProgressEvent>? onProgress = null, CancellationToken ct = default)
		=> ConsumeStreamAsync(
			t => client.ResetChild(new ResetChildRequest { ChildVhdxPath = childVhdxPath }, cancellationToken: t),
			s => s.EventCase == ResetChildStream.EventOneofCase.Progress ? s.Progress : null,
			s => s.EventCase == ResetChildStream.EventOneofCase.Final ? s.Final : null,
			onProgress, ct);

	public Task<DetachReply> DetachAsync(
		string childVhdxPath,
		Action<ProgressEvent>? onProgress = null, CancellationToken ct = default)
		=> ConsumeStreamAsync(
			t => client.Detach(new DetachRequest { ChildVhdxPath = childVhdxPath }, cancellationToken: t),
			s => s.EventCase == DetachStream.EventOneofCase.Progress ? s.Progress : null,
			s => s.EventCase == DetachStream.EventOneofCase.Final ? s.Final : null,
			onProgress, ct);

	public Task<PublishReply> PublishAsync(
		string overlayVhdxPath,
		Action<ProgressEvent>? onProgress = null, CancellationToken ct = default)
		=> ConsumeStreamAsync(
			t => client.Publish(new PublishRequest { OverlayVhdxPath = overlayVhdxPath }, cancellationToken: t),
			s => s.EventCase == PublishStream.EventOneofCase.Progress ? s.Progress : null,
			s => s.EventCase == PublishStream.EventOneofCase.Final ? s.Final : null,
			onProgress, ct);

	public Task<CreateVhdxReply> CreateVhdxAsync(
		string vhdxPath, long sizeBytes, bool dynamic, string volumeLabel, string mountPath, string filesystem,
		Action<ProgressEvent>? onProgress = null, CancellationToken ct = default)
		=> ConsumeStreamAsync(
			t => client.CreateVhdx(new CreateVhdxRequest
			{
				VhdxPath = vhdxPath,
				SizeBytes = sizeBytes,
				Dynamic = dynamic,
				VolumeLabel = volumeLabel,
				MountPath = mountPath,
				Filesystem = filesystem,
			}, cancellationToken: t),
			s => s.EventCase == CreateVhdxStream.EventOneofCase.Progress ? s.Progress : null,
			s => s.EventCase == CreateVhdxStream.EventOneofCase.Final ? s.Final : null,
			onProgress, ct);

	public Task<AttachAndMountReply> AttachAndMountAsync(
		string vhdxPath, string mountPath,
		Action<ProgressEvent>? onProgress = null, CancellationToken ct = default)
		=> ConsumeStreamAsync(
			t => client.AttachAndMount(new AttachAndMountRequest
			{
				VhdxPath = vhdxPath,
				MountPath = mountPath,
			}, cancellationToken: t),
			s => s.EventCase == AttachAndMountStream.EventOneofCase.Progress ? s.Progress : null,
			s => s.EventCase == AttachAndMountStream.EventOneofCase.Final ? s.Final : null,
			onProgress, ct);

	public Task<UnmountAndDetachReply> UnmountAndDetachAsync(
		string vhdxPath,
		Action<ProgressEvent>? onProgress = null, CancellationToken ct = default)
		=> ConsumeStreamAsync(
			t => client.UnmountAndDetach(new UnmountAndDetachRequest { VhdxPath = vhdxPath }, cancellationToken: t),
			s => s.EventCase == UnmountAndDetachStream.EventOneofCase.Progress ? s.Progress : null,
			s => s.EventCase == UnmountAndDetachStream.EventOneofCase.Final ? s.Final : null,
			onProgress, ct);

	public Task<ConvertFolderReply> ConvertFolderAsync(
		string folderPath, string vhdxPath, long sizeBytes, bool dynamic, string volumeLabel, string filesystem, bool deleteStaging,
		Action<ProgressEvent>? onProgress = null, CancellationToken ct = default)
		=> ConsumeStreamAsync(
			t => client.ConvertFolder(new ConvertFolderRequest
			{
				FolderPath = folderPath,
				VhdxPath = vhdxPath,
				SizeBytes = sizeBytes,
				Dynamic = dynamic,
				VolumeLabel = volumeLabel,
				Filesystem = filesystem,
				DeleteStaging = deleteStaging,
			}, cancellationToken: t),
			s => s.EventCase == ConvertFolderStream.EventOneofCase.Progress ? s.Progress : null,
			s => s.EventCase == ConvertFolderStream.EventOneofCase.Final ? s.Final : null,
			onProgress, ct);

	// ─── Helpers ───────────────────────────────────────────────────────────

	async Task<TReply> ConsumeStreamAsync<TStream, TReply>(
		Func<CancellationToken, AsyncServerStreamingCall<TStream>> startCall,
		Func<TStream, ProgressEvent?> getProgress,
		Func<TStream, TReply?> getFinal,
		Action<ProgressEvent>? onProgress,
		CancellationToken ct)
		where TReply : class
	{
		using var cts = LinkTimeout(ct);
		var effectiveToken = cts?.Token ?? ct;

		try
		{
			using var call = startCall(effectiveToken);
			TReply? finalReply = null;
			await foreach (var msg in call.ResponseStream.ReadAllAsync(effectiveToken))
			{
				var progress = getProgress(msg);
				if (progress is not null)
				{
					onProgress?.Invoke(progress);
					continue;
				}
				var final = getFinal(msg);
				if (final is not null)
				{
					finalReply = final;
				}
			}

			return finalReply
				?? throw new VhdxManagerServiceException(
					"Server stream ended without a final reply (protocol violation).");
		}
		catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
		{
			throw new VhdxManagerServiceException(
				$"VhdxManager service is not running. Start it with 'sc start {ServiceConstants.ServiceName}' or run Install-Service.ps1.",
				ex);
		}
		catch (OperationCanceledException) when (cts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
		{
			throw new TimeoutException($"Operation timed out after {timeout!.Value.TotalSeconds:F0} seconds.");
		}
	}

	async Task<T> SafeUnaryAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
	{
		using var cts = LinkTimeout(ct);
		var effectiveToken = cts?.Token ?? ct;
		try
		{
			return await call(effectiveToken);
		}
		catch (RpcException ex) when (ex.StatusCode == StatusCode.Unavailable)
		{
			throw new VhdxManagerServiceException(
				$"VhdxManager service is not running. Start it with 'sc start {ServiceConstants.ServiceName}' or run Install-Service.ps1.",
				ex);
		}
		catch (OperationCanceledException) when (cts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
		{
			throw new TimeoutException($"Operation timed out after {timeout!.Value.TotalSeconds:F0} seconds.");
		}
	}

	CancellationTokenSource? LinkTimeout(CancellationToken ct)
	{
		if (!timeout.HasValue) return null;
		var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
		cts.CancelAfter(timeout.Value);
		return cts;
	}
}

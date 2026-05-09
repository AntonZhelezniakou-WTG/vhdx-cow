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

	public Task<PingReply> PingAsync(
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.PingAsync(
			new PingRequest(),
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<CreateChildReply> CreateChildAsync(
		string parentVhdxPath,
		string childVhdxPath,
		string mountPath,
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.CreateChildAsync(
			new CreateChildRequest
			{
				ParentVhdxPath = parentVhdxPath,
				ChildVhdxPath = childVhdxPath,
				MountPath = mountPath,
			},
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<ResetChildReply> ResetChildAsync(
		string childVhdxPath,
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.ResetChildAsync(
			new ResetChildRequest {
				ChildVhdxPath = childVhdxPath,
			},
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<DetachReply> DetachAsync(
		string childVhdxPath,
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.DetachAsync(
			new DetachRequest
			{
				ChildVhdxPath = childVhdxPath,
			}, cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<GetStatusReply> GetStatusAsync(
		string childVhdxPath,
		CancellationToken ct = default) => SafeInvokeAsync(effectiveCancellationToken
		=> client.GetStatusAsync(
			new GetStatusRequest
			{
				ChildVhdxPath = childVhdxPath,
			}, cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<PublishReply> PublishAsync(
		string overlayVhdxPath,
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken => client.PublishAsync(new PublishRequest
		{
			OverlayVhdxPath = overlayVhdxPath,
		}, cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<ListMountsReply> ListMountsAsync(
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.ListMountsAsync(
			new ListMountsRequest(),
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<CreateVhdxReply> CreateVhdxAsync(
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string ntfsLabel,
		string mountPath,
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.CreateVhdxAsync(
			new CreateVhdxRequest
			{
				VhdxPath = vhdxPath,
				SizeBytes = sizeBytes,
				Dynamic = dynamic,
				NtfsLabel = ntfsLabel,
				MountPath = mountPath,
			},
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<AttachAndMountReply> AttachAndMountAsync(
		string vhdxPath,
		string mountPath,
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.AttachAndMountAsync(
			new AttachAndMountRequest
			{
				VhdxPath = vhdxPath,
				MountPath = mountPath,
			},
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<UnmountAndDetachReply> UnmountAndDetachAsync(
		string vhdxPath,
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.UnmountAndDetachAsync(
			new UnmountAndDetachRequest
			{
				VhdxPath = vhdxPath,
			},
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<ConvertFolderReply> ConvertFolderAsync(
		string folderPath,
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string ntfsLabel,
		bool deleteStaging,
		CancellationToken ct = default)
		=> SafeInvokeAsync(effectiveCancellationToken
		=> client.ConvertFolderAsync(
			new ConvertFolderRequest
			{
				FolderPath = folderPath,
				VhdxPath = vhdxPath,
				SizeBytes = sizeBytes,
				Dynamic = dynamic,
				NtfsLabel = ntfsLabel,
				DeleteStaging = deleteStaging,
			},
			cancellationToken: effectiveCancellationToken).ResponseAsync,ct);

	async Task<T> SafeInvokeAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
	{
		using var cts = timeout.HasValue
			? CancellationTokenSource.CreateLinkedTokenSource(ct)
			: null;
		cts?.CancelAfter(timeout!.Value);

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
}

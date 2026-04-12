using Grpc.Core;
using Grpc.Net.Client;
using VhdxCow.Contracts;

namespace VhdxCow.Client;

/// <summary>
/// High-level client for communicating with the VhdxCow Windows Service.
/// Connects via named pipe using gRPC.
/// </summary>
public sealed class VhdxCowClient : IVhdxCowClient
{
	readonly GrpcChannel channel;
	readonly VhdxService.VhdxServiceClient client;
	readonly TimeSpan? timeout;

	public VhdxCowClient(string pipeName = ServiceConstants.PipeName, TimeSpan? timeout = null)
	{
		channel = NamedPipeChannelFactory.Create(pipeName);
		client = new VhdxService.VhdxServiceClient(channel);
		this.timeout = timeout;
	}

	public Task<PingReply> PingAsync(CancellationToken ct = default) => InvokeAsync(effectiveCancellationToken
		=> client.PingAsync(
			new PingRequest(),
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<CreateChildReply> CreateChildAsync(
		string parentVhdxPath,
		string childVhdxPath,
		string mountPath,
		CancellationToken ct = default) => InvokeAsync(effectiveCancellationToken
		=> client.CreateChildAsync(
			new CreateChildRequest
			{
				ParentVhdxPath = parentVhdxPath,
				ChildVhdxPath = childVhdxPath,
				MountPath = mountPath,
			},
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<ResetChildReply> ResetChildAsync(string childVhdxPath, CancellationToken ct = default) => InvokeAsync(effectiveCancellationToken
		=> client.ResetChildAsync(
			new ResetChildRequest {
				ChildVhdxPath = childVhdxPath,
			},
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<DetachReply> DetachAsync(string childVhdxPath, CancellationToken ct = default) => InvokeAsync(effectiveCancellationToken
		=> client.DetachAsync(
			new DetachRequest
			{
				ChildVhdxPath = childVhdxPath,
			}, cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<GetStatusReply> GetStatusAsync(string childVhdxPath, CancellationToken ct = default) => InvokeAsync(effectiveCancellationToken
		=> client.GetStatusAsync(
			new GetStatusRequest
			{
				ChildVhdxPath = childVhdxPath,
			}, cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<PublishReply> PublishAsync(string overlayVhdxPath, CancellationToken ct = default)
		=> InvokeAsync(effectiveCancellationToken => client.PublishAsync(new PublishRequest
		{
			OverlayVhdxPath = overlayVhdxPath,
		}, cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public Task<ListMountsReply> ListMountsAsync(CancellationToken ct = default) => InvokeAsync(effectiveCancellationToken
		=> client.ListMountsAsync(
			new ListMountsRequest(),
			cancellationToken: effectiveCancellationToken).ResponseAsync, ct);

	public void Dispose() => channel.Dispose();

	async Task<T> InvokeAsync<T>(Func<CancellationToken, Task<T>> call, CancellationToken ct)
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
			throw new VhdxCowServiceException(
				$"VhdxCow service is not running. Start it with 'sc start {ServiceConstants.ServiceName}' or run Install-Service.ps1.",
				ex);
		}
		catch (OperationCanceledException) when (cts is { IsCancellationRequested: true } && !ct.IsCancellationRequested)
		{
			throw new TimeoutException(
				$"Operation timed out after {timeout!.Value.TotalSeconds:F0} seconds.");
		}
	}
}

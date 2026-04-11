using Grpc.Net.Client;
using VhdxCow.Contracts;

namespace VhdxCow.Client;

/// <summary>
/// High-level client for communicating with the VhdxCow Windows Service.
/// Connects via named pipe using gRPC.
/// </summary>
public sealed class VhdxCowClient : IDisposable
{
	readonly GrpcChannel channel;
	readonly VhdxService.VhdxServiceClient client;

	public VhdxCowClient(string pipeName = "VhdxCowService")
	{
		channel = NamedPipeChannelFactory.Create(pipeName);
		client = new VhdxService.VhdxServiceClient(channel);
	}

	public async Task<PingReply> PingAsync(CancellationToken ct = default)
	{
		return await client.PingAsync(new PingRequest(), cancellationToken: ct);
	}

	public async Task<CreateChildReply> CreateChildAsync(
		string parentVhdxPath,
		string childVhdxPath,
		string mountPath,
		CancellationToken ct = default)
	{
		return await client.CreateChildAsync(new CreateChildRequest
		{
			ParentVhdxPath = parentVhdxPath,
			ChildVhdxPath = childVhdxPath,
			MountPath = mountPath,
		}, cancellationToken: ct);
	}

	public async Task<ResetChildReply> ResetChildAsync(string childVhdxPath, CancellationToken ct = default)
	{
		return await client.ResetChildAsync(new ResetChildRequest
		{
			ChildVhdxPath = childVhdxPath,
		}, cancellationToken: ct);
	}

	public async Task<DetachReply> DetachAsync(string childVhdxPath, CancellationToken ct = default)
	{
		return await client.DetachAsync(new DetachRequest
		{
			ChildVhdxPath = childVhdxPath,
		}, cancellationToken: ct);
	}

	public async Task<GetStatusReply> GetStatusAsync(string childVhdxPath, CancellationToken ct = default)
	{
		return await client.GetStatusAsync(new GetStatusRequest
		{
			ChildVhdxPath = childVhdxPath,
		}, cancellationToken: ct);
	}

	public async Task<PublishReply> PublishAsync(string overlayVhdxPath, CancellationToken ct = default)
	{
		return await client.PublishAsync(new PublishRequest
		{
			OverlayVhdxPath = overlayVhdxPath,
		}, cancellationToken: ct);
	}

	public void Dispose()
	{
		channel.Dispose();
	}
}

using Grpc.Core;
using VhdxCow.Contracts;
using VhdxCow.Service.State;
using VhdxCow.Service.VhdxOperations;

namespace VhdxCow.Service.Services;

public sealed class VhdxGrpcService(
	IVhdxManager vhdxManager,
	IVolumeManager volumeManager,
	IStateStore stateStore,
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

	public override Task<CreateChildReply> CreateChild(CreateChildRequest request, ServerCallContext context)
	{
		logger.LogWarning("CreateChild not yet implemented");
		throw new RpcException(new Status(StatusCode.Unimplemented, "CreateChild is not yet implemented"));
	}

	public override Task<ResetChildReply> ResetChild(ResetChildRequest request, ServerCallContext context)
	{
		logger.LogWarning("ResetChild not yet implemented");
		throw new RpcException(new Status(StatusCode.Unimplemented, "ResetChild is not yet implemented"));
	}

	public override Task<DetachReply> Detach(DetachRequest request, ServerCallContext context)
	{
		logger.LogWarning("Detach not yet implemented");
		throw new RpcException(new Status(StatusCode.Unimplemented, "Detach is not yet implemented"));
	}

	public override Task<GetStatusReply> GetStatus(GetStatusRequest request, ServerCallContext context)
	{
		logger.LogWarning("GetStatus not yet implemented");
		throw new RpcException(new Status(StatusCode.Unimplemented, "GetStatus is not yet implemented"));
	}

	public override Task<PublishReply> Publish(PublishRequest request, ServerCallContext context)
	{
		logger.LogWarning("Publish not yet implemented");
		throw new RpcException(new Status(StatusCode.Unimplemented, "Publish is not yet implemented"));
	}
}

using VhdxCow.Contracts;

namespace VhdxCow.Client;

/// <summary>
/// Client interface for the VhdxCow service. Enables testing CLI without a real service.
/// </summary>
public interface IVhdxCowClient : IDisposable
{
	Task<PingReply> PingAsync(CancellationToken ct = default);

	Task<CreateChildReply> CreateChildAsync(
		string parentVhdxPath, string childVhdxPath, string mountPath,
		CancellationToken ct = default);

	Task<ResetChildReply> ResetChildAsync(string childVhdxPath, CancellationToken ct = default);

	Task<DetachReply> DetachAsync(string childVhdxPath, CancellationToken ct = default);

	Task<GetStatusReply> GetStatusAsync(string childVhdxPath, CancellationToken ct = default);

	Task<PublishReply> PublishAsync(string overlayVhdxPath, CancellationToken ct = default);

	Task<ListMountsReply> ListMountsAsync(CancellationToken ct = default);
}

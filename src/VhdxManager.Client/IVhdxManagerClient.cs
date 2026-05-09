using VhdxManager.Contracts;

namespace VhdxManager.Client;

/// <summary>
/// Client interface for the VhdxManager service. Enables testing CLI without a real service.
/// </summary>
public interface IVhdxManagerClient : IDisposable
{
	Task<PingReply> PingAsync(CancellationToken ct = default);

	Task<CreateChildReply> CreateChildAsync(
		string parentVhdxPath,
		string childVhdxPath,
		string mountPath,
		CancellationToken ct = default);

	Task<ResetChildReply> ResetChildAsync(
		string childVhdxPath,
		CancellationToken ct = default);

	Task<DetachReply> DetachAsync(
		string childVhdxPath,
		CancellationToken ct = default);

	Task<GetStatusReply> GetStatusAsync(
		string childVhdxPath,
		CancellationToken ct = default);

	Task<PublishReply> PublishAsync(
		string overlayVhdxPath,
		CancellationToken ct = default);

	Task<ListMountsReply> ListMountsAsync(CancellationToken ct = default);

	Task<CreateVhdxReply> CreateVhdxAsync(
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string ntfsLabel,
		string mountPath,
		CancellationToken ct = default);

	Task<AttachAndMountReply> AttachAndMountAsync(
		string vhdxPath,
		string mountPath,
		CancellationToken ct = default);

	Task<UnmountAndDetachReply> UnmountAndDetachAsync(
		string vhdxPath,
		CancellationToken ct = default);

	Task<ConvertFolderReply> ConvertFolderAsync(
		string folderPath,
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string ntfsLabel,
		bool deleteStaging,
		CancellationToken ct = default);
}

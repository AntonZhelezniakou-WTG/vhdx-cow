using VhdxManager.Contracts;

namespace VhdxManager.Client;

/// <summary>
/// Client interface for the VhdxManager service. Enables testing CLI without a real service.
/// <para>
/// Mutating operations are server-streaming on the wire — they emit
/// <see cref="ProgressEvent"/>s for each step (STARTED → COMPLETED/FAILED) and end
/// with the operation's reply. Callers may pass an <c>onProgress</c> callback to
/// observe progress in real time; passing <c>null</c> just collects the final
/// reply.
/// </para>
/// </summary>
public interface IVhdxManagerClient : IDisposable
{
	Task<PingReply> PingAsync(CancellationToken ct = default);

	Task<GetStatusReply> GetStatusAsync(
		string childVhdxPath,
		CancellationToken ct = default);

	Task<ListMountsReply> ListMountsAsync(CancellationToken ct = default);

	/// <summary>Reads the persisted service-side defaults (e.g. add-defender-exclusion).</summary>
	Task<GetSettingsReply> GetSettingsAsync(CancellationToken ct = default);

	/// <summary>Writes service-side defaults. Pass clearAddDefenderExclusion=true to wipe the override.</summary>
	Task<SetSettingsReply> SetSettingsAsync(
		bool? defaultAddDefenderExclusion,
		bool clearAddDefenderExclusion,
		CancellationToken ct = default);

	// ---- Streaming mutating operations ----

	Task<CreateChildReply> CreateChildAsync(
		string parentVhdxPath,
		string childVhdxPath,
		string mountPath,
		bool addDefenderExclusion,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);

	Task<ResetChildReply> ResetChildAsync(
		string childVhdxPath,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);

	Task<DetachReply> DetachAsync(
		string childVhdxPath,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);

	Task<PublishReply> PublishAsync(
		string overlayVhdxPath,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);

	Task<CreateVhdxReply> CreateVhdxAsync(
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string volumeLabel,
		string mountPath,
		string filesystem,
		bool addDefenderExclusion,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);

	Task<AttachAndMountReply> AttachAndMountAsync(
		string vhdxPath,
		string mountPath,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);

	Task<UnmountAndDetachReply> UnmountAndDetachAsync(
		string vhdxPath,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);

	Task<ConvertFolderReply> ConvertFolderAsync(
		string folderPath,
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string volumeLabel,
		string filesystem,
		bool deleteStaging,
		bool addDefenderExclusion,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);
}

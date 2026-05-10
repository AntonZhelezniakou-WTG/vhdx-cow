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

	// ---- Streaming mutating operations ----

	Task<CreateChildReply> CreateChildAsync(
		string parentVhdxPath,
		string childVhdxPath,
		string mountPath,
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

	/// <summary>
	/// Create a standalone VHDX, partition GPT, format the volume, optionally mount.
	/// </summary>
	/// <param name="filesystem">Filesystem name. Empty string = service default (ReFS). Other accepted values: "ReFS", "NTFS".</param>
	Task<CreateVhdxReply> CreateVhdxAsync(
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string volumeLabel,
		string mountPath,
		string filesystem,
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

	/// <summary>
	/// Rename source folder aside, create + mount a new VHDX in its place, robocopy contents back.
	/// </summary>
	/// <param name="filesystem">Filesystem name. Empty string = service default (ReFS). Other accepted values: "ReFS", "NTFS".</param>
	Task<ConvertFolderReply> ConvertFolderAsync(
		string folderPath,
		string vhdxPath,
		long sizeBytes,
		bool dynamic,
		string volumeLabel,
		string filesystem,
		bool deleteStaging,
		Action<ProgressEvent>? onProgress = null,
		CancellationToken ct = default);
}

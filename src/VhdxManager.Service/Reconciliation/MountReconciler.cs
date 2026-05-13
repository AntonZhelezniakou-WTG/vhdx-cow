using System.ComponentModel;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VhdxManager.Service.State;
using VhdxManager.Service.VhdxOperations;

namespace VhdxManager.Service.Reconciliation;

/// <summary>
/// At service startup, re-attaches every VHDX recorded in <see cref="IStateStore"/>
/// and re-establishes its NTFS folder mount point.
///
/// Rationale: VirtDisk's PERMANENT_LIFETIME attach flag survives handle close and
/// service restart, but the Volume Manager's mount table is in-memory and is wiped
/// on reboot. Without an explicit reconciliation pass, a VHDX created via
/// <c>vhdx create</c> with a folder mount stops being mounted after the host reboots.
/// </summary>
sealed class MountReconciler(
	IStateStore stateStore,
	IVirtDiskManager virtDisk,
	IVolumeManager volume,
	ILogger<MountReconciler> logger) : IHostedService
{
	public async Task StartAsync(CancellationToken ct)
	{
		IReadOnlyList<MountedDiskState> entries;
		try
		{
			entries = await stateStore.GetAllAsync(ct);
		}
		catch (Exception ex)
		{
			logger.LogError(ex, "Failed to load mount state; skipping reconciliation");
			return;
		}

		if (entries.Count == 0)
		{
			logger.LogDebug("No persisted mounts to reconcile");
			return;
		}

		logger.LogInformation("Reconciling {Count} persisted mount(s) after startup", entries.Count);

		foreach (var entry in entries)
		{
			ct.ThrowIfCancellationRequested();

			try
			{
				await ReconcileOneAsync(entry, ct);
			}
			catch (Exception ex)
			{
				// One bad entry must not block the rest — log and continue.
				logger.LogError(
					ex,
					"Failed to reconcile mount for {Child} at {MountPath}",
					entry.ChildVhdxPath,
					entry.MountPath);
			}
		}
	}

	public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

	async Task ReconcileOneAsync(MountedDiskState entry, CancellationToken ct)
	{
		if (!File.Exists(entry.ChildVhdxPath))
		{
			logger.LogWarning(
				"Persisted child VHDX {Child} no longer exists; removing stale state entry",
				entry.ChildVhdxPath);
			await stateStore.RemoveAsync(entry.ChildVhdxPath, ct);
			return;
		}

		var info = await virtDisk.GetInfoAsync(entry.ChildVhdxPath, ct);

		string physicalPath;
		if (info.IsAttached && !string.IsNullOrEmpty(info.PhysicalPath))
		{
			logger.LogDebug(
				"VHDX {Child} already attached at {PhysicalPath}; refreshing mount point only",
				entry.ChildVhdxPath,
				info.PhysicalPath);
			physicalPath = info.PhysicalPath;
		}
		else
		{
			logger.LogInformation("Re-attaching VHDX {Child}", entry.ChildVhdxPath);
			physicalPath = await virtDisk.AttachAsync(entry.ChildVhdxPath, ct);
		}

		// Re-discover the volume GUID: on a fresh boot the host's volume table is
		// empty, so we must explicitly remount even if the GUID is unchanged.
		var volumeGuid = await volume.GetVolumeGuidPathAsync(physicalPath, ct);

		Directory.CreateDirectory(entry.MountPath);

		// SetVolumeMountPoint refuses to overwrite an existing reparse point. Strip
		// any stale one first; the call is a no-op if the folder has no mount point.
		try
		{
			await volume.UnmountFolderAsync(entry.MountPath, ct);
		}
		catch (Win32Exception ex)
		{
			logger.LogDebug(
				ex,
				"No existing mount point to clear at {MountPath} (expected on first reconciliation after boot)",
				entry.MountPath);
		}

		await volume.MountToFolderAsync(volumeGuid, entry.MountPath, ct);

		if (!string.Equals(volumeGuid, entry.VolumeGuidPath, StringComparison.OrdinalIgnoreCase))
		{
			logger.LogInformation(
				"Volume GUID for {Child} changed from {Old} to {New}; updating state",
				entry.ChildVhdxPath,
				entry.VolumeGuidPath,
				volumeGuid);
			await stateStore.AddAsync(entry with { VolumeGuidPath = volumeGuid }, ct);
		}

		logger.LogInformation(
			"Reconciled mount {Child} → {MountPath}",
			entry.ChildVhdxPath,
			entry.MountPath);
	}
}

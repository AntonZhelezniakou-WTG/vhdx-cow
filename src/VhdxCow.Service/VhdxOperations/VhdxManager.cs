namespace VhdxCow.Service.VhdxOperations;

/// <summary>
/// VHDX operations via P/Invoke (CsWin32) against VirtDisk.dll.
/// Stub implementation — actual P/Invoke calls will be added in Phase 2.
/// </summary>
public sealed class VhdxManager(ILogger<VhdxManager> logger) : IVhdxManager
{
	public Task CreateDifferencingDiskAsync(string parentVhdxPath, string childVhdxPath, CancellationToken ct)
	{
		logger.LogInformation(
			"CreateDifferencingDisk: Parent={ParentPath}, Child={ChildPath}",
			parentVhdxPath, childVhdxPath);

		throw new NotImplementedException("VHDX P/Invoke not yet implemented");
	}

	public Task<string> AttachAsync(string vhdxPath, CancellationToken ct)
	{
		logger.LogInformation("Attach: {VhdxPath}", vhdxPath);
		throw new NotImplementedException("VHDX P/Invoke not yet implemented");
	}

	public Task DetachAsync(string vhdxPath, CancellationToken ct)
	{
		logger.LogInformation("Detach: {VhdxPath}", vhdxPath);
		throw new NotImplementedException("VHDX P/Invoke not yet implemented");
	}

	public Task MergeAsync(string childVhdxPath, CancellationToken ct)
	{
		logger.LogInformation("Merge: {ChildPath}", childVhdxPath);
		throw new NotImplementedException("VHDX P/Invoke not yet implemented");
	}

	public Task<VhdxInfo> GetInfoAsync(string vhdxPath, CancellationToken ct)
	{
		logger.LogInformation("GetInfo: {VhdxPath}", vhdxPath);
		throw new NotImplementedException("VHDX P/Invoke not yet implemented");
	}
}

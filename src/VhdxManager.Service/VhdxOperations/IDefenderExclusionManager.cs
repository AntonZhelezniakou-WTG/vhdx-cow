namespace VhdxManager.Service.VhdxOperations;

/// <summary>
/// Registers VHDX file paths with Windows Defender's exclusion list (so the
/// virtual filesystem on top of the host filesystem isn't double-scanned).
///
/// Implementations shell out to PowerShell's <c>Add-MpPreference -ExclusionPath</c>
/// because the equivalent native API requires WMI / Defender-COM bindings that
/// drag in considerably more surface area.
///
/// On a managed machine, this operation can be blocked by Group Policy. Callers
/// MUST treat failures as best-effort and surface them as warnings rather than
/// failing the parent operation.
/// </summary>
public interface IDefenderExclusionManager
{
	/// <summary>
	/// Adds <paramref name="vhdxPath"/> to Defender's exclusion list. Throws on
	/// failure. The caller is responsible for catching and converting the
	/// exception into a non-fatal warning.
	/// </summary>
	Task AddExclusionAsync(string vhdxPath, CancellationToken ct);
}

/// <summary>
/// Thrown by <see cref="IDefenderExclusionManager"/> when registration is
/// rejected by Group Policy (or otherwise denied by the OS). The
/// <see cref="Exception.Message"/> is suitable for direct user display.
/// </summary>
public sealed class DefenderPolicyBlockedException : InvalidOperationException
{
	public DefenderPolicyBlockedException(string message) : base(message) { }
	public DefenderPolicyBlockedException(string message, Exception inner) : base(message, inner) { }
	public DefenderPolicyBlockedException() { }
}

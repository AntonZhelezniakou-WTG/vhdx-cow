namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Host-side Hyper-V operations against a single VM: snapshot lifecycle,
/// power on/off, checkpoint inspection. Thin wrappers over the Hyper-V
/// PowerShell module — running these on a machine without Hyper-V
/// installed will throw a <see cref="PowerShellInvocationException"/>
/// (callers gate on <see cref="E2EConfig.LoadOrSkip"/> first).
/// </summary>
public sealed class VmHost(string vmName, PowerShellRunner ps)
{
	/// <summary>
	/// Restore the given snapshot and wait for the VM to settle (snapshots
	/// taken from an offline VM leave it Off; saved-state snapshots restore
	/// to Saved, then Start-VM is needed). Caller is expected to call
	/// <see cref="StartAsync"/> + <see cref="WaitGuestReadyAsync"/> after.
	/// </summary>
	public Task RestoreSnapshotAsync(string snapshotName, CancellationToken ct = default)
		=> ps.RunVoidAsync(
			$"Restore-VMSnapshot -VMName '{vmName}' -Name '{Escape(snapshotName)}' -Confirm:$false",
			ct);

	public Task StartAsync(CancellationToken ct = default)
		// Idempotent: starting an already-Running VM is a no-op + warning.
		=> ps.RunVoidAsync(
			$"if ((Get-VM -Name '{vmName}').State -ne 'Running') {{ Start-VM -Name '{vmName}' | Out-Null }}",
			ct);

	/// <summary>
	/// Graceful shutdown via Stop-VM. If <paramref name="turnOff"/> is true
	/// the VM is hard-powered-off (equivalent to pulling the cord) — use for
	/// post-test cleanup, never to take a clean checkpoint from.
	/// </summary>
	public Task StopAsync(bool turnOff, CancellationToken ct = default)
	{
		var args = turnOff ? "-TurnOff -Force" : "-Force";
		return ps.RunVoidAsync(
			$"if ((Get-VM -Name '{vmName}').State -ne 'Off') {{ Stop-VM -Name '{vmName}' {args} }}",
			ct);
	}

	public Task<bool> SnapshotExistsAsync(string snapshotName, CancellationToken ct = default)
		=> ps.RunJsonAsync<bool>(
			$"[bool](Get-VMSnapshot -VMName '{vmName}' -Name '{Escape(snapshotName)}' -ErrorAction SilentlyContinue)",
			ct);

	public Task TakeCheckpointAsync(string snapshotName, CancellationToken ct = default)
		=> ps.RunVoidAsync(
			$"Checkpoint-VM -Name '{vmName}' -SnapshotName '{Escape(snapshotName)}'",
			ct);

	/// <summary>
	/// Block until <c>C:\Setup\boot-complete.flag</c> is reachable through a
	/// fresh PSSession into the guest. Implemented in PowerShell so we can
	/// re-use the polling logic from <c>tests/e2e/lib/Helpers.ps1</c>
	/// (Wait-VmReady) — same retry semantics as the bootstrap script.
	/// </summary>
	public Task WaitGuestReadyAsync(string guestUser, string guestPassword,
		TimeSpan timeout, CancellationToken ct = default)
	{
		// Helpers.ps1::Wait-VmReady expects -Credential -TimeoutMinutes; we
		// dot-source it via PowerShellRunner so the function is in scope.
		var minutes = Math.Max(1, (int)Math.Ceiling(timeout.TotalMinutes));
		var script = $"""

			$pw = ConvertTo-SecureString '{Escape(guestPassword)}' -AsPlainText -Force
			$cred = New-Object System.Management.Automation.PSCredential('{vmName}\{Escape(guestUser)}', $pw)
			Wait-VmReady -VmName '{vmName}' -Credential $cred -TimeoutMinutes {minutes} | Out-Null
			""";
		return ps.RunVoidAsync(script, ct);
	}

	static string Escape(string s) => s.Replace("'", "''");
}

using System;
using System.Threading.Tasks;
using NUnit.Framework;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Base class for every E2E fixture. Owns the per-fixture VM lifecycle:
/// restore a checkpoint, start the VM, wait until the guest is reachable,
/// open a <see cref="GuestSession"/>. Subclasses just declare which checkpoint
/// they want via <see cref="CheckpointName"/> and run their tests.
/// </summary>
[Category("E2E")]
[Parallelizable(ParallelScope.None)]
public abstract class E2EFixtureBase
{
	protected E2EConfig         Config { get; private set; } = null!;
	protected PowerShellRunner  Ps     { get; private set; } = null!;
	protected VmHost            Vm     { get; private set; } = null!;
	protected GuestSession      Guest  { get; private set; } = null!;

	/// <summary>Snapshot to restore in <see cref="BaseOneTimeSetUp"/>.</summary>
	protected abstract string CheckpointName { get; }

	/// <summary>
	/// How long <see cref="BaseOneTimeSetUp"/> waits for the guest to become
	/// reachable after Start-VM. Default 5 min covers the ~30-60 s typical
	/// boot from a checkpoint + slack for first-boot-after-restore quirks.
	/// </summary>
	protected virtual TimeSpan BootTimeout => TimeSpan.FromMinutes(5);

	[OneTimeSetUp]
	public async Task BaseOneTimeSetUp()
	{
		Config = E2EConfig.LoadOrSkip();
		Ps     = new PowerShellRunner(Config.HelpersScriptPath);
		Vm     = new VmHost(Config.VmName, Ps);
		Guest  = new GuestSession(Config.VmName, Config.GuestUsername, Config.GuestPassword, Ps);

		await Vm.RestoreSnapshotAsync(CheckpointName);
		await Vm.StartAsync();
		await Vm.WaitGuestReadyAsync(Config.GuestUsername, Config.GuestPassword, BootTimeout);

		await OnGuestReadyAsync();
	}

	/// <summary>
	/// Override to run one-time setup inside the guest after the VM is up
	/// (e.g. install the MSI in the installer fixture, copy test artefacts).
	/// </summary>
	protected virtual Task OnGuestReadyAsync() => Task.CompletedTask;

	[OneTimeTearDown]
	public async Task BaseOneTimeTearDown()
	{
		// Hard turn-off — the VM is going to be restored from a checkpoint
		// next time anyway, so we don't owe it a graceful shutdown.
		if (Vm is not null)
		{
			try
			{
				await Vm.StopAsync(turnOff: true);
			}
			catch
			{
				// Swallow — if the host PS bridge is wedged we don't want to
				// mask the actual test failure with a teardown error.
			}
		}
	}
}

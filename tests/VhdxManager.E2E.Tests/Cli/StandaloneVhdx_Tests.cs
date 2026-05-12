using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Cli;

/// <summary>
/// Standalone-VHDX lifecycle driven through the CLI: create → list (sees it)
/// → status → unmount → mount → delete. All steps run inside one VM boot;
/// <c>[Order]</c> enforces sequence and <c>Assert.Inconclusive</c> short-
/// circuits dependent tests when an earlier step failed (otherwise every
/// downstream test repeats the same root-cause error).
///
/// <para>VHDX files are created under <c>C:\E2E\</c> on the guest — a
/// dedicated directory so we never collide with anything the installer
/// left behind, and so cleanup-on-failure is just <c>Remove-Item</c>.</para>
/// </summary>
[TestFixture]
[Order(20)]
public sealed class StandaloneVhdx_Tests : InstalledFixtureBase
{
	const string TestDir   = @"C:\E2E";
	const string VhdxPath  = @"C:\E2E\standalone.vhdx";
	const string MountPath = @"C:\E2E\mount";

	protected override async Task OnGuestReadyAsync()
	{
		// Fresh, empty workspace on the guest. The mount target must exist
		// before `vhmgr create --mount` will attach to it.
		await Guest.InvokeVoidAsync($"""

			Remove-Item -LiteralPath '{TestDir}' -Recurse -Force -ErrorAction SilentlyContinue
			New-Item -ItemType Directory -Path '{TestDir}' -Force | Out-Null
			New-Item -ItemType Directory -Path '{MountPath}' -Force | Out-Null

			""");
	}

	[Test, Order(1)]
	public async Task Create_Standalone_Vhdx_And_Mount()
	{
		// Small dynamic VHDX so the test doesn't churn gigabytes — 64 MB is
		// well above the NTFS minimum (~8 MB) and small enough that format+
		// mount completes in a couple of seconds.
		//
		// We pass --add-defender-exclusion false explicitly: otherwise the
		// CLI's DefenderExclusionResolver falls through to an interactive
		// Spectre.Console prompt, which dies under a redirected stdout
		// ("Cannot show selection prompt since the current terminal does
		// not support ANSI escape sequences"). The value itself doesn't
		// matter for these assertions.
		//
		// --filesystem NTFS: ReFS (the CLI default) refuses sizes under a
		// few hundred MB ("Format-Volume: Size Not Supported"). NTFS happily
		// formats a 64 MB partition, keeping the test under a couple of
		// seconds. We're testing the CLI pipeline, not the filesystem layer.
		var r = await Vhmgr.RunAsync(Guest,
			$"create --path \"{VhdxPath}\" --size 64M --label e2etest --mount \"{MountPath}\" " +
			$"--filesystem NTFS --add-defender-exclusion false");

		r.Succeeded.Should().BeTrue(
			$"`vhmgr create` returned {r.ExitCode}.\nstdout: {r.StdoutText}\nstderr: {r.StderrText}");
		// CreateCommand prints "Volume:" / "Volume GUID:" when mounted — both
		// forms appear in the codebase. Accept either to absorb a future
		// formatter tweak.
		(r.StdoutText.Contains("Volume", StringComparison.OrdinalIgnoreCase)
		 || r.StdoutText.Contains("GUID", StringComparison.OrdinalIgnoreCase))
			.Should().BeTrue($"expected create to confirm the volume mount. stdout: {r.StdoutText}");

		await GuestFs.AssertFileExistsAsync(Guest, VhdxPath);
	}

	[Test, Order(2)]
	public async Task List_Shows_The_New_Mount()
	{
		var r = await Vhmgr.RunAsync(Guest, "list");
		r.Succeeded.Should().BeTrue();
		// ListCommand prints "Child: <path>" / "Mount: <path>" per entry.
		// Don't lock down the case of the drive letter — the service may
		// canonicalise paths.
		r.StdoutText.Should().Contain("standalone.vhdx",
			$"the just-created VHDX should appear in `vhmgr list`. stdout: {r.StdoutText}");
		r.StdoutText.Should().NotContain("No active mounts");
	}

	// NOTE: there's no Status_Reports_Attached test here. `vhmgr status
	// --child <path>` is a differencing-workflow command — it reads from
	// the service's managed-children registry, which is populated by
	// `init`, not by `create`. A standalone VHDX is mounted but not
	// registered as a "managed child", so `status` reports Attached:False.
	// That's tested in Differencing_Tests instead.

	[Test, Order(4)]
	public async Task Unmount_Keeps_The_File()
	{
		var r = await Vhmgr.RunAsync(Guest, $"unmount --path \"{VhdxPath}\"");
		r.Succeeded.Should().BeTrue(
			$"`vhmgr unmount` returned {r.ExitCode}. stderr: {r.StderrText}");

		// File must still exist (unmount is non-destructive)…
		await GuestFs.AssertFileExistsAsync(Guest, VhdxPath);
		// …and list should be empty again.
		var list = await Vhmgr.RunAsync(Guest, "list");
		list.StdoutText.Should().Contain("No active mounts",
			$"after unmount the list should be empty. stdout: {list.StdoutText}");
	}

	[Test, Order(5)]
	public async Task Mount_Reattaches_Existing_Vhdx()
	{
		var r = await Vhmgr.RunAsync(Guest,
			$"mount --path \"{VhdxPath}\" --mount \"{MountPath}\"");
		r.Succeeded.Should().BeTrue(
			$"`vhmgr mount` returned {r.ExitCode}. stderr: {r.StderrText}");

		// And the mount shows up again.
		var list = await Vhmgr.RunAsync(Guest, "list");
		list.StdoutText.Should().Contain("standalone.vhdx");
	}

	[Test, Order(6)]
	public async Task Delete_Removes_The_Vhdx_File()
	{
		// `delete` takes a positional <path>, not --path.
		var r = await Vhmgr.RunAsync(Guest, $"delete \"{VhdxPath}\"");
		r.Succeeded.Should().BeTrue(
			$"`vhmgr delete` returned {r.ExitCode}. stderr: {r.StderrText}");

		// File gone…
		var exists = await GuestFs.ExistsAsync(Guest, VhdxPath);
		exists.Should().BeFalse("delete should remove the VHDX file");
		// …and list empty.
		var list = await Vhmgr.RunAsync(Guest, "list");
		list.StdoutText.Should().Contain("No active mounts");
	}
}

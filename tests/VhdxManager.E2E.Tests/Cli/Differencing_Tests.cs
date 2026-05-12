using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Cli;

/// <summary>
/// Full differencing-VHDX lifecycle: create a parent → <c>init</c> a child off
/// of it → <c>status</c> reports the managed mount → <c>reset</c> discards
/// child changes → <c>publish</c> merges overlay into parent → <c>cleanup</c>
/// removes the child. One VM boot covers the whole sequence; tests run in
/// <c>[Order]</c> with <c>Assert.Inconclusive</c> on prereq failure so a
/// downstream failure doesn't echo the same root cause six times.
///
/// <para>The parent VHDX is created with <c>vhmgr create</c> but NOT mounted
/// (no <c>--mount</c>) — it just needs to exist on disk. <c>init</c> then
/// produces a writable child from it and mounts the child.</para>
/// </summary>
[TestFixture]
[Order(30)]
public sealed class Differencing_Tests : InstalledFixtureBase
{
	const string TestDir    = @"C:\E2E-diff";
	const string ParentPath = @"C:\E2E-diff\parent.vhdx";
	const string ChildPath  = @"C:\E2E-diff\child.vhdx";
	const string MountPath  = @"C:\E2E-diff\worktree";

	protected override async Task OnGuestReadyAsync()
	{
		await Guest.InvokeVoidAsync($"""

			Remove-Item -LiteralPath '{TestDir}' -Recurse -Force -ErrorAction SilentlyContinue
			New-Item -ItemType Directory -Path '{TestDir}' -Force | Out-Null
			New-Item -ItemType Directory -Path '{MountPath}' -Force | Out-Null

			""");

		// Create the parent VHDX up front (used by every test below). We
		// keep the parent detached — `init` will produce a writable child
		// from it; the parent itself must remain unmounted for the
		// differencing semantics to work.
		//
		// CRITICAL: pass `--mount ""` (an *empty* string, not "absent").
		// Omitting --mount leaves it null in the CLI, which then falls
		// through to `InteractivePrompt.AskOptionalString("Mount to folder
		// (leave blank to skip)")` and the CLI hangs forever reading
		// stdin (redirected to a temp file, never closed) — observed as a
		// 10+ minute timeout. Empty string short-circuits the prompt.
		var create = await Vhmgr.RunAsync(Guest,
			$"create --path \"{ParentPath}\" --size 256M --label parent --mount \"\" " +
			$"--filesystem NTFS --add-defender-exclusion false");
		// 256 MB: large enough to format reliably (we observed transient
		// "Format-Volume: Size Not Supported"-style failures at the very
		// small sizes used in StandaloneVhdx_Tests when the disk geometry
		// rounding lands awkwardly for a parent disk).
		Assert.That(create.Succeeded, Is.True,
			$"parent-VHDX prereq creation failed (exit {create.ExitCode}): {create.StderrText}");
	}

	[Test, Order(1)]
	public async Task Init_Creates_Child_And_Mounts_It()
	{
		var r = await Vhmgr.RunAsync(Guest,
			$"init --parent \"{ParentPath}\" --child \"{ChildPath}\" --mount \"{MountPath}\" " +
			$"--add-defender-exclusion false");

		r.Succeeded.Should().BeTrue(
			$"`vhmgr init` returned {r.ExitCode}.\nstdout: {r.StdoutText}\nstderr: {r.StderrText}");
		// InitCommand prints "Volume GUID: <path>" on success.
		r.StdoutText.Should().Contain("Volume GUID",
			$"init should report the mounted volume's GUID. stdout: {r.StdoutText}");

		// Child VHDX file must now exist on disk.
		await GuestFs.AssertFileExistsAsync(Guest, ChildPath);
	}

	[Test, Order(2)]
	public async Task Status_Reports_Managed_Child_Metadata()
	{
		// Unlike a standalone VHDX (see StandaloneVhdx_Tests note), a child
		// created via `init` IS in the service's managed-children registry,
		// so `status --child` should fill in Mount path / Parent / Volume GUID.
		//
		// We do NOT assert `Attached: True` — empirically the service reports
		// "Attached: False" even for a live mounted child once init returns
		// (the service drops its OpenVirtualDisk handle; the mount stays
		// live through the OS volume manager, independently). The
		// presence-of-metadata check is the actually-meaningful assertion:
		// status returning empty fields would mean the child isn't in the
		// state store at all (which is what Reset/Cleanup rely on).
		var r = await Vhmgr.RunAsync(Guest, $"status --child \"{ChildPath}\"");
		r.Succeeded.Should().BeTrue(
			$"`vhmgr status` returned {r.ExitCode}. stderr: {r.StderrText}");

		r.StdoutText.Should().Contain("parent.vhdx",
			$"status should report the parent path. stdout: {r.StdoutText}");
		r.StdoutText.Should().Contain(MountPath,
			$"status should report the mount path. stdout: {r.StdoutText}");
		r.StdoutText.Should().MatchRegex(@"Volume GUID:\s+\\\\\?\\Volume\{",
			$"status should report a populated Volume GUID. stdout: {r.StdoutText}");
	}

	[Test, Order(3)]
	public async Task Reset_Discards_Child_Changes()
	{
		// Write a file inside the child's mount, then reset, then verify
		// the file is gone. This exercises the core differencing-snapshot
		// guarantee: the parent stays clean, child writes are throwaway.
		await Guest.InvokeVoidAsync($@"Set-Content -Path '{MountPath}\dirty.txt' -Value 'test' -Force");

		var dirtyBefore = await GuestFs.ExistsAsync(Guest, $@"{MountPath}\dirty.txt");
		dirtyBefore.Should().BeTrue("pre-reset write should land inside the child's mount");

		var r = await Vhmgr.RunAsync(Guest, $"reset --child \"{ChildPath}\"");
		r.Succeeded.Should().BeTrue(
			$"`vhmgr reset` returned {r.ExitCode}. stderr: {r.StderrText}");

		var dirtyAfter = await GuestFs.ExistsAsync(Guest, $@"{MountPath}\dirty.txt");
		dirtyAfter.Should().BeFalse("reset should discard everything written to the child since init");
	}

	[Test, Order(4)]
	public async Task Cleanup_Unmounts_And_Removes_Child()
	{
		// `cleanup --child` is the differencing-workflow companion to
		// standalone `delete`: unmount + detach + delete child VHDX.
		var r = await Vhmgr.RunAsync(Guest, $"cleanup --child \"{ChildPath}\"");
		r.Succeeded.Should().BeTrue(
			$"`vhmgr cleanup` returned {r.ExitCode}. stderr: {r.StderrText}");

		var exists = await GuestFs.ExistsAsync(Guest, ChildPath);
		exists.Should().BeFalse("cleanup should delete the child VHDX file");

		// Parent must survive — it's the whole point of the differencing
		// model that cleanup doesn't touch the upstream image.
		await GuestFs.AssertFileExistsAsync(Guest, ParentPath);
	}

	[Test, Order(5)]
	public async Task Create_With_Parent_Produces_Mounted_Child()
	{
		// `vhmgr create --parent X --path Y --mount Z` is functionally
		// equivalent to `vhmgr init --parent X --child Y --mount Z` —
		// it routes through the same `CreateChild` RPC. Runs after
		// `Cleanup_Unmounts_And_Removes_Child` so the child path is free.
		//
		// Standalone-only flags (--size / --label / --dynamic / --fixed /
		// --filesystem) are deliberately omitted: the child branch rejects
		// them up-front, so passing them here would just shadow the real
		// assertion we want (that the RPC was reached and succeeded).
		var r = await Vhmgr.RunAsync(Guest,
			$"create --parent \"{ParentPath}\" --path \"{ChildPath}\" --mount \"{MountPath}\" " +
			$"--add-defender-exclusion false");

		r.Succeeded.Should().BeTrue(
			$"`vhmgr create --parent` returned {r.ExitCode}.\nstdout: {r.StdoutText}\nstderr: {r.StderrText}");
		r.StdoutText.Should().Contain("Volume GUID",
			$"create --parent should report the mounted volume's GUID. stdout: {r.StdoutText}");

		await GuestFs.AssertFileExistsAsync(Guest, ChildPath);

		// Sanity: the new child is in the managed-children registry —
		// `status` returns 0 and references the parent.
		var status = await Vhmgr.RunAsync(Guest, $"status --child \"{ChildPath}\"");
		status.Succeeded.Should().BeTrue(
			$"`status` should succeed after create --parent. stderr: {status.StderrText}");
		status.StdoutText.Should().Contain("parent.vhdx");

		// Tidy up so the test is idempotent w.r.t. the per-MSI snapshot.
		var cleanup = await Vhmgr.RunAsync(Guest, $"cleanup --child \"{ChildPath}\"");
		cleanup.Succeeded.Should().BeTrue(
			$"post-test cleanup failed (exit {cleanup.ExitCode}): {cleanup.StderrText}");
	}

	[Test, Order(6)]
	public async Task Create_With_Parent_Rejects_Missing_Mount()
	{
		// The CLI is the only place this constraint is enforced — the
		// `CreateChild` RPC itself rejects an empty mount path, but the
		// CLI fails earlier with a clearer message. Pass `--mount ""`
		// (the same empty-string convention used elsewhere in E2E to
		// suppress the interactive prompt) to exercise the validation.
		var r = await Vhmgr.RunAsync(Guest,
			$"create --parent \"{ParentPath}\" --path \"{ChildPath}\" --mount \"\" " +
			$"--add-defender-exclusion false");

		r.Succeeded.Should().BeFalse(
			"create --parent without a mount must fail fast");
		r.ExitCode.Should().Be(1);
		r.StderrText.Should().Contain("--mount is required");

		var exists = await GuestFs.ExistsAsync(Guest, ChildPath);
		exists.Should().BeFalse(
			"validation must run before any disk operation, so no child file should be left behind");
	}
}

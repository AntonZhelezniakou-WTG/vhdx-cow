using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Cli;

/// <summary>
/// Full <c>vhdx publish</c> lifecycle against two registered children:
/// <list type="number">
/// <item>Create a standalone parent VHDX (no mount).</item>
/// <item><c>vhdx init</c> a managed <em>child</em> from the parent — represents
///   a worker that will be recreated after the publish.</item>
/// <item><c>vhdx init</c> an <em>overlay</em> from the same parent — this is
///   the staging child whose changes will be merged into the parent.</item>
/// <item>Write a marker file to the overlay's mount path.</item>
/// <item><c>vhdx publish --overlay &lt;overlay.vhdx&gt;</c> — merges the
///   overlay into the parent, recreates all registered children.</item>
/// <item>Assert the marker is now visible through every child mount (it was in
///   the overlay → merged into parent → inherited by all fresh children).</item>
/// </list>
///
/// <para>The service's <c>Publish</c> handler was fixed alongside this fixture
/// to handle two bugs that were dormant because <c>publish</c> was never
/// exercised end-to-end:</para>
/// <list type="bullet">
/// <item><b>Double-detach bug</b> — the original code detached + deleted every
///   state-store child in Step 1 including the overlay, then tried to open the
///   now-missing overlay file in Step 2 (ERROR_FILE_NOT_FOUND). Fix: exclude the
///   overlay from Step 1; handle its unmount + detach in Step 2.</item>
/// <item><b>Concurrent-modification bug</b> — <c>GetAllAsync</c> returns a live
///   <c>ReadOnlyCollection</c> wrapper over the in-memory cache. The "Recreating"
///   step called <c>stateStore.AddAsync</c> inside a <c>foreach</c> over that
///   wrapper, which modifies the backing <c>List&lt;T&gt;</c> and triggers
///   <c>InvalidOperationException: Collection was modified</c>. Fix: materialise
///   the result of <c>GetAllAsync</c> into a snapshot <c>List&lt;T&gt;</c>
///   before the first loop.</item>
/// </list>
/// </summary>
[TestFixture]
[Order(70)]
public sealed class Publish_Tests : InstalledFixtureBase
{
	const string TestDir      = @"C:\E2E-pub";
	const string ParentPath   = @"C:\E2E-pub\parent.vhdx";
	const string ChildPath    = @"C:\E2E-pub\child.vhdx";
	const string ChildMount   = @"C:\E2E-pub\child_mount";
	const string OverlayPath  = @"C:\E2E-pub\overlay.vhdx";
	const string OverlayMount = @"C:\E2E-pub\overlay_mount";
	const string MarkerFile   = @"C:\E2E-pub\overlay_mount\pub-marker.txt";

	bool _setupSucceeded;
	bool _publishSucceeded;

	protected override async Task OnGuestReadyAsync()
	{
		await Guest.InvokeVoidAsync($"""
			Remove-Item -LiteralPath '{TestDir}' -Recurse -Force -ErrorAction SilentlyContinue
			New-Item -ItemType Directory -Path '{TestDir}'      -Force | Out-Null
			New-Item -ItemType Directory -Path '{ChildMount}'   -Force | Out-Null
			New-Item -ItemType Directory -Path '{OverlayMount}' -Force | Out-Null
			""");

		// Parent VHDX — standalone, not mounted. The differencing model requires
		// the parent file to exist but not be attached when children are created.
		// `--mount ""` triggers the "Detaching (no mount requested)" service path
		// so the file is left on disk in a clean detached state.
		var parent = await Vhdx.RunAsync(Guest,
			$"create --path \"{ParentPath}\" --size 256M --label parent " +
			$"--mount \"\" --filesystem NTFS --add-defender-exclusion false");
		Assert.That(parent.Succeeded, Is.True,
			$"parent-VHDX prereq creation failed (exit {parent.ExitCode}): {parent.StderrText}");

		// Managed child — will be detached + deleted + recreated fresh after publish.
		// Represents a "worker" that always gets a clean slate when the parent updates.
		var child = await Vhdx.RunAsync(Guest,
			$"init --parent \"{ParentPath}\" --child \"{ChildPath}\" " +
			$"--mount \"{ChildMount}\" --add-defender-exclusion false");
		Assert.That(child.Succeeded, Is.True,
			$"child-VHDX prereq creation failed (exit {child.ExitCode}): {child.StderrText}");

		// Overlay child — changes written here will be merged permanently into the
		// parent by publish. It is also a registered child (in the state store), so
		// after publish it too is recreated fresh from the updated parent.
		var overlay = await Vhdx.RunAsync(Guest,
			$"init --parent \"{ParentPath}\" --child \"{OverlayPath}\" " +
			$"--mount \"{OverlayMount}\" --add-defender-exclusion false");
		Assert.That(overlay.Succeeded, Is.True,
			$"overlay-VHDX prereq creation failed (exit {overlay.ExitCode}): {overlay.StderrText}");
	}

	[Test, Order(1)]
	public async Task Overlay_Change_Is_Isolated_Before_Publish()
	{
		// Write a marker through the overlay's mount. At this point the marker
		// lives only in the overlay differencing layer — it hasn't been merged
		// into the parent yet, so the managed child can't see it.
		await Guest.InvokeVoidAsync(
			$@"Set-Content -Path '{MarkerFile}' -Value 'published' -Encoding utf8 -Force");

		var inOverlay = await GuestFs.ExistsAsync(Guest, MarkerFile);
		inOverlay.Should().BeTrue(
			"marker should be immediately readable through the overlay mount");

		var inChild = await GuestFs.ExistsAsync(Guest, $@"{ChildMount}\pub-marker.txt");
		inChild.Should().BeFalse(
			"before publish the marker exists only in the overlay layer, " +
			"not in the parent or the managed child");

		_setupSucceeded = true;
	}

	[Test, Order(2)]
	public async Task Publish_Exits_Zero_And_Reports_Two_Children_Recreated()
	{
		if (!_setupSucceeded) Assert.Inconclusive("marker-write step failed; nothing to publish");

		var r = await Vhdx.RunAsync(Guest, $"publish --overlay \"{OverlayPath}\"");

		r.Succeeded.Should().BeTrue(
			$"`vhdx publish` returned {r.ExitCode}.\nstdout: {r.StdoutText}\nstderr: {r.StderrText}");

		// Two registered children: child.vhdx + overlay.vhdx.
		// Both are recreated as fresh differencing disks from the updated parent.
		r.StdoutText.Should().Contain("Children recreated: 2",
			$"publish must report recreating both registered children. stdout: {r.StdoutText}");

		_publishSucceeded = true;
	}

	[Test, Order(3)]
	public async Task After_Publish_Marker_Visible_Through_All_Children()
	{
		if (!_publishSucceeded) Assert.Inconclusive("publish step failed; nothing to inspect");

		// The marker's journey: overlay layer → merged into parent → inherited by
		// every fresh differencing child. Both mount paths must show the marker.
		var inChild = await GuestFs.ExistsAsync(Guest, $@"{ChildMount}\pub-marker.txt");
		inChild.Should().BeTrue(
			$"after publish the overlay changes are in the parent; the recreated " +
			$"managed child ({ChildMount}) must inherit them");

		var inOverlay = await GuestFs.ExistsAsync(Guest, MarkerFile);
		inOverlay.Should().BeTrue(
			$"the recreated overlay child ({OverlayMount}) also inherits from the " +
			"updated parent, so the marker must be readable through its mount path too");
	}

	[Test, Order(4)]
	public async Task After_Publish_Both_Children_In_List()
	{
		if (!_publishSucceeded) Assert.Inconclusive("publish step failed; nothing to inspect");

		// Publish should remount both children — they must appear in `vhdx list`.
		var r = await Vhdx.RunAsync(Guest, "list");
		r.Succeeded.Should().BeTrue();
		r.StdoutText.Should().Contain("child.vhdx",
			$"managed child must be remounted after publish. stdout: {r.StdoutText}");
		r.StdoutText.Should().Contain("overlay.vhdx",
			$"overlay child must be remounted after publish. stdout: {r.StdoutText}");
	}
}

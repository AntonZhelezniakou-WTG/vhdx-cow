using System.ComponentModel;
using VhdxManager.Service.Reconciliation;
using VhdxManager.Service.State;
using VhdxManager.Service.VhdxOperations;

namespace VhdxManager.Service.Tests;

[TestFixture]
public class MountReconcilerTests
{
	IStateStore stateStore = null!;
	IVirtDiskManager virtDisk = null!;
	IVolumeManager volume = null!;
	string tempDir = null!;

	[SetUp]
	public void SetUp()
	{
		stateStore = Substitute.For<IStateStore>();
		virtDisk = Substitute.For<IVirtDiskManager>();
		volume = Substitute.For<IVolumeManager>();
		tempDir = Path.Combine(Path.GetTempPath(), $"vhdx-reconciler-tests-{Guid.NewGuid()}");
		Directory.CreateDirectory(tempDir);
	}

	[TearDown]
	public void TearDown()
	{
		if (Directory.Exists(tempDir))
			Directory.Delete(tempDir, recursive: true);
	}

	MountReconciler CreateReconciler() =>
		new(stateStore, virtDisk, volume, NullLogger<MountReconciler>.Instance);

	MountedDiskState MakeState(string childFileName, string? mountPath = null)
	{
		var childPath = Path.Combine(tempDir, childFileName);
		File.WriteAllText(childPath, "fake-vhdx");

		var mp = mountPath ?? Path.Combine(tempDir, "mount-" + Path.GetFileNameWithoutExtension(childFileName));

		return new MountedDiskState
		{
			ChildVhdxPath = childPath,
			ParentVhdxPath = Path.Combine(tempDir, "parent.vhdx"),
			MountPath = mp,
			VolumeGuidPath = @"\\?\Volume{aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee}\",
		};
	}

	[Test]
	public async Task StartAsync_EmptyState_DoesNothing()
	{
		stateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(Array.Empty<MountedDiskState>());

		await CreateReconciler().StartAsync(CancellationToken.None);

		await virtDisk.DidNotReceiveWithAnyArgs().AttachAsync(default!, default);
		await volume.DidNotReceiveWithAnyArgs().MountToFolderAsync(default!, default!, default);
	}

	[Test]
	public async Task StartAsync_MissingChildFile_RemovesStaleEntry_NoAttach()
	{
		var staleChild = Path.Combine(tempDir, "deleted.vhdx");
		var state = new MountedDiskState
		{
			ChildVhdxPath = staleChild,
			ParentVhdxPath = Path.Combine(tempDir, "parent.vhdx"),
			MountPath = Path.Combine(tempDir, "mnt"),
			VolumeGuidPath = @"\\?\Volume{x}\",
		};
		stateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });

		await CreateReconciler().StartAsync(CancellationToken.None);

		await stateStore.Received(1).RemoveAsync(staleChild, Arg.Any<CancellationToken>());
		await virtDisk.DidNotReceiveWithAnyArgs().AttachAsync(default!, default);
		await volume.DidNotReceiveWithAnyArgs().MountToFolderAsync(default!, default!, default);
	}

	[Test]
	public async Task StartAsync_NotAttached_AttachesAndMounts()
	{
		var state = MakeState("child.vhdx");
		stateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });
		virtDisk.GetInfoAsync(state.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(new VhdxInfo(IsAttached: false, ParentPath: null, VirtualSize: 0, PhysicalSize: 0));
		virtDisk.AttachAsync(state.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(@"\\.\PhysicalDrive7");
		volume.GetVolumeGuidPathAsync(@"\\.\PhysicalDrive7", Arg.Any<CancellationToken>())
			.Returns(state.VolumeGuidPath);

		await CreateReconciler().StartAsync(CancellationToken.None);

		await virtDisk.Received(1).AttachAsync(state.ChildVhdxPath, Arg.Any<CancellationToken>());
		await volume.Received(1).MountToFolderAsync(state.VolumeGuidPath, state.MountPath, Arg.Any<CancellationToken>());
		Directory.Exists(state.MountPath).Should().BeTrue("reconciler must create the mount-point folder");
		// Volume GUID unchanged → no upsert
		await stateStore.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
	}

	[Test]
	public async Task StartAsync_AlreadyAttached_SkipsAttach_StillRemounts()
	{
		var state = MakeState("child.vhdx");
		stateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });
		virtDisk.GetInfoAsync(state.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(new VhdxInfo(
				IsAttached: true,
				ParentPath: null,
				VirtualSize: 0,
				PhysicalSize: 0,
				PhysicalPath: @"\\.\PhysicalDrive3"));
		volume.GetVolumeGuidPathAsync(@"\\.\PhysicalDrive3", Arg.Any<CancellationToken>())
			.Returns(state.VolumeGuidPath);

		await CreateReconciler().StartAsync(CancellationToken.None);

		await virtDisk.DidNotReceiveWithAnyArgs().AttachAsync(default!, default);
		await volume.Received(1).MountToFolderAsync(state.VolumeGuidPath, state.MountPath, Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task StartAsync_VolumeGuidChanged_PersistsNewGuid()
	{
		var state = MakeState("child.vhdx");
		var newGuid = @"\\?\Volume{99999999-9999-9999-9999-999999999999}\";
		stateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });
		virtDisk.GetInfoAsync(state.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(new VhdxInfo(IsAttached: false, ParentPath: null, VirtualSize: 0, PhysicalSize: 0));
		virtDisk.AttachAsync(state.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(@"\\.\PhysicalDrive7");
		volume.GetVolumeGuidPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(newGuid);

		await CreateReconciler().StartAsync(CancellationToken.None);

		await stateStore.Received(1).AddAsync(
			Arg.Is<MountedDiskState>(s => s.ChildVhdxPath == state.ChildVhdxPath && s.VolumeGuidPath == newGuid),
			Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task StartAsync_StaleReparsePoint_SwallowsWin32_StillMounts()
	{
		var state = MakeState("child.vhdx");
		stateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { state });
		virtDisk.GetInfoAsync(state.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(new VhdxInfo(IsAttached: false, ParentPath: null, VirtualSize: 0, PhysicalSize: 0));
		virtDisk.AttachAsync(state.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(@"\\.\PhysicalDrive7");
		volume.GetVolumeGuidPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(state.VolumeGuidPath);
		volume.UnmountFolderAsync(state.MountPath, Arg.Any<CancellationToken>())
			.Returns(Task.FromException(new Win32Exception(4390 /* ERROR_NOT_A_REPARSE_POINT */)));

		await CreateReconciler().StartAsync(CancellationToken.None);

		await volume.Received(1).MountToFolderAsync(state.VolumeGuidPath, state.MountPath, Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task StartAsync_OneEntryFails_OthersStillProcessed()
	{
		var bad = MakeState("bad.vhdx");
		var good = MakeState("good.vhdx");
		stateStore.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new[] { bad, good });

		virtDisk.GetInfoAsync(bad.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns<VhdxInfo>(_ => throw new InvalidOperationException("kaboom"));
		virtDisk.GetInfoAsync(good.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(new VhdxInfo(IsAttached: false, ParentPath: null, VirtualSize: 0, PhysicalSize: 0));
		virtDisk.AttachAsync(good.ChildVhdxPath, Arg.Any<CancellationToken>())
			.Returns(@"\\.\PhysicalDrive9");
		volume.GetVolumeGuidPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(good.VolumeGuidPath);

		await CreateReconciler().StartAsync(CancellationToken.None);

		await volume.Received(1).MountToFolderAsync(good.VolumeGuidPath, good.MountPath, Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task StartAsync_StateStoreThrows_DoesNotPropagate()
	{
		stateStore.GetAllAsync(Arg.Any<CancellationToken>())
			.Returns<IReadOnlyList<MountedDiskState>>(_ => throw new IOException("disk full"));

		Func<Task> act = () => CreateReconciler().StartAsync(CancellationToken.None);

		await act.Should().NotThrowAsync();
	}
}

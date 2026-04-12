using Grpc.Core;
using VhdxCow.Contracts;
using VhdxCow.Service.Security;
using VhdxCow.Service.Services;
using VhdxCow.Service.State;
using VhdxCow.Service.VhdxOperations;

namespace VhdxCow.Service.Tests;

[TestFixture]
public class VhdxGrpcServiceTests
{
	IVhdxManager vhdxManager = null!;
	IVolumeManager volumeManager = null!;
	IStateStore stateStore = null!;
	PathValidator pathValidator = null!;
	VhdxGrpcService sut = null!;
	ServerCallContext callContext = null!;

	const string AllowedParent = @"C:\Parents";
	const string AllowedChild = @"C:\Children";
	const string AllowedMount = @"C:\Mounts";

	[SetUp]
	public void SetUp()
	{
		vhdxManager = Substitute.For<IVhdxManager>();
		volumeManager = Substitute.For<IVolumeManager>();
		stateStore = Substitute.For<IStateStore>();

		var config = new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["VhdxCow:AllowedParentPaths:0"] = AllowedParent,
				["VhdxCow:AllowedMountBasePaths:0"] = AllowedMount,
				["VhdxCow:AllowedChildBasePaths:0"] = AllowedChild,
			})
			.Build();

		pathValidator = new PathValidator(config, NullLogger<PathValidator>.Instance);
		sut = new VhdxGrpcService(
			vhdxManager, volumeManager, stateStore, pathValidator,
			NullLogger<VhdxGrpcService>.Instance);

		callContext = Substitute.For<ServerCallContext>();
		callContext.CancellationToken.Returns(CancellationToken.None);
	}

	// ─── Ping ───────────────────────────────────────────────────────────────

	[Test]
	public async Task Ping_ReturnsMountCount()
	{
		stateStore.GetActiveMountCount().Returns(5);

		var reply = await sut.Ping(new PingRequest(), callContext);

		reply.ActiveMounts.Should().Be(5);
	}

	[Test]
	public async Task Ping_ReturnsNonEmptyVersion()
	{
		var reply = await sut.Ping(new PingRequest(), callContext);

		reply.Version.Should().NotBeNullOrEmpty();
	}

	// ─── CreateChild ────────────────────────────────────────────────────────

	[Test]
	public async Task CreateChild_ParentPathNotAllowed_ReturnsFailure()
	{
		var reply = await sut.CreateChild(new CreateChildRequest
		{
			ParentVhdxPath = @"C:\Forbidden\parent.vhdx",
			ChildVhdxPath = $@"{AllowedChild}\child.vhdx",
			MountPath = $@"{AllowedMount}\wt1",
		}, callContext);

		reply.Success.Should().BeFalse();
		reply.ErrorMessage.Should().Contain("parent VHDX");
		await vhdxManager.DidNotReceive().CreateDifferencingDiskAsync(
			Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task CreateChild_ChildPathNotAllowed_ReturnsFailure()
	{
		var reply = await sut.CreateChild(new CreateChildRequest
		{
			ParentVhdxPath = $@"{AllowedParent}\parent.vhdx",
			ChildVhdxPath = @"C:\Forbidden\child.vhdx",
			MountPath = $@"{AllowedMount}\wt1",
		}, callContext);

		reply.Success.Should().BeFalse();
		reply.ErrorMessage.Should().Contain("child VHDX");
	}

	[Test]
	public async Task CreateChild_MountPathNotAllowed_ReturnsFailure()
	{
		var reply = await sut.CreateChild(new CreateChildRequest
		{
			ParentVhdxPath = $@"{AllowedParent}\parent.vhdx",
			ChildVhdxPath = $@"{AllowedChild}\child.vhdx",
			MountPath = @"C:\Forbidden\wt1",
		}, callContext);

		reply.Success.Should().BeFalse();
		reply.ErrorMessage.Should().Contain("mount");
	}

	[Test]
	public async Task CreateChild_ParentFileNotFound_ReturnsFailure()
	{
		// Paths are valid but the parent .vhdx file doesn't exist on disk
		var reply = await sut.CreateChild(new CreateChildRequest
		{
			ParentVhdxPath = $@"{AllowedParent}\does-not-exist.vhdx",
			ChildVhdxPath = $@"{AllowedChild}\child.vhdx",
			MountPath = $@"{AllowedMount}\wt1",
		}, callContext);

		reply.Success.Should().BeFalse();
		reply.ErrorMessage.Should().Contain("not found");
	}

	[Test]
	public async Task CreateChild_VhdxManagerThrows_ReturnsFailure()
	{
		var parentFile = Path.GetTempFileName();
		try
		{
			var config = new ConfigurationBuilder()
				.AddInMemoryCollection(new Dictionary<string, string?>
				{
					["VhdxCow:AllowedParentPaths:0"] = Path.GetTempPath(),
					["VhdxCow:AllowedMountBasePaths:0"] = Path.GetTempPath(),
					["VhdxCow:AllowedChildBasePaths:0"] = Path.GetTempPath(),
				})
				.Build();
			var service = new VhdxGrpcService(
				vhdxManager, volumeManager, stateStore,
				new PathValidator(config, NullLogger<PathValidator>.Instance),
				NullLogger<VhdxGrpcService>.Instance);

			vhdxManager
				.CreateDifferencingDiskAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
				.Returns(Task.FromException(new InvalidOperationException("disk creation failed")));

			var reply = await service.CreateChild(new CreateChildRequest
			{
				ParentVhdxPath = parentFile,
				ChildVhdxPath = Path.Combine(Path.GetTempPath(), "child.vhdx"),
				MountPath = Path.Combine(Path.GetTempPath(), "mount"),
			}, callContext);

			reply.Success.Should().BeFalse();
			reply.ErrorMessage.Should().Contain("disk creation failed");
		}
		finally
		{
			File.Delete(parentFile);
		}
	}

	// ─── ResetChild ─────────────────────────────────────────────────────────

	[Test]
	public async Task ResetChild_NoTrackedState_ReturnsFailure()
	{
		stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns((MountedDiskState?)null);

		var reply = await sut.ResetChild(
			new ResetChildRequest { ChildVhdxPath = @"C:\child.vhdx" }, callContext);

		reply.Success.Should().BeFalse();
		reply.ErrorMessage.Should().Contain("No tracked mount");
	}

	[Test]
	public async Task ResetChild_UnmountFails_ReturnsFailure()
	{
		stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(SomeState(@"C:\child.vhdx"));

		volumeManager
			.UnmountFolderAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(Task.FromException(new InvalidOperationException("unmount failed")));

		var reply = await sut.ResetChild(
			new ResetChildRequest { ChildVhdxPath = @"C:\child.vhdx" }, callContext);

		reply.Success.Should().BeFalse();
		reply.ErrorMessage.Should().Contain("unmount failed");
	}

	[Test]
	public async Task ResetChild_Success_ReattachesAndUpdatesState()
	{
		var state = SomeState(@"C:\child.vhdx");
		stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(state);
		vhdxManager.AttachAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(@"\\.\PhysicalDrive5");
		volumeManager.GetVolumeGuidPathAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(@"\\?\Volume{new-guid}\");

		var reply = await sut.ResetChild(
			new ResetChildRequest { ChildVhdxPath = @"C:\child.vhdx" }, callContext);

		reply.Success.Should().BeTrue();
		await stateStore.Received(1).AddAsync(
			Arg.Is<MountedDiskState>(s => s.VolumeGuidPath == @"\\?\Volume{new-guid}\"),
			Arg.Any<CancellationToken>());
	}

	// ─── Detach ─────────────────────────────────────────────────────────────

	[Test]
	public async Task Detach_DetachFails_ReturnsFailure()
	{
		stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns((MountedDiskState?)null);

		vhdxManager.DetachAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(Task.FromException(new InvalidOperationException("detach failed")));

		var reply = await sut.Detach(
			new DetachRequest { ChildVhdxPath = @"C:\child.vhdx" }, callContext);

		reply.Success.Should().BeFalse();
		reply.ErrorMessage.Should().Contain("detach failed");
	}

	[Test]
	public async Task Detach_WithState_UnmountsBeforeDetach()
	{
		stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(SomeState(@"C:\child.vhdx"));

		var reply = await sut.Detach(
			new DetachRequest { ChildVhdxPath = @"C:\child.vhdx" }, callContext);

		reply.Success.Should().BeTrue();
		await volumeManager.Received(1).UnmountFolderAsync(
			Arg.Any<string>(), Arg.Any<CancellationToken>());
		await vhdxManager.Received(1).DetachAsync(
			Arg.Any<string>(), Arg.Any<CancellationToken>());
		await stateStore.Received(1).RemoveAsync(
			Arg.Any<string>(), Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task Detach_WithoutState_SkipsUnmount()
	{
		stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns((MountedDiskState?)null);

		await sut.Detach(new DetachRequest { ChildVhdxPath = @"C:\child.vhdx" }, callContext);

		await volumeManager.DidNotReceive().UnmountFolderAsync(
			Arg.Any<string>(), Arg.Any<CancellationToken>());
	}

	// ─── GetStatus ──────────────────────────────────────────────────────────

	[Test]
	public async Task GetStatus_NoTrackedState_ReturnsNotAttached()
	{
		stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns((MountedDiskState?)null);

		var reply = await sut.GetStatus(
			new GetStatusRequest { ChildVhdxPath = @"C:\child.vhdx" }, callContext);

		reply.IsAttached.Should().BeFalse();
	}

	[Test]
	public async Task GetStatus_HasState_ReturnsStoredPaths()
	{
		var state = SomeState(@"C:\child.vhdx");
		stateStore.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(state);
		vhdxManager.GetInfoAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(new VhdxInfo(IsAttached: true, ParentPath: null, VirtualSize: 0, PhysicalSize: 2048));

		var reply = await sut.GetStatus(
			new GetStatusRequest { ChildVhdxPath = @"C:\child.vhdx" }, callContext);

		reply.IsAttached.Should().BeTrue();
		reply.MountPath.Should().Be(state.MountPath);
		reply.ParentVhdxPath.Should().Be(state.ParentVhdxPath);
		reply.VolumeGuidPath.Should().Be(state.VolumeGuidPath);
		reply.ChildSizeBytes.Should().Be(2048);
	}

	// ─── Helpers ────────────────────────────────────────────────────────────

	static MountedDiskState SomeState(string childPath) => new()
	{
		ChildVhdxPath = childPath,
		ParentVhdxPath = @"C:\parent.vhdx",
		MountPath = @"C:\mount\wt1",
		VolumeGuidPath = @"\\?\Volume{abcd}\",
	};
}

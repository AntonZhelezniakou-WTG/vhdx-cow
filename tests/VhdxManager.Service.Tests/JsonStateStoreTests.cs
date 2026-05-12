using VhdxManager.Service.State;

namespace VhdxManager.Service.Tests;

[TestFixture]
public class JsonStateStoreTests
{
	string tempFile = null!;

	[SetUp]
	public void SetUp()
	{
		tempFile = Path.Combine(Path.GetTempPath(), $"vhdx-cow-test-{Guid.NewGuid()}.json");
	}

	[TearDown]
	public void TearDown()
	{
		if (File.Exists(tempFile))
			File.Delete(tempFile);
	}

	JsonStateStore CreateStore() =>
		new(new ConfigurationBuilder()
			.AddInMemoryCollection(new Dictionary<string, string?>
			{
				["VhdxManager:StatePath"] = tempFile,
			})
			.Build(),
		NullLogger<JsonStateStore>.Instance);

	static MountedDiskState MakeState(string child, string parent = @"C:\parent.vhdx")
		=> new()
		{
			ChildVhdxPath = child,
			ParentVhdxPath = parent,
			MountPath = @"C:\mount",
			VolumeGuidPath = @"\\?\Volume{1234-5678}\",
		};

	[Test]
	public void GetActiveMountCount_InitiallyZero()
	{
		var store = CreateStore();

		store.GetActiveMountCount().Should().Be(0);
	}

	[Test]
	public async Task AddAsync_IncreasesCount()
	{
		var store = CreateStore();

		await store.AddAsync(MakeState(@"C:\child.vhdx"), CancellationToken.None);

		store.GetActiveMountCount().Should().Be(1);
	}

	[Test]
	public async Task AddAsync_SameChildPath_Upserts()
	{
		var store = CreateStore();
		await store.AddAsync(MakeState(@"C:\child.vhdx", @"C:\parent-v1.vhdx"), CancellationToken.None);
		await store.AddAsync(MakeState(@"C:\child.vhdx", @"C:\parent-v2.vhdx"), CancellationToken.None);

		store.GetActiveMountCount().Should().Be(1);
		var state = await store.GetAsync(@"C:\child.vhdx", CancellationToken.None);
		state!.ParentVhdxPath.Should().Be(@"C:\parent-v2.vhdx");
	}

	[Test]
	public async Task GetAsync_ExistingEntry_ReturnsState()
	{
		var store = CreateStore();
		await store.AddAsync(MakeState(@"C:\child.vhdx"), CancellationToken.None);

		var state = await store.GetAsync(@"C:\child.vhdx", CancellationToken.None);

		state.Should().NotBeNull();
		state.ChildVhdxPath.Should().Be(@"C:\child.vhdx");
	}

	[Test]
	public async Task GetAsync_MissingEntry_ReturnsNull()
	{
		var store = CreateStore();

		var state = await store.GetAsync(@"C:\nonexistent.vhdx", CancellationToken.None);

		state.Should().BeNull();
	}

	[Test]
	public async Task GetAsync_IsCaseInsensitive()
	{
		var store = CreateStore();
		await store.AddAsync(MakeState(@"C:\Child.vhdx"), CancellationToken.None);

		var state = await store.GetAsync(@"C:\CHILD.VHDX", CancellationToken.None);

		state.Should().NotBeNull();
	}

	[Test]
	public async Task RemoveAsync_ExistingEntry_DecreasesCount()
	{
		var store = CreateStore();
		await store.AddAsync(MakeState(@"C:\child.vhdx"), CancellationToken.None);

		await store.RemoveAsync(@"C:\child.vhdx", CancellationToken.None);

		store.GetActiveMountCount().Should().Be(0);
		(await store.GetAsync(@"C:\child.vhdx", CancellationToken.None)).Should().BeNull();
	}

	[Test]
	public async Task RemoveAsync_MissingEntry_DoesNotThrow()
	{
		var store = CreateStore();

		await store.Invoking(s => s.RemoveAsync(@"C:\nonexistent.vhdx", CancellationToken.None))
			.Should().NotThrowAsync();
	}

	[Test]
	public async Task GetAllAsync_ReturnsAllEntries()
	{
		var store = CreateStore();
		await store.AddAsync(MakeState(@"C:\child1.vhdx"), CancellationToken.None);
		await store.AddAsync(MakeState(@"C:\child2.vhdx"), CancellationToken.None);

		var all = await store.GetAllAsync(CancellationToken.None);

		all.Should().HaveCount(2);
		all.Select(s => s.ChildVhdxPath).Should().BeEquivalentTo(@"C:\child1.vhdx", @"C:\child2.vhdx");
	}

	[Test]
	public async Task AddAsync_PersistsToDisk()
	{
		var store = CreateStore();
		await store.AddAsync(MakeState(@"C:\child.vhdx"), CancellationToken.None);

		// small delay to let async fire-and-forget Save complete
		await Task.Delay(50);

		File.Exists(tempFile).Should().BeTrue();
		var json = await File.ReadAllTextAsync(tempFile);
		json.Should().Contain("child.vhdx");
	}

	[Test]
	public async Task NewStore_LoadsExistingStateFile()
	{
		// Persist via first store instance
		var store1 = CreateStore();
		await store1.AddAsync(MakeState(@"C:\child.vhdx"), CancellationToken.None);
		await Task.Delay(50);

		// Second instance reads the same file
		var store2 = CreateStore();
		await Task.Delay(50); // let async Load complete

		var state = await store2.GetAsync(@"C:\child.vhdx", CancellationToken.None);
		state.Should().NotBeNull();
		state!.ChildVhdxPath.Should().Be(@"C:\child.vhdx");
	}
}

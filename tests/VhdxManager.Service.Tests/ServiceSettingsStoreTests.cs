using VhdxManager.Service.Configuration;

namespace VhdxManager.Service.Tests;

[TestFixture]
public class ServiceSettingsStoreTests
{
	string tempDir = null!;
	string settingsPath = null!;
	object? previousBaseDirOverride;

	[SetUp]
	public void SetUp()
	{
		tempDir = Path.Combine(Path.GetTempPath(), "vhdxmgr-settings-tests-" + Guid.NewGuid().ToString("N"));
		Directory.CreateDirectory(tempDir);
		settingsPath = Path.Combine(tempDir, "appsettings.json");

		// ServiceSettingsStore reads/writes "appsettings.json" alongside the
		// process. AppContext.BaseDirectory honours the APP_CONTEXT_BASE_DIRECTORY
		// data slot, so we redirect it at our temp dir for the test, then restore
		// whatever was there before (typically null).
		previousBaseDirOverride = AppDomain.CurrentDomain.GetData("APP_CONTEXT_BASE_DIRECTORY");
		AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", tempDir);
	}

	[TearDown]
	public void TearDown()
	{
		AppDomain.CurrentDomain.SetData("APP_CONTEXT_BASE_DIRECTORY", previousBaseDirOverride);
		try { Directory.Delete(tempDir, recursive: true); } catch { /* best-effort */ }
	}

	ServiceSettingsStore CreateStore() =>
		new(NullLogger<ServiceSettingsStore>.Instance);

	[Test]
	public void GetDefaultAddDefenderExclusion_FileMissing_ReturnsNull()
	{
		File.Exists(settingsPath).Should().BeFalse();

		CreateStore().GetDefaultAddDefenderExclusion().Should().BeNull();
	}

	[Test]
	public void GetDefaultAddDefenderExclusion_KeyMissing_ReturnsNull()
	{
		File.WriteAllText(settingsPath, "{ \"VhdxManager\": { \"PipeName\": \"x\" } }");

		CreateStore().GetDefaultAddDefenderExclusion().Should().BeNull();
	}

	[Test]
	public void GetDefaultAddDefenderExclusion_KeyNull_ReturnsNull()
	{
		File.WriteAllText(settingsPath,
			"{ \"VhdxManager\": { \"Defaults\": { \"AddDefenderExclusion\": null } } }");

		CreateStore().GetDefaultAddDefenderExclusion().Should().BeNull();
	}

	[Test]
	public void GetDefaultAddDefenderExclusion_KeyTrue_ReturnsTrue()
	{
		File.WriteAllText(settingsPath,
			"{ \"VhdxManager\": { \"Defaults\": { \"AddDefenderExclusion\": true } } }");

		CreateStore().GetDefaultAddDefenderExclusion().Should().BeTrue();
	}

	[Test]
	public void GetDefaultAddDefenderExclusion_KeyFalse_ReturnsFalse()
	{
		File.WriteAllText(settingsPath,
			"{ \"VhdxManager\": { \"Defaults\": { \"AddDefenderExclusion\": false } } }");

		CreateStore().GetDefaultAddDefenderExclusion().Should().BeFalse();
	}

	[Test]
	public void GetDefaultAddDefenderExclusion_CorruptJson_ReturnsNull()
	{
		File.WriteAllText(settingsPath, "{ this is not valid json");

		CreateStore().GetDefaultAddDefenderExclusion().Should().BeNull();
	}

	[Test]
	public async Task SetDefaultAddDefenderExclusion_FileMissing_CreatesFileWithValue()
	{
		await CreateStore().SetDefaultAddDefenderExclusionAsync(true, CancellationToken.None);

		File.Exists(settingsPath).Should().BeTrue();
		CreateStore().GetDefaultAddDefenderExclusion().Should().BeTrue();
	}

	[Test]
	public async Task SetDefaultAddDefenderExclusion_PreservesOtherKeys()
	{
		const string original =
			"{ \"VhdxManager\": { \"PipeName\": \"my-pipe\", \"Defaults\": { \"AddDefenderExclusion\": null } }, \"Serilog\": { \"MinimumLevel\": \"Information\" } }";
		File.WriteAllText(settingsPath, original);

		await CreateStore().SetDefaultAddDefenderExclusionAsync(true, CancellationToken.None);

		var written = File.ReadAllText(settingsPath);
		written.Should().Contain("\"PipeName\"").And.Contain("\"my-pipe\"");
		written.Should().Contain("\"MinimumLevel\"");
		CreateStore().GetDefaultAddDefenderExclusion().Should().BeTrue();
	}

	[Test]
	public async Task SetDefaultAddDefenderExclusion_NullValue_ClearsOverride()
	{
		File.WriteAllText(settingsPath,
			"{ \"VhdxManager\": { \"Defaults\": { \"AddDefenderExclusion\": true } } }");

		await CreateStore().SetDefaultAddDefenderExclusionAsync(null, CancellationToken.None);

		CreateStore().GetDefaultAddDefenderExclusion().Should().BeNull();
	}

	[Test]
	public async Task SetDefaultAddDefenderExclusion_RoundtripFalse()
	{
		await CreateStore().SetDefaultAddDefenderExclusionAsync(false, CancellationToken.None);
		CreateStore().GetDefaultAddDefenderExclusion().Should().BeFalse();
	}
}

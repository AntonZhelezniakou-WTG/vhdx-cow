using VhdxManager.Client;
using VhdxManager.Contracts;

namespace VhdxManager.Cli.Tests;

[TestFixture]
public class DefenderExclusionResolverTests
{
	IVhdxManagerClient client = null!;

	[SetUp]
	public void SetUp() => client = Substitute.For<IVhdxManagerClient>();

	[Test]
	public async Task CliValueTrue_Wins_NoSettingsCall()
	{
		var result = await DefenderExclusionResolver.ResolveAsync(
			cliValue: true, client, CancellationToken.None);

		result.Should().BeTrue();
		await client.DidNotReceive().GetSettingsAsync(Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task CliValueFalse_Wins_NoSettingsCall()
	{
		var result = await DefenderExclusionResolver.ResolveAsync(
			cliValue: false, client, CancellationToken.None);

		result.Should().BeFalse();
		await client.DidNotReceive().GetSettingsAsync(Arg.Any<CancellationToken>());
	}

	[Test]
	public async Task CliUnset_ServiceDefaultTrue_Wins()
	{
		client.GetSettingsAsync(Arg.Any<CancellationToken>())
			.Returns(new GetSettingsReply
			{
				HasDefaultAddDefenderExclusion = true,
				DefaultAddDefenderExclusion = true,
			});

		var result = await DefenderExclusionResolver.ResolveAsync(
			cliValue: null, client, CancellationToken.None);

		result.Should().BeTrue();
	}

	[Test]
	public async Task CliUnset_ServiceDefaultFalse_Wins()
	{
		client.GetSettingsAsync(Arg.Any<CancellationToken>())
			.Returns(new GetSettingsReply
			{
				HasDefaultAddDefenderExclusion = true,
				DefaultAddDefenderExclusion = false,
			});

		var result = await DefenderExclusionResolver.ResolveAsync(
			cliValue: null, client, CancellationToken.None);

		result.Should().BeFalse();
	}

	[Test]
	public async Task CliUnset_ServiceUnset_ReturnsFalse()
	{
		client.GetSettingsAsync(Arg.Any<CancellationToken>())
			.Returns(new GetSettingsReply { HasDefaultAddDefenderExclusion = false });

		var result = await DefenderExclusionResolver.ResolveAsync(
			cliValue: null, client, CancellationToken.None);

		result.Should().BeFalse();
	}
}

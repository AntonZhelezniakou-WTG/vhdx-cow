using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Sanity tests for <see cref="PowerShellRunner"/>. Not tagged
/// <c>[Category("E2E")]</c> because these don't touch the VM — they
/// just verify the C# ↔ powershell.exe bridge itself. Keep them runnable
/// on any developer box (and in any CI) without the Hyper-V rig.
/// </summary>
[TestFixture]
[Category("E2E-Smoke")]
public sealed class PowerShellRunnerSmokeTests
{
	private PowerShellRunner _runner = null!;

	[OneTimeSetUp]
	public void SetUp()
	{
		_runner = new PowerShellRunner();
	}

	[Test]
	public async Task RunRaw_Echoes_String()
	{
		var output = await _runner.RunRawAsync("'hello-from-ps'");
		output.Should().Contain("hello-from-ps");
	}

	[Test]
	public async Task RunJson_RoundTrips_PSObject()
	{
		var obj = await _runner.RunJsonAsync<Record>(
			"[pscustomobject]@{ Name = 'vhdxtest'; Count = 42 }");
		obj.Name.Should().Be("vhdxtest");
		obj.Count.Should().Be(42);
	}

	[Test]
	public async Task RunJson_RoundTrips_Array()
	{
		var arr = await _runner.RunJsonAsync<int[]>("@(1, 2, 3)");
		arr.Should().Equal(1, 2, 3);
	}

	[Test]
	public async Task RunVoid_Surfaces_Errors_With_Script_Context()
	{
		var act = async () => await _runner.RunVoidAsync("throw 'kaboom'");
		var assertion = await act.Should().ThrowAsync<PowerShellInvocationException>();
		assertion.Which.Message.Should().Contain("kaboom");
	}

	private sealed record Record(string Name, int Count);
}

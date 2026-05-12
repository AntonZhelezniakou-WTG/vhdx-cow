using FluentAssertions;
using NUnit.Framework;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// End-to-end smoke test that actually touches the Hyper-V VM. Uses
/// <c>pre-install-clean</c> (always present after Bootstrap-VM.ps1) so
/// it never depends on the MSI having been built. If the VM/Hyper-V/
/// credentials are missing the fixture skips with a clear remediation
/// message courtesy of <see cref="E2EConfig.LoadOrSkip"/>.
/// </summary>
[TestFixture]
public sealed class GuestSessionSmokeTests : E2EFixtureBase
{
	protected override string CheckpointName => "pre-install-clean";

	[Test]
	public async Task Guest_Hostname_Matches_Expected()
	{
		// The autounattend.xml.template hard-codes ComputerName=VHMGRTEST.
		// If this assertion ever drifts it means someone changed the template
		// without re-running the bootstrap script.
		var hostname = await Guest.InvokeJsonAsync<string>("hostname");
		hostname.Should().BeEquivalentTo("VHMGRTEST");
	}

	[Test]
	public async Task Guest_Has_Vhdxtest_User_As_Admin()
	{
		// Sanity check: confirms the credential we just used really maps to
		// a local admin in the guest. If this fails, FirstLogon.ps1 didn't
		// apply or the bootstrap created a non-admin account.
		var isAdmin = await Guest.InvokeJsonAsync<bool>("""

			$id = [Security.Principal.WindowsIdentity]::GetCurrent()
			$principal = New-Object Security.Principal.WindowsPrincipal($id)
			$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)

			""");
		isAdmin.Should().BeTrue();
	}
}

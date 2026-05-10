using VhdxManager.Service.VhdxOperations;

namespace VhdxManager.Service.Tests;

[TestFixture]
public class DefenderExclusionManagerTests
{
	// IsPolicyBlocked is the only piece of DefenderExclusionManager we can unit-test
	// without actually shelling to PowerShell + touching the live Defender service.
	// The integration test rig covers the happy path on real Windows.

	[Test]
	public void IsPolicyBlocked_HResult0x800704EC_True()
	{
		DefenderExclusionManager.IsPolicyBlocked(
			"Add-MpPreference: HRESULT 0x800704EC: This program is blocked by group policy.")
			.Should().BeTrue();
	}

	[Test]
	public void IsPolicyBlocked_TamperProtection_True()
	{
		DefenderExclusionManager.IsPolicyBlocked(
			"Operation failed with the following error: 0x800106ba (tamper protection enabled).")
			.Should().BeTrue();
	}

	[Test]
	public void IsPolicyBlocked_AccessDeniedPhrase_True()
	{
		DefenderExclusionManager.IsPolicyBlocked(
			"Add-MpPreference: Access is denied.")
			.Should().BeTrue();
	}

	[Test]
	public void IsPolicyBlocked_BlockedByGroupPolicyPhrase_True()
	{
		DefenderExclusionManager.IsPolicyBlocked(
			"This program is blocked by group policy. Contact your sysadmin.")
			.Should().BeTrue();
	}

	[Test]
	public void IsPolicyBlocked_RandomPowershellError_False()
	{
		DefenderExclusionManager.IsPolicyBlocked(
			"Add-MpPreference: Cannot bind argument to parameter 'ExclusionPath'.")
			.Should().BeFalse();
	}

	[Test]
	public void IsPolicyBlocked_EmptyString_False()
	{
		DefenderExclusionManager.IsPolicyBlocked("").Should().BeFalse();
	}

	[Test]
	public void IsPolicyBlocked_HrCaseInsensitive_True()
	{
		DefenderExclusionManager.IsPolicyBlocked("error: 0x800704ec").Should().BeTrue();
	}
}

using FluentAssertions;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Wrappers for inspecting Windows services inside the guest. Combines
/// <c>Get-Service</c> (status, name) with a <c>Get-CimInstance Win32_Service</c>
/// projection for the bits Get-Service doesn't expose (StartName/LogOnAs,
/// StartMode in cleaner form, ProcessId).
/// </summary>
public static class GuestService
{
	/// <summary>
	/// Returns null if the service isn't registered in the guest. Callers
	/// can distinguish "service missing" from "service stopped" cleanly.
	/// </summary>
	public static Task<ServiceInfo?> TryGetAsync(GuestSession s, string name, CancellationToken ct = default)
		// Win32_Service is the canonical source: StartName ("LocalSystem" /
		// "NT AUTHORITY\NetworkService" / etc.) and StartMode ("Auto" /
		// "Manual" / "Disabled") aren't on the Get-Service object.
		=> s.InvokeJsonAsync<ServiceInfo?>($$"""
			$svc = Get-CimInstance -ClassName Win32_Service -Filter "Name='{{Esc(name)}}'" -ErrorAction SilentlyContinue
			if ($null -eq $svc) { $null } else {
			    [pscustomobject]@{
			        Name      = $svc.Name
			        State     = $svc.State        # Running, Stopped, ...
			        StartMode = $svc.StartMode    # Auto, Manual, Disabled
			        StartName = $svc.StartName    # LocalSystem, NT AUTHORITY\..., DOMAIN\user
			        ProcessId = [int]$svc.ProcessId
			    }
			}
			""", ct);

	public static async Task AssertRunningAsync(GuestSession s, string name, CancellationToken ct = default)
	{
		var svc = await TryGetAsync(s, name, ct);
		svc.Should().NotBeNull($"service '{name}' must be registered in the guest");
		svc!.State.Should().Be("Running", $"service '{name}' was {svc.State}");
	}

	public static async Task AssertStartModeAsync(GuestSession s, string name, string mode, CancellationToken ct = default)
	{
		var svc = await TryGetAsync(s, name, ct);
		svc.Should().NotBeNull($"service '{name}' must be registered in the guest");
		svc!.StartMode.Should().Be(mode);
	}

	public static async Task AssertStartNameAsync(GuestSession s, string name, string expected, CancellationToken ct = default)
	{
		var svc = await TryGetAsync(s, name, ct);
		svc.Should().NotBeNull($"service '{name}' must be registered in the guest");
		svc!.StartName.Should().Be(expected);
	}

	public static async Task AssertNotRegisteredAsync(GuestSession s, string name, CancellationToken ct = default)
	{
		var svc = await TryGetAsync(s, name, ct);
		svc.Should().BeNull($"service '{name}' should have been removed");
	}

	static string Esc(string s) => s.Replace("'", "''");
}

/// <summary>Subset of Win32_Service we care about in tests.</summary>
public sealed record ServiceInfo(string Name, string State, string StartMode, string StartName, int ProcessId);

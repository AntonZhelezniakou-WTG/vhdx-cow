using System.Threading.Tasks;
using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Cli;

/// <summary>
/// Stateless CLI verbs against a freshly-installed (no mounts) service.
/// One VM boot drives all assertions — boot cost is ~30 s, per-test verb
/// invocation is ~1-3 s.
///
/// <para>The set of verbs covered here are those that don't depend on or
/// produce any VHDX state: <c>ping</c>, <c>list</c> (empty), <c>--version</c>,
/// and <c>--help</c>. Stateful verb workflows live in their own fixtures
/// (<c>StandaloneVhdx_Tests</c>, <c>Differencing_Tests</c>).</para>
/// </summary>
[TestFixture]
[Order(10)]
public sealed class BasicVerbs_Tests : InstalledFixtureBase
{
	[Test, Order(1)]
	public async Task Ping_Returns_Service_Info()
	{
		var r = await Vhmgr.RunAsync(Guest, "ping");

		r.Succeeded.Should().BeTrue(
			$"`vhmgr ping` returned {r.ExitCode}.\nstdout: {r.StdoutText}\nstderr: {r.StderrText}");
		// Contract from PingCommand:
		//   "Service is running. Version: {ver}, Active mounts: {count}"
		// Both fragments are stable post-install. We pin the suffix
		// "Active mounts: 0" rather than the version (which bumps every
		// release).
		r.StdoutText.Should().Contain("Service is running",
			"ping is the canonical service-health smoke test");
		r.StdoutText.Should().Contain("Active mounts: 0",
			"on a fresh install with no mounts the count should be zero");
	}

	[Test, Order(2)]
	public async Task List_Returns_No_Mounts_When_Empty()
	{
		var r = await Vhmgr.RunAsync(Guest, "list");

		r.Succeeded.Should().BeTrue(
			$"`vhmgr list` returned {r.ExitCode}. stderr: {r.StderrText}");
		// Contract from ListCommand: literal "No active mounts." on empty.
		r.StdoutText.Should().Contain("No active mounts");
	}

	[Test, Order(3)]
	public async Task Help_Lists_All_Documented_Verbs()
	{
		// `--help` is the first thing a user sees and the contract our
		// own tests build on top of. If a verb stops showing up here,
		// either it was removed (Phase B fixtures will fail immediately
		// and loudly) or the help text was reorganized (we want to know).
		var r = await Vhmgr.RunAsync(Guest, "--help");

		r.Succeeded.Should().BeTrue();
		foreach (var verb in new[] {
			"ping", "init", "reset", "cleanup", "status", "publish",
			"list", "logs", "create", "mount", "unmount", "delete", "convert" })
		{
			r.StdoutText.Should().Contain(verb,
				$"`vhmgr --help` is expected to document the '{verb}' command");
		}
	}

	[Test, Order(4)]
	public async Task Version_Prints_Something()
	{
		var r = await Vhmgr.RunAsync(Guest, "--version");

		r.Succeeded.Should().BeTrue();
		// We don't pin the exact version string (CI bumps it on every
		// release) — just that the flag produces output and exits cleanly.
		r.StdoutText.Should().NotBeNullOrWhiteSpace();
	}
}

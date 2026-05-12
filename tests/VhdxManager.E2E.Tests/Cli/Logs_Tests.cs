using FluentAssertions;
using NUnit.Framework;
using VhdxManager.E2E.Tests.Infrastructure;

namespace VhdxManager.E2E.Tests.Cli;

/// <summary>
/// <c>vhdx logs</c> reads Windows Event Log entries related to the service
/// and pretty-prints them. It's a read-only, client-side collector — no
/// gRPC round-trip, no state mutation — so a single VM boot can drive
/// every assertion cheaply.
/// </summary>
[TestFixture]
[Order(50)]
public sealed class Logs_Tests : InstalledFixtureBase
{
	[Test, Order(1)]
	public async Task Logs_Since_Install_Returns_Something()
	{
		// `--since install` is the default-but-explicit form. The service
		// has been running since the snapshot was taken, so at minimum we
		// expect a "Service started" / install-time entry in the report.
		var r = await Vhdx.RunAsync(Guest, "logs --since install");

		r.Succeeded.Should().BeTrue(
			$"`vhdx logs --since install` returned {r.ExitCode}. stderr: {r.StderrText}");
		r.StdoutText.Should().NotBeNullOrWhiteSpace(
			"logs should print at least a metadata header even with no events");
	}

	[Test, Order(2)]
	public async Task Logs_Writes_To_File_When_Output_Specified()
	{
		const string logDest = @"C:\E2E\events.txt";
		await Guest.InvokeVoidAsync("""

			New-Item -ItemType Directory -Path 'C:\E2E' -Force | Out-Null
			Remove-Item -LiteralPath 'C:\E2E\events.txt' -Force -ErrorAction SilentlyContinue

			""");

		var r = await Vhdx.RunAsync(Guest, $"logs --since install --output \"{logDest}\"");

		r.Succeeded.Should().BeTrue(
			$"`vhdx logs --output` returned {r.ExitCode}. stderr: {r.StderrText}");
		// LogsCommand prints "Wrote N event(s) to <path>" to stdout when
		// --output is used. Either the file exists or that line is present.
		await GuestFs.AssertFileExistsAsync(Guest, logDest);
	}

	[Test, Order(3)]
	public async Task Logs_Accepts_Duration_Form_Of_Since()
	{
		// The CLI doc-string lists "15m", "2h", "3d" as accepted durations.
		// Just `1h` here — long enough to cover the snapshot's
		// install/boot window which is well under an hour old.
		var r = await Vhdx.RunAsync(Guest, "logs --since 1h");

		r.Succeeded.Should().BeTrue(
			$"`vhdx logs --since 1h` returned {r.ExitCode}. stderr: {r.StderrText}");
	}
}

using System.CommandLine;
using VhdxManager.Client;
using VhdxManager.Contracts;

namespace VhdxManager.Cli.Tests;

[TestFixture]
public class CommandTests
{
	IVhdxManagerClient mockClient = null!;
	string? capturedPipeName;
	TimeSpan? capturedTimeout;

	[SetUp]
	public void SetUp()
	{
		mockClient = Substitute.For<IVhdxManagerClient>();
		capturedPipeName = null;
		capturedTimeout = null;
	}

	RootCommand CreateRoot() => CommandFactory.CreateRootCommand((pipe, timeout) =>
	{
		capturedPipeName = pipe;
		capturedTimeout = timeout;
		return mockClient;
	});

	async Task<int> Invoke(string commandLine)
	{
		var oldOut = Console.Out;
		var oldErr = Console.Error;
		try
		{
			Console.SetOut(new StringWriter());
			Console.SetError(new StringWriter());
			return await CreateRoot().Parse(commandLine).InvokeAsync();
		}
		finally
		{
			Console.SetOut(oldOut);
			Console.SetError(oldErr);
		}
	}

	async Task<(int exitCode, string stdout, string stderr)> InvokeWithOutput(string commandLine)
	{
		var stdoutWriter = new StringWriter();
		var stderrWriter = new StringWriter();
		var oldOut = Console.Out;
		var oldErr = Console.Error;
		try
		{
			Console.SetOut(stdoutWriter);
			Console.SetError(stderrWriter);
			var exitCode = await CreateRoot().Parse(commandLine).InvokeAsync();
			return (exitCode, stdoutWriter.ToString(), stderrWriter.ToString());
		}
		finally
		{
			Console.SetOut(oldOut);
			Console.SetError(oldErr);
		}
	}

	// ─── ping ───────────────────────────────────────────────────────────────

	[Test]
	public async Task Ping_Success_ExitCode0()
	{
		mockClient.PingAsync(Arg.Any<CancellationToken>())
			.Returns(new PingReply { Version = "1.2.3", ActiveMounts = 2 });

		var (exitCode, stdout, _) = await InvokeWithOutput("ping");

		exitCode.Should().Be(0);
		stdout.Should().Contain("1.2.3");
		stdout.Should().Contain("2");
	}

	[Test]
	public async Task Ping_ServiceDown_ExitCode2()
	{
		mockClient.PingAsync(Arg.Any<CancellationToken>())
			.Returns<PingReply>(_ => throw new VhdxManagerServiceException("service down"));

		var (exitCode, _, stderr) = await InvokeWithOutput("ping");

		exitCode.Should().Be(2);
		stderr.Should().Contain("service down");
	}

	[Test]
	public async Task Ping_Timeout_ExitCode3()
	{
		mockClient.PingAsync(Arg.Any<CancellationToken>())
			.Returns<PingReply>(_ => throw new TimeoutException("timed out"));

		var (exitCode, _, stderr) = await InvokeWithOutput("ping");

		exitCode.Should().Be(3);
		stderr.Should().Contain("timed out");
	}

	// ─── init ───────────────────────────────────────────────────────────────

	[Test]
	public async Task Init_Success_ExitCode0()
	{
		// init now also calls GetSettings to resolve the Defender default. Default
		// reply (HasDefault=false) → CLI would prompt; but with --add-defender-exclusion
		// passed below the prompt is bypassed.
		mockClient.GetSettingsAsync(Arg.Any<CancellationToken>())
			.Returns(new GetSettingsReply());
		mockClient.CreateChildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
			.Returns(new CreateChildReply { Success = true, VolumeGuidPath = @"\\?\Volume{abcd}\" });

		var (exitCode, stdout, _) = await InvokeWithOutput(@"init --parent C:\p.vhdx --child C:\c.vhdx --mount C:\m --add-defender-exclusion false");

		exitCode.Should().Be(0);
		stdout.Should().Contain("Volume GUID");
	}

	[Test]
	public async Task Init_ServerFailure_ExitCode1()
	{
		mockClient.GetSettingsAsync(Arg.Any<CancellationToken>())
			.Returns(new GetSettingsReply());
		mockClient.CreateChildAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
			.Returns(new CreateChildReply { Success = false, ErrorMessage = "path invalid" });

		var (exitCode, _, stderr) = await InvokeWithOutput(@"init --parent C:\p.vhdx --child C:\c.vhdx --mount C:\m --add-defender-exclusion false");

		exitCode.Should().Be(1);
		stderr.Should().Contain("path invalid");
	}

	// ─── reset ──────────────────────────────────────────────────────────────

	[Test]
	public async Task Reset_Success_ExitCode0()
	{
		mockClient.ResetChildAsync(Arg.Any<string>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
			.Returns(new ResetChildReply { Success = true });

		var exitCode = await Invoke(@"reset --child C:\c.vhdx");

		exitCode.Should().Be(0);
	}

	[Test]
	public async Task Reset_ServerFailure_ExitCode1()
	{
		mockClient.ResetChildAsync(Arg.Any<string>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
			.Returns(new ResetChildReply { Success = false, ErrorMessage = "not tracked" });

		var (exitCode, _, stderr) = await InvokeWithOutput(@"reset --child C:\c.vhdx");

		exitCode.Should().Be(1);
		stderr.Should().Contain("not tracked");
	}

	// ─── cleanup ────────────────────────────────────────────────────────────

	[Test]
	public async Task Cleanup_Success_ExitCode0()
	{
		mockClient.DetachAsync(Arg.Any<string>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
			.Returns(new DetachReply { Success = true });

		var exitCode = await Invoke(@"cleanup --child C:\c.vhdx");

		exitCode.Should().Be(0);
	}

	// ─── status ─────────────────────────────────────────────────────────────

	[Test]
	public async Task Status_PrintsAllFields()
	{
		mockClient.GetStatusAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
			.Returns(new GetStatusReply
			{
				IsAttached = true,
				MountPath = @"C:\mount",
				ParentVhdxPath = @"C:\parent.vhdx",
				VolumeGuidPath = @"\\?\Volume{xyz}\",
				ChildSizeBytes = 4096,
			});

		var (exitCode, stdout, _) = await InvokeWithOutput(@"status --child C:\c.vhdx");

		exitCode.Should().Be(0);
		stdout.Should().Contain("True");
		stdout.Should().Contain(@"C:\mount");
		stdout.Should().Contain(@"C:\parent.vhdx");
		stdout.Should().Contain("4,096");
	}

	// ─── publish ────────────────────────────────────────────────────────────

	[Test]
	public async Task Publish_Success_ShowsCount()
	{
		mockClient.PublishAsync(Arg.Any<string>(), Arg.Any<Action<ProgressEvent>?>(), Arg.Any<CancellationToken>())
			.Returns(new PublishReply { Success = true, ChildrenRecreated = 3 });

		var (exitCode, stdout, _) = await InvokeWithOutput(@"publish --overlay C:\overlay.vhdx");

		exitCode.Should().Be(0);
		stdout.Should().Contain("3");
	}

	// ─── list ───────────────────────────────────────────────────────────────

	[Test]
	public async Task List_NoMounts_PrintsEmpty()
	{
		mockClient.ListMountsAsync(Arg.Any<CancellationToken>())
			.Returns(new ListMountsReply());

		var (exitCode, stdout, _) = await InvokeWithOutput("list");

		exitCode.Should().Be(0);
		stdout.Should().Contain("No active mounts");
	}

	[Test]
	public async Task List_WithMounts_PrintsDetails()
	{
		var reply = new ListMountsReply();
		reply.Mounts.Add(new MountInfo
		{
			ChildVhdxPath = @"C:\child1.vhdx",
			ParentVhdxPath = @"C:\parent.vhdx",
			MountPath = @"C:\m1",
			IsAttached = true,
			ChildSizeBytes = 1024,
		});
		mockClient.ListMountsAsync(Arg.Any<CancellationToken>()).Returns(reply);

		var (exitCode, stdout, _) = await InvokeWithOutput("list");

		exitCode.Should().Be(0);
		stdout.Should().Contain(@"C:\child1.vhdx");
		stdout.Should().Contain(@"C:\m1");
		stdout.Should().Contain("True");
	}

	// ─── global options ─────────────────────────────────────────────────────

	[Test]
	public async Task PipeName_DefaultsToServiceConstantsValue()
	{
		mockClient.PingAsync(Arg.Any<CancellationToken>())
			.Returns(new PingReply { Version = "1.0" });

		await Invoke("ping");

		capturedPipeName.Should().Be(ServiceConstants.PipeName);
	}

	[Test]
	public async Task PipeName_CanBeOverridden()
	{
		mockClient.PingAsync(Arg.Any<CancellationToken>())
			.Returns(new PingReply { Version = "1.0" });

		await Invoke("--pipe-name CustomPipe ping");

		capturedPipeName.Should().Be("CustomPipe");
	}

	[Test]
	public async Task Timeout_DefaultsToNull()
	{
		mockClient.PingAsync(Arg.Any<CancellationToken>())
			.Returns(new PingReply { Version = "1.0" });

		await Invoke("ping");

		capturedTimeout.Should().BeNull();
	}

	[Test]
	public async Task Timeout_CanBeSet()
	{
		mockClient.PingAsync(Arg.Any<CancellationToken>())
			.Returns(new PingReply { Version = "1.0" });

		await Invoke("--timeout 30 ping");

		capturedTimeout.Should().Be(TimeSpan.FromSeconds(30));
	}
}

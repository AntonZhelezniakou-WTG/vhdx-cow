using System.CommandLine;
using VhdxManager.Client;
using VhdxManager.Contracts;

namespace VhdxManager.Cli;

/// <summary>
/// Builds the CLI command tree. Accepts an optional client factory for testability.
/// </summary>
static class CommandFactory
{
	public static RootCommand CreateRootCommand(
		Func<string, TimeSpan?, IVhdxManagerClient>? clientFactory = null)
	{
		clientFactory ??= (pipe, timeout) => new VhdxManagerClient(pipe, timeout);

		// --- global options ---
		var pipeNameOption = new Option<string>("--pipe-name")
		{
			Description = "Named pipe to connect to the VhdxManager service",
			DefaultValueFactory = _ => ServiceConstants.PipeName,
		};

		var timeoutOption = new Option<int?>("--timeout")
		{
			Description = "Timeout in seconds for the operation",
		};

		var rootCommand = new RootCommand("VHDX management tool")
		{
			Options = { pipeNameOption, timeoutOption },
		};

		// --- ping ---
		var pingCommand = new Command("ping", "Check if the VhdxManager service is running");
		pingCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				var reply = await client.PingAsync(token);
				Console.WriteLine($"Service is running. Version: {reply.Version}, Active mounts: {reply.ActiveMounts}");
				return 0;
			}));

		// --- reset ---
		var resetChildOption = new Option<string>("--child") { Description = "Path to the child VHDX file", Required = true };
		var resetCommand = new Command("reset", "Discard all changes in a child VHDX, restoring parent state")
		{
			Options = { resetChildOption },
		};

		resetCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				using var progress = new ProgressRenderer();
				var reply = await client.ResetChildAsync(
					parseResult.GetValue(resetChildOption)!, progress.Handle, token);
				if (reply.Success) return 0;
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
			}));

		// --- cleanup ---
		var cleanupChildOption = new Option<string>("--child") { Description = "Path to the child VHDX file", Required = true };
		var cleanupCommand = new Command("cleanup", "Unmount, detach, and delete a child VHDX")
		{
			Options = { cleanupChildOption },
		};

		cleanupCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				using var progress = new ProgressRenderer();
				var reply = await client.DetachAsync(
					parseResult.GetValue(cleanupChildOption)!, progress.Handle, token);
				if (reply.Success) return 0;
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
			}));

		// --- status ---
		var statusChildOption = new Option<string>("--child") { Description = "Path to the child VHDX file", Required = true };
		var statusCommand = new Command("status", "Show the status of a managed VHDX mount")
		{
			Options = { statusChildOption },
		};

		statusCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				var reply = await client.GetStatusAsync(parseResult.GetValue(statusChildOption)!, token);
				Console.WriteLine($"Attached:    {reply.IsAttached}");
				Console.WriteLine($"Mount path:  {reply.MountPath}");
				Console.WriteLine($"Parent:      {reply.ParentVhdxPath}");
				Console.WriteLine($"Volume GUID: {reply.VolumeGuidPath}");
				Console.WriteLine($"Child size:  {reply.ChildSizeBytes:N0} bytes");
				return 0;
			}));

		// --- publish ---
		var overlayOption = new Option<string>("--overlay") { Description = "Path to the overlay VHDX to merge into the parent", Required = true };
		var publishCommand = new Command("publish", "Merge an overlay VHDX into its parent and recreate all children")
		{
			Options = { overlayOption },
		};

		publishCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				using var progress = new ProgressRenderer();
				var reply = await client.PublishAsync(
					parseResult.GetValue(overlayOption)!, progress.Handle, token);
				if (reply.Success)
				{
					Console.WriteLine($"Children recreated: {reply.ChildrenRecreated}");
					return 0;
				}
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
			}));

		// --- list ---
		var listCommand = new Command("list", "Show all active VHDX mounts");

		listCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				var reply = await client.ListMountsAsync(token);
				if (reply.Mounts.Count == 0)
				{
					Console.WriteLine("No active mounts.");
					return 0;
				}
				foreach (var m in reply.Mounts)
				{
					Console.WriteLine($"Child:    {m.ChildVhdxPath}");
					Console.WriteLine($"Parent:   {m.ParentVhdxPath}");
					Console.WriteLine($"Mount:    {m.MountPath}");
					Console.WriteLine($"Attached: {m.IsAttached}");
					Console.WriteLine($"Size:     {m.ChildSizeBytes:N0} bytes");
					Console.WriteLine();
				}
				return 0;
			}));

		// --- mount (existing standalone VHDX) ---
		var mountVhdxOption = new Option<string>("--path") { Description = "Path to the VHDX file to mount", Required = true };
		var mountFolderOption = new Option<string>("--mount") { Description = "Folder to mount the VHDX to", Required = true };
		var mountCommand = new Command("mount", "Attach + mount an existing VHDX without creating it.")
		{
			Options = { mountVhdxOption, mountFolderOption },
		};

		mountCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				using var progress = new ProgressRenderer();
				var reply = await client.AttachAndMountAsync(
					parseResult.GetValue(mountVhdxOption)!,
					parseResult.GetValue(mountFolderOption)!,
					progress.Handle, token);
				if (reply.Success)
				{
					Console.WriteLine($"Volume GUID: {reply.VolumeGuidPath}");
					return 0;
				}
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
			}));

		// --- unmount (keep file) ---
		var unmountVhdxOption = new Option<string>("--path") { Description = "Path to the VHDX file to unmount", Required = true };
		var unmountCommand = new Command("unmount", "Unmount + detach a VHDX, keeping the file on disk.")
		{
			Options = { unmountVhdxOption },
		};

		unmountCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				using var progress = new ProgressRenderer();
				var reply = await client.UnmountAndDetachAsync(
					parseResult.GetValue(unmountVhdxOption)!, progress.Handle, token);
				if (reply.Success) return 0;
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
			}));

		// --- delete (unmount + detach + delete file) ---
		// Path is a positional argument so the natural form works:
		//     vhmgr delete C:\path\to\file.vhdx
		var deleteVhdxArg = new Argument<string>("path") { Description = "Path to the VHDX file to delete" };
		var deleteCommand = new Command("delete", "Unmount + detach + delete the VHDX file.")
		{
			Arguments = { deleteVhdxArg },
		};

		deleteCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				// Delete reuses the existing destructive Detach RPC.
				using var progress = new ProgressRenderer();
				var reply = await client.DetachAsync(
					parseResult.GetValue(deleteVhdxArg)!, progress.Handle, token);
				if (reply.Success) return 0;
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
			}));

		rootCommand.Subcommands.Add(pingCommand);
		rootCommand.Subcommands.Add(resetCommand);
		rootCommand.Subcommands.Add(cleanupCommand);
		rootCommand.Subcommands.Add(statusCommand);
		rootCommand.Subcommands.Add(publishCommand);
		rootCommand.Subcommands.Add(listCommand);
		rootCommand.Subcommands.Add(LogsCommand.Create());
		rootCommand.Subcommands.Add(CreateCommand.Build(pipeNameOption, timeoutOption, clientFactory));
		rootCommand.Subcommands.Add(mountCommand);
		rootCommand.Subcommands.Add(unmountCommand);
		rootCommand.Subcommands.Add(deleteCommand);
		rootCommand.Subcommands.Add(ConvertCommand.Build(pipeNameOption, timeoutOption, clientFactory));
		rootCommand.Subcommands.Add(ConfigCommand.Build(pipeNameOption, timeoutOption, clientFactory));

		return rootCommand;
	}

	static async Task<int> RunCommand(
		ParseResult parseResult,
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		CancellationToken ct,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory,
		Func<IVhdxManagerClient, CancellationToken, Task<int>> action)
	{
		var pipeName = parseResult.GetValue(pipeNameOption)!;
		var timeoutSeconds = parseResult.GetValue(timeoutOption);
		var timeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;

		using var client = clientFactory(pipeName, timeout);
		try
		{
			return await action(client, ct);
		}
		catch (VhdxManagerServiceException ex)
		{
			Console.Error.WriteLine(ex.Message);
			return 2;
		}
		catch (TimeoutException ex)
		{
			Console.Error.WriteLine($"Timeout: {ex.Message}");
			return 3;
		}
		catch (Exception ex)
		{
			Console.Error.WriteLine($"Error: {ex.Message}");
			return 1;
		}
	}
}

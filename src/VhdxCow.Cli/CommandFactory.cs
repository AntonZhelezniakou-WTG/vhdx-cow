using System.CommandLine;
using VhdxCow.Client;
using VhdxCow.Contracts;

namespace VhdxCow.Cli;

/// <summary>
/// Builds the CLI command tree. Accepts an optional client factory for testability.
/// </summary>
static class CommandFactory
{
	public static RootCommand CreateRootCommand(
		Func<string, TimeSpan?, IVhdxCowClient>? clientFactory = null)
	{
		clientFactory ??= (pipe, timeout) => new VhdxCowClient(pipe, timeout);

		// --- global options ---
		var pipeNameOption = new Option<string>("--pipe-name")
		{
			Description = "Named pipe to connect to the VhdxCow service",
			DefaultValueFactory = _ => ServiceConstants.PipeName,
		};

		var timeoutOption = new Option<int?>("--timeout")
		{
			Description = "Timeout in seconds for the operation",
		};

		var rootCommand = new RootCommand("VHDX Copy-on-Write management tool")
		{
			Options = { pipeNameOption, timeoutOption },
		};

		// --- ping ---
		var pingCommand = new Command("ping", "Check if the VhdxCow service is running");
		pingCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				var reply = await client.PingAsync(token);
				Console.WriteLine($"Service is running. Version: {reply.Version}, Active mounts: {reply.ActiveMounts}");
				return 0;
			}));

		// --- init ---
		var parentOption = new Option<string>("--parent") { Description = "Path to the parent VHDX file", Required = true };
		var childOption = new Option<string>("--child") { Description = "Path for the new child VHDX file", Required = true };
		var mountOption = new Option<string>("--mount") { Description = "Folder path to mount the child VHDX", Required = true };

		var initCommand = new Command("init", "Create a differencing VHDX and mount it to a worktree folder")
		{
			Options = { parentOption, childOption, mountOption },
		};
		initCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				var reply = await client.CreateChildAsync(
					parseResult.GetValue(parentOption)!,
					parseResult.GetValue(childOption)!,
					parseResult.GetValue(mountOption)!,
					token);
				if (reply.Success)
				{
					Console.WriteLine($"Child VHDX created and mounted at {parseResult.GetValue(mountOption)}");
					Console.WriteLine($"Volume GUID: {reply.VolumeGuidPath}");
					return 0;
				}
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
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
				var reply = await client.ResetChildAsync(parseResult.GetValue(resetChildOption)!, token);
				if (reply.Success)
				{
					Console.WriteLine("Child VHDX reset to parent state");
					return 0;
				}
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
				var reply = await client.DetachAsync(parseResult.GetValue(cleanupChildOption)!, token);
				if (reply.Success)
				{
					Console.WriteLine("Child VHDX detached and deleted");
					return 0;
				}
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
				var reply = await client.PublishAsync(parseResult.GetValue(overlayOption)!, token);
				if (reply.Success)
				{
					Console.WriteLine($"Publish completed. Children recreated: {reply.ChildrenRecreated}");
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
				var reply = await client.AttachAndMountAsync(
					parseResult.GetValue(mountVhdxOption)!,
					parseResult.GetValue(mountFolderOption)!,
					token);
				if (reply.Success)
				{
					Console.WriteLine($"Mounted to {parseResult.GetValue(mountFolderOption)} (volume {reply.VolumeGuidPath})");
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
				var reply = await client.UnmountAndDetachAsync(parseResult.GetValue(unmountVhdxOption)!, token);
				if (reply.Success)
				{
					Console.WriteLine("VHDX unmounted and detached (file kept).");
					return 0;
				}
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
			}));

		// --- delete (unmount + detach + delete file) ---
		var deleteVhdxOption = new Option<string>("--path") { Description = "Path to the VHDX file to delete", Required = true };
		var deleteCommand = new Command("delete", "Unmount + detach + delete the VHDX file.")
		{
			Options = { deleteVhdxOption },
		};
		deleteCommand.SetAction(async (parseResult, ct) =>
			await RunCommand(parseResult, pipeNameOption, timeoutOption, ct, clientFactory, async (client, token) =>
			{
				// Delete reuses the existing destructive Detach RPC.
				var reply = await client.DetachAsync(parseResult.GetValue(deleteVhdxOption)!, token);
				if (reply.Success)
				{
					Console.WriteLine("VHDX detached and file deleted.");
					return 0;
				}
				Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
				return 1;
			}));

		rootCommand.Subcommands.Add(pingCommand);
		rootCommand.Subcommands.Add(initCommand);
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

		return rootCommand;
	}

	static async Task<int> RunCommand(
		ParseResult parseResult,
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		CancellationToken ct,
		Func<string, TimeSpan?, IVhdxCowClient> clientFactory,
		Func<IVhdxCowClient, CancellationToken, Task<int>> action)
	{
		var pipeName = parseResult.GetValue(pipeNameOption)!;
		var timeoutSeconds = parseResult.GetValue(timeoutOption);
		var timeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;

		using var client = clientFactory(pipeName, timeout);
		try
		{
			return await action(client, ct);
		}
		catch (VhdxCowServiceException ex)
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

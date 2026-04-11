using System.CommandLine;
using VhdxCow.Client;

var rootCommand = new RootCommand("VHDX Copy-on-Write management tool");

// --- ping ---
var pingCommand = new Command("ping", "Check if the VhdxCow service is running");
pingCommand.SetAction(async (_, ct) =>
{
	using var client = new VhdxCowClient();
	try
	{
		var reply = await client.PingAsync(ct);
		Console.WriteLine($"Service is running. Version: {reply.Version}, Active mounts: {reply.ActiveMounts}");
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"Failed to connect to VhdxCow service: {ex.Message}");
		return 1;
	}
	return 0;
});

// --- init ---
var parentOption = new Option<string>("--parent") { Description = "Path to the parent VHDX file", Required = true };
var childOption = new Option<string>("--child") { Description = "Path for the new child VHDX file", Required = true };
var mountOption = new Option<string>("--mount") { Description = "Folder path to mount the child VHDX", Required = true };

var initCommand = new Command("init", "Create a differencing VHDX and mount it to a worktree folder")
{
	Options = { parentOption, childOption, mountOption },
};
initCommand.SetAction(async (parseResult, ct) =>
{
	var parent = parseResult.GetValue(parentOption)!;
	var child = parseResult.GetValue(childOption)!;
	var mount = parseResult.GetValue(mountOption)!;

	using var client = new VhdxCowClient();
	try
	{
		var reply = await client.CreateChildAsync(parent, child, mount, ct);
		if (reply.Success)
		{
			Console.WriteLine($"Child VHDX created and mounted at {mount}");
			Console.WriteLine($"Volume GUID: {reply.VolumeGuidPath}");
		}
		else
		{
			Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
			return 1;
		}
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"Error: {ex.Message}");
		return 1;
	}
	return 0;
});

// --- reset ---
var resetChildOption = new Option<string>("--child") { Description = "Path to the child VHDX file", Required = true };
var resetCommand = new Command("reset", "Discard all changes in a child VHDX, restoring parent state")
{
	Options = { resetChildOption },
};
resetCommand.SetAction(async (parseResult, ct) =>
{
	var child = parseResult.GetValue(resetChildOption)!;

	using var client = new VhdxCowClient();
	try
	{
		var reply = await client.ResetChildAsync(child, ct);
		if (reply.Success)
		{
			Console.WriteLine("Child VHDX reset to parent state");
		}
		else
		{
			Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
			return 1;
		}
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"Error: {ex.Message}");
		return 1;
	}
	return 0;
});

// --- cleanup ---
var cleanupChildOption = new Option<string>("--child") { Description = "Path to the child VHDX file", Required = true };
var cleanupCommand = new Command("cleanup", "Unmount, detach, and delete a child VHDX")
{
	Options = { cleanupChildOption },
};
cleanupCommand.SetAction(async (parseResult, ct) =>
{
	var child = parseResult.GetValue(cleanupChildOption)!;

	using var client = new VhdxCowClient();
	try
	{
		var reply = await client.DetachAsync(child, ct);
		if (reply.Success)
		{
			Console.WriteLine("Child VHDX detached and deleted");
		}
		else
		{
			Console.Error.WriteLine($"Failed: {reply.ErrorMessage}");
			return 1;
		}
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"Error: {ex.Message}");
		return 1;
	}
	return 0;
});

// --- status ---
var statusChildOption = new Option<string>("--child") { Description = "Path to the child VHDX file", Required = true };
var statusCommand = new Command("status", "Show the status of a managed VHDX mount")
{
	Options = { statusChildOption },
};
statusCommand.SetAction(async (parseResult, ct) =>
{
	var child = parseResult.GetValue(statusChildOption)!;

	using var client = new VhdxCowClient();
	try
	{
		var reply = await client.GetStatusAsync(child, ct);
		Console.WriteLine($"Attached:    {reply.IsAttached}");
		Console.WriteLine($"Mount path:  {reply.MountPath}");
		Console.WriteLine($"Parent:      {reply.ParentVhdxPath}");
		Console.WriteLine($"Volume GUID: {reply.VolumeGuidPath}");
		Console.WriteLine($"Child size:  {reply.ChildSizeBytes:N0} bytes");
	}
	catch (Exception ex)
	{
		Console.Error.WriteLine($"Error: {ex.Message}");
		return 1;
	}
	return 0;
});

rootCommand.Subcommands.Add(pingCommand);
rootCommand.Subcommands.Add(initCommand);
rootCommand.Subcommands.Add(resetCommand);
rootCommand.Subcommands.Add(cleanupCommand);
rootCommand.Subcommands.Add(statusCommand);

return await rootCommand.Parse(args).InvokeAsync();

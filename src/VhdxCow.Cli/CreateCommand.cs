using System.CommandLine;
using Spectre.Console;
using VhdxCow.Client;

namespace VhdxCow.Cli;

/// <summary>
/// `vhmgr create` — create a fresh standalone VHDX (GPT + NTFS) and optionally
/// mount it to a folder. Missing required options are prompted for via Spectre.
/// </summary>
internal static class CreateCommand
{
	public static Command Build(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxCowClient> clientFactory)
	{
		var pathOption = new Option<string?>("--path") { Description = "Path of the new VHDX file" };
		var sizeOption = new Option<string?>("--size") { Description = "Size, e.g. 50G, 500M, 1T" };
		var labelOption = new Option<string?>("--label") { Description = "NTFS volume label (default: data)" };
		var mountOption = new Option<string?>("--mount") { Description = "Folder to mount the new disk (optional)" };
		var dynamicOption = new Option<bool?>("--dynamic") { Description = "Create a dynamic VHDX (default: true)" };
		var fixedOption = new Option<bool?>("--fixed") { Description = "Create a fixed (preallocated) VHDX" };

		var command = new Command("create", "Create a new standalone VHDX, partition + format NTFS, optionally mount.")
		{
			Options = { pathOption, sizeOption, labelOption, mountOption, dynamicOption, fixedOption },
		};

		command.SetAction(async (parseResult, ct) =>
		{
			var pipeName = parseResult.GetValue(pipeNameOption)!;
			var timeoutSeconds = parseResult.GetValue(timeoutOption);
			var timeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;

			var path = parseResult.GetValue(pathOption);
			var sizeRaw = parseResult.GetValue(sizeOption);
			var label = parseResult.GetValue(labelOption);
			var mount = parseResult.GetValue(mountOption);
			var dyn = parseResult.GetValue(dynamicOption);
			var fix = parseResult.GetValue(fixedOption);

			try
			{
				path ??= InteractivePrompt.AskString("Path to new VHDX file");

				long sizeBytes;
				if (string.IsNullOrEmpty(sizeRaw))
				{
					sizeBytes = InteractivePrompt.AskSize("Size");
				}
				else if (!InteractivePrompt.TryParseSize(sizeRaw, out sizeBytes))
				{
					AnsiConsole.MarkupLine($"[red]Invalid --size '{sizeRaw}'.[/] Use 50G / 500M / 1T.");
					return 1;
				}

				label ??= InteractivePrompt.AskString("NTFS volume label", defaultValue: "data");

				bool dynamic;
				if (dyn == true && fix == true)
				{
					AnsiConsole.MarkupLine("[red]Cannot specify both --dynamic and --fixed.[/]");
					return 1;
				}

				if (dyn == true)
					dynamic = true;
				else if (fix == true)
					dynamic = false;
				else
					dynamic = InteractivePrompt.AskDynamicVsFixed("Disk type");

				mount ??= InteractivePrompt.AskOptionalString("Mount to folder (leave blank to skip)");

				using var client = clientFactory(pipeName, timeout);
				var resp = await AnsiConsole.Status()
					.StartAsync(
						$"Creating VHDX ({InteractivePrompt.FormatSize(sizeBytes)}, {(dynamic ? "dynamic" : "fixed")})…",
						async _ => await client.CreateVhdxAsync(path, sizeBytes, dynamic, label, mount, ct));

				if (resp.Success)
				{
					AnsiConsole.MarkupLine($"[green]✓[/] VHDX created at [yellow]{path}[/]");
					if (!string.IsNullOrEmpty(resp.VolumeGuidPath))
					{
						AnsiConsole.MarkupLine($"[green]✓[/] Mounted to [yellow]{mount}[/] (volume {resp.VolumeGuidPath})");
					}
					return 0;
				}

				AnsiConsole.MarkupLine($"[red]Failed:[/] {resp.ErrorMessage}");
				return 1;
			}
			catch (VhdxCowServiceException ex)
			{
				AnsiConsole.MarkupLine($"[red]{ex.Message}[/]");
				return 2;
			}
			catch (TimeoutException ex)
			{
				AnsiConsole.MarkupLine($"[red]Timeout: {ex.Message}[/]");
				return 3;
			}
			catch (Exception ex)
			{
				AnsiConsole.MarkupLine($"[red]Error:[/] {ex.Message}");
				return 1;
			}
		});

		return command;
	}
}

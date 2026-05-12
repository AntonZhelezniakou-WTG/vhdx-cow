using System.CommandLine;
using Spectre.Console;
using VhdxManager.Client;

namespace VhdxManager.Cli;

/// <summary>
/// `vhmgr convert` — convert an existing folder into a VHDX-mounted folder.
/// Renames the source folder aside, creates + mounts a fresh VHDX in its place,
/// robocopies content back, optionally deletes the staging copy.
/// </summary>
static class ConvertCommand
{
	public static Command Build(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory)
	{
		var folderOption = new Option<string?>("--folder") { Description = "Folder to convert" };
		var vhdxOption = new Option<string?>("--vhdx") { Description = "Path of the VHDX file to create" };
		var sizeOption = new Option<string?>("--size") { Description = "VHDX size, e.g. 50G, 500M, 1T" };
		var labelOption = new Option<string?>("--label") { Description = "Volume label (default: data)" };
		var dynamicOption = new Option<bool?>("--dynamic") { Description = "Dynamic VHDX (default: true)" };
		var fixedOption = new Option<bool?>("--fixed") { Description = "Fixed (preallocated) VHDX" };
		var keepStagingOption = new Option<bool>("--keep-staging") { Description = "Keep the renamed source folder after copy" };
		var yesOption = new Option<bool>("--yes") { Description = "Skip the confirmation prompt" };
		var filesystemOption = new Option<string?>("--filesystem")
		{
			Description = "Filesystem to format with: ReFS (default) or NTFS",
		};
		var addDefenderExclusionOption = new Option<bool?>("--add-defender-exclusion")
		{
			Description = "Register the new VHDX file with Windows Defender exclusions",
		};

		var command = new Command(
			"convert",
			"Convert an existing folder to a VHDX-mounted folder (renames source, creates VHDX, robocopies content).")
		{
			Options =
			{
				folderOption,
				vhdxOption,
				sizeOption,
				labelOption,
				dynamicOption,
				fixedOption,
				keepStagingOption,
				yesOption,
				filesystemOption,
				addDefenderExclusionOption,
			},
		};

		command.SetAction(async (parseResult, ct) =>
		{
			var pipeName = parseResult.GetValue(pipeNameOption)!;
			var timeoutSeconds = parseResult.GetValue(timeoutOption);
			var timeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;

			var folder = parseResult.GetValue(folderOption);
			var vhdxPath = parseResult.GetValue(vhdxOption);
			var sizeRaw = parseResult.GetValue(sizeOption);
			var label = parseResult.GetValue(labelOption);
			var isDynamic = parseResult.GetValue(dynamicOption);
			var isFixed = parseResult.GetValue(fixedOption);
			var keepStaging = parseResult.GetValue(keepStagingOption);
			var yes = parseResult.GetValue(yesOption);
			var filesystem = parseResult.GetValue(filesystemOption);
			var addDefenderExclusionRaw = parseResult.GetValue(addDefenderExclusionOption);

			try
			{
				folder ??= InteractivePrompt.AskString("Folder to convert");
				vhdxPath ??= InteractivePrompt.AskString("VHDX file path");

				long sizeBytes;
				if (string.IsNullOrEmpty(sizeRaw))
				{
					sizeBytes = InteractivePrompt.AskSize("VHDX size");
				}
				else if (!InteractivePrompt.TryParseSize(sizeRaw, out sizeBytes))
				{
					AnsiConsole.MarkupLine($"[red]Invalid --size '{sizeRaw}'.[/] Use 50G / 500M / 1T.");
					return 1;
				}

				label ??= InteractivePrompt.AskString("Volume label", defaultValue: "data");

				bool dynamic;
				switch (isDynamic)
				{
					case true when isFixed == true:
						AnsiConsole.MarkupLine("[red]Cannot specify both --dynamic and --fixed.[/]");
						return 1;
					case true:
						dynamic = true;
						break;
					default:
					{
						dynamic = isFixed != true; // default: dynamic, no prompt
						break;
					}
				}

				// Filesystem: default ReFS, no interactive prompt (user must opt-in to NTFS via --filesystem).
				filesystem = CreateCommand.NormalizeFilesystem(filesystem);
				if (filesystem is null)
				{
					AnsiConsole.MarkupLine($"[red]Invalid --filesystem '{filesystem}'.[/] Use 'ReFS' or 'NTFS'.");
					return 1;
				}

				using var client = clientFactory(pipeName, timeout);

				// Resolve before the confirmation prompt so the user sees the final value
				// in the summary block.
				var addDefender = await DefenderExclusionResolver.ResolveAsync(
					addDefenderExclusionRaw, client, ct);

				if (!yes)
				{
					AnsiConsole.MarkupLine("");
					AnsiConsole.MarkupLine($"[bold]Folder:[/]       {folder}");
					AnsiConsole.MarkupLine($"[bold]VHDX:[/]         {vhdxPath}");
					AnsiConsole.MarkupLine($"[bold]Size:[/]         {InteractivePrompt.FormatSize(sizeBytes)} ({(dynamic ? "dynamic" : "fixed")})");
					AnsiConsole.MarkupLine($"[bold]Filesystem:[/]   {filesystem}");
					AnsiConsole.MarkupLine($"[bold]Label:[/]        {label}");
					AnsiConsole.MarkupLine($"[bold]Keep staging:[/] {keepStaging}");
					AnsiConsole.MarkupLine($"[bold]Defender:[/]     {addDefender}");
					AnsiConsole.MarkupLine("");
					AnsiConsole.MarkupLine("[yellow]The folder will be renamed aside, a new VHDX created in its place, and contents copied back.[/]");
					if (!InteractivePrompt.AskBool("Proceed?", defaultValue: false))
					{
						AnsiConsole.MarkupLine("[yellow]Aborted by user.[/]");
						return 1;
					}
				}

				AnsiConsole.MarkupLine("[bold]vhmgr convert[/]");
				using var progress = new ProgressRenderer();
				var resp = await client.ConvertFolderAsync(
					folder,
					vhdxPath,
					sizeBytes,
					dynamic,
					label,
					filesystem,
					deleteStaging: !keepStaging,
					addDefender,
					progress.Handle,
					ct);

				if (resp.Success)
				{
					AnsiConsole.MarkupLine(
						$"  [grey]Copied:[/] {resp.FilesCopied:N0} files, {InteractivePrompt.FormatSize(resp.BytesCopied)}");
					if (keepStaging && !string.IsNullOrEmpty(resp.StagingFolderPath))
					{
						AnsiConsole.MarkupLine($"  [grey]Staging:[/] [yellow]{resp.StagingFolderPath}[/]");
					}
					if (!string.IsNullOrEmpty(resp.DefenderWarning))
					{
						AnsiConsole.MarkupLine($"[yellow]Warning:[/] Defender exclusion not added: {resp.DefenderWarning}");
					}
					return 0;
				}

				AnsiConsole.MarkupLine($"[red]Convert failed:[/] {resp.ErrorMessage}");
				if (!string.IsNullOrEmpty(resp.StagingFolderPath))
				{
					AnsiConsole.MarkupLine($"Staging folder (original data): [yellow]{resp.StagingFolderPath}[/]");
				}
				return 1;
			}
			catch (VhdxManagerServiceException ex)
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

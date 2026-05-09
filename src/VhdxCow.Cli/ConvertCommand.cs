using System.CommandLine;
using Spectre.Console;
using VhdxCow.Client;

namespace VhdxCow.Cli;

/// <summary>
/// `vhmgr convert` — convert an existing folder into a VHDX-mounted folder.
/// Renames the source folder aside, creates + mounts a fresh VHDX in its place,
/// robocopies content back, optionally deletes the staging copy.
/// </summary>
internal static class ConvertCommand
{
	public static Command Build(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxCowClient> clientFactory)
	{
		var folderOption = new Option<string?>("--folder") { Description = "Folder to convert" };
		var vhdxOption = new Option<string?>("--vhdx") { Description = "Path of the VHDX file to create" };
		var sizeOption = new Option<string?>("--size") { Description = "VHDX size, e.g. 50G, 500M, 1T" };
		var labelOption = new Option<string?>("--label") { Description = "NTFS volume label (default: data)" };
		var dynamicOption = new Option<bool?>("--dynamic") { Description = "Dynamic VHDX (default: true)" };
		var fixedOption = new Option<bool?>("--fixed") { Description = "Fixed (preallocated) VHDX" };
		var keepStagingOption = new Option<bool>("--keep-staging") { Description = "Keep the renamed source folder after copy" };
		var yesOption = new Option<bool>("--yes") { Description = "Skip the confirmation prompt" };

		var command = new Command(
			"convert",
			"Convert an existing folder to a VHDX-mounted folder (renames source, creates VHDX, robocopies content).")
		{
			Options =
			{
				folderOption, vhdxOption, sizeOption, labelOption,
				dynamicOption, fixedOption, keepStagingOption, yesOption,
			},
		};

		command.SetAction(async (parseResult, ct) =>
		{
			var pipeName = parseResult.GetValue(pipeNameOption)!;
			var timeoutSeconds = parseResult.GetValue(timeoutOption);
			var timeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;

			var folder = parseResult.GetValue(folderOption);
			var vhdx = parseResult.GetValue(vhdxOption);
			var sizeRaw = parseResult.GetValue(sizeOption);
			var label = parseResult.GetValue(labelOption);
			var dyn = parseResult.GetValue(dynamicOption);
			var fix = parseResult.GetValue(fixedOption);
			var keepStaging = parseResult.GetValue(keepStagingOption);
			var yes = parseResult.GetValue(yesOption);

			try
			{
				folder ??= InteractivePrompt.AskString("Folder to convert");
				vhdx ??= InteractivePrompt.AskString("VHDX file path");

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

				label ??= InteractivePrompt.AskString("NTFS volume label", defaultValue: "data");

				bool dynamic;
				if (dyn == true && fix == true)
				{
					AnsiConsole.MarkupLine("[red]Cannot specify both --dynamic and --fixed.[/]");
					return 1;
				}
				else if (dyn == true) dynamic = true;
				else if (fix == true) dynamic = false;
				else dynamic = InteractivePrompt.AskDynamicVsFixed("Disk type");

				if (!yes)
				{
					AnsiConsole.MarkupLine("");
					AnsiConsole.MarkupLine($"[bold]Folder:[/]      {folder}");
					AnsiConsole.MarkupLine($"[bold]VHDX:[/]        {vhdx}");
					AnsiConsole.MarkupLine($"[bold]Size:[/]        {InteractivePrompt.FormatSize(sizeBytes)} ({(dynamic ? "dynamic" : "fixed")})");
					AnsiConsole.MarkupLine($"[bold]Label:[/]       {label}");
					AnsiConsole.MarkupLine($"[bold]Keep staging:[/] {keepStaging}");
					AnsiConsole.MarkupLine("");
					AnsiConsole.MarkupLine("[yellow]The folder will be renamed aside, a new VHDX created in its place, and contents copied back.[/]");
					if (!InteractivePrompt.AskBool("Proceed?", defaultValue: false))
					{
						AnsiConsole.MarkupLine("[yellow]Aborted by user.[/]");
						return 1;
					}
				}

				using var client = clientFactory(pipeName, timeout);

				ConvertFolderReplyLike result = default!;
				await AnsiConsole.Status()
					.StartAsync("Converting folder…", async ctx =>
					{
						ctx.Status("Renaming source, creating VHDX, formatting NTFS, mounting…");
						var resp = await client.ConvertFolderAsync(
							folder, vhdx, sizeBytes, dynamic, label!,
							deleteStaging: !keepStaging,
							ct);
						result = new ConvertFolderReplyLike(
							resp.Success, resp.ErrorMessage, resp.StagingFolderPath,
							resp.FilesCopied, resp.BytesCopied);
					});

				if (result.Success)
				{
					AnsiConsole.MarkupLine($"[green]✓[/] Convert complete. {result.FilesCopied:N0} files, {InteractivePrompt.FormatSize(result.BytesCopied)} copied.");
					if (keepStaging || !string.IsNullOrEmpty(result.StagingFolderPath))
					{
						AnsiConsole.MarkupLine($"  Staging: [yellow]{result.StagingFolderPath}[/]");
					}
					return 0;
				}

				AnsiConsole.MarkupLine($"[red]Convert failed:[/] {result.ErrorMessage}");
				if (!string.IsNullOrEmpty(result.StagingFolderPath))
				{
					AnsiConsole.MarkupLine($"Staging folder (original data): [yellow]{result.StagingFolderPath}[/]");
				}
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

	readonly record struct ConvertFolderReplyLike(
		bool Success, string ErrorMessage, string StagingFolderPath,
		long FilesCopied, long BytesCopied);
}

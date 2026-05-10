using System.CommandLine;
using Spectre.Console;
using VhdxManager.Client;

namespace VhdxManager.Cli;

/// <summary>
/// `vhmgr create` — create a fresh standalone VHDX (GPT + NTFS) and optionally
/// mount it to a folder. Missing required options are prompted for via Spectre.
/// </summary>
internal static class CreateCommand
{
	public static Command Build(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory)
	{
		var pathOption = new Option<string?>("--path") { Description = "Path of the new VHDX file" };
		var sizeOption = new Option<string?>("--size") { Description = "Size, e.g. 50G, 500M, 1T" };
		var labelOption = new Option<string?>("--label") { Description = "Volume label (default: data)" };
		var mountOption = new Option<string?>("--mount") { Description = "Folder to mount the new disk (optional)" };
		var dynamicOption = new Option<bool?>("--dynamic") { Description = "Create a dynamic VHDX (default: true)" };
		var fixedOption = new Option<bool?>("--fixed") { Description = "Create a fixed (preallocated) VHDX" };
		// --filesystem: defaults to ReFS, but stays nullable so the user "didn't say"
		// case maps to the empty wire value (server-side default = ReFS) without
		// extra prompting.
		var filesystemOption = new Option<string?>("--filesystem")
		{
			Description = "Filesystem to format with: ReFS (default) or NTFS",
		};

		var command = new Command("create", "Create a new standalone VHDX, partition + format, optionally mount.")
		{
			Options = { pathOption, sizeOption, labelOption, mountOption, dynamicOption, fixedOption, filesystemOption },
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
			var filesystem = parseResult.GetValue(filesystemOption);

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

				label ??= InteractivePrompt.AskString("Volume label", defaultValue: "data");

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

				// Filesystem: per the project rule, default is ReFS without prompting.
				// Validate any user-supplied value early so we fail fast rather than
				// after going through the create+attach+partition pipeline.
				var fs = NormalizeFilesystem(filesystem);
				if (fs is null)
				{
					AnsiConsole.MarkupLine($"[red]Invalid --filesystem '{filesystem}'.[/] Use 'ReFS' or 'NTFS'.");
					return 1;
				}

				using var client = clientFactory(pipeName, timeout);

				AnsiConsole.MarkupLine(
					$"[bold]vhmgr create[/] [grey]({InteractivePrompt.FormatSize(sizeBytes)}, {(dynamic ? "dynamic" : "fixed")}, fs={fs}, label={label})[/]");

				// Per-step progress is rendered live by ProgressRenderer; the streaming
				// RPC emits STARTED/COMPLETED/FAILED events for every internal step.
				using var progress = new ProgressRenderer();
				var resp = await client.CreateVhdxAsync(
					path, sizeBytes, dynamic, label, mount, fs, progress.Handle, ct);

				if (resp.Success)
				{
					if (!string.IsNullOrEmpty(resp.VolumeGuidPath))
					{
						AnsiConsole.MarkupLine($"  [grey]Volume:[/] {resp.VolumeGuidPath}");
					}
					return 0;
				}

				AnsiConsole.MarkupLine($"[red]Failed:[/] {resp.ErrorMessage}");
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

	/// <summary>
	/// Resolves the user-supplied --filesystem value to the canonical name
	/// expected by the service ("ReFS" or "NTFS"). null/empty input → "ReFS"
	/// (project default — never asked interactively). Returns null for any
	/// other value so the caller can render a clear error.
	/// </summary>
	internal static string? NormalizeFilesystem(string? raw)
	{
		if (string.IsNullOrWhiteSpace(raw))
		{
			return "ReFS";
		}
		return raw.Trim().ToUpperInvariant() switch
		{
			"REFS" => "ReFS",
			"NTFS" => "NTFS",
			_ => null,
		};
	}
}

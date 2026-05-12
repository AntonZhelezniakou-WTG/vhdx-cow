using System.CommandLine;
using Spectre.Console;
using VhdxManager.Client;

namespace VhdxManager.Cli;

/// <summary>
/// `vhdx create` — create a fresh standalone VHDX (GPT + NTFS) and optionally
/// mount it to a folder. Missing required options are prompted for via Spectre.
/// </summary>
static class CreateCommand
{
	public static Command Build(
		Option<string> pipeNameOption,
		Option<int?> timeoutOption,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory)
	{
		var pathOption = new Option<string?>("--path") { Description = "Path of the new VHDX file" };
		var parentOption = new Option<string?>("--parent")
		{
			Description = "Path to a parent VHDX — when set, creates a differencing (child) VHDX instead of a standalone one. Requires --mount.",
		};
		var sizeOption = new Option<string?>("--size") { Description = "Size, e.g. 50G, 500M, 1T (standalone only)" };
		var labelOption = new Option<string?>("--label") { Description = "Volume label (default: data, standalone only)" };
		var mountOption = new Option<string?>("--mount") { Description = "Folder to mount the new disk (optional for standalone, required with --parent)" };
		var dynamicOption = new Option<bool?>("--dynamic") { Description = "Create a dynamic VHDX (default: true, standalone only)" };
		var fixedOption = new Option<bool?>("--fixed") { Description = "Create a fixed (preallocated) VHDX (standalone only)" };
		// --filesystem: defaults to ReFS, but stays nullable so the user "didn't say"
		// case maps to the empty wire value (server-side default = ReFS) without
		// extra prompting.
		var filesystemOption = new Option<string?>("--filesystem")
		{
			Description = "Filesystem to format with: ReFS (default) or NTFS (standalone only)",
		};
		// Nullable so we can distinguish "user didn't say" from "user said no":
		// unset → fall through to service default, then prompt.
		var addDefenderExclusionOption = new Option<bool?>("--add-defender-exclusion")
		{
			Description = "Register the new VHDX file with Windows Defender exclusions",
		};

		var command = new Command("create", "Create a new VHDX (standalone or differencing child), optionally mount.")
		{
			Options = {
				pathOption,
				parentOption,
				sizeOption,
				labelOption,
				mountOption,
				dynamicOption,
				fixedOption,
				filesystemOption,
				addDefenderExclusionOption,
			}
		};

		command.SetAction(async (parseResult, ct) =>
		{
			var pipeName = parseResult.GetValue(pipeNameOption)!;
			var timeoutSeconds = parseResult.GetValue(timeoutOption);
			var timeout = timeoutSeconds.HasValue ? TimeSpan.FromSeconds(timeoutSeconds.Value) : (TimeSpan?)null;

			var path = parseResult.GetValue(pathOption);
			var parent = parseResult.GetValue(parentOption);
			var sizeRaw = parseResult.GetValue(sizeOption);
			var label = parseResult.GetValue(labelOption);
			var mount = parseResult.GetValue(mountOption);
			var isDynamic = parseResult.GetValue(dynamicOption);
			var isFixed = parseResult.GetValue(fixedOption);
			var filesystem = parseResult.GetValue(filesystemOption);
			var addDefenderExclusionRaw = parseResult.GetValue(addDefenderExclusionOption);

			try
			{
				if (!string.IsNullOrEmpty(parent))
				{
					return await RunChildBranchAsync(
						pipeName,
						timeout,
						clientFactory,
						parent,
						path,
						mount,
						sizeRaw,
						label,
						isDynamic,
						isFixed,
						filesystem,
						addDefenderExclusionRaw,
						ct);
				}

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

				// Defender exclusion: CLI flag → service default → interactive prompt.
				// Resolved AFTER the client is up (we need the service to read settings).
				var addDefender = await DefenderExclusionResolver.ResolveAsync(
					addDefenderExclusionRaw, client, ct);

				AnsiConsole.MarkupLine(
					$"[bold]vhdx create[/] [grey]({InteractivePrompt.FormatSize(sizeBytes)}, {(dynamic ? "dynamic" : "fixed")}, fs={fs}, label={label}, defender={addDefender})[/]");

				// Per-step progress is rendered live by ProgressRenderer; the streaming
				// RPC emits STARTED/COMPLETED/FAILED events for every internal step.
				using var progress = new ProgressRenderer();
				var resp = await client.CreateVhdxAsync(
					path, sizeBytes, dynamic, label, mount, fs, addDefender, progress.Handle, ct);

				if (resp.Success)
				{
					if (!string.IsNullOrEmpty(resp.VolumeGuidPath))
					{
						AnsiConsole.MarkupLine($"  [grey]Volume:[/] {resp.VolumeGuidPath}");
					}
					if (!string.IsNullOrEmpty(resp.DefenderWarning))
					{
						AnsiConsole.MarkupLine($"[yellow]Warning:[/] Defender exclusion not added: {resp.DefenderWarning}");
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

	/// <summary>
	/// `vhdx create --parent …` branch: create a differencing child VHDX.
	/// Per design, --mount is mandatory in this
	/// branch (we do not silently substitute prompts) and every standalone-only
	/// option is rejected up-front so the user gets a fast, explicit error
	/// rather than seeing flags silently dropped on the floor.
	/// </summary>
	static async Task<int> RunChildBranchAsync(
		string pipeName,
		TimeSpan? timeout,
		Func<string, TimeSpan?, IVhdxManagerClient> clientFactory,
		string parent,
		string? path,
		string? mount,
		string? sizeRaw,
		string? label,
		bool? dyn,
		bool? fix,
		string? filesystem,
		bool? addDefenderExclusionRaw,
		CancellationToken ct)
	{
		if (string.IsNullOrEmpty(path))
		{
			Console.Error.WriteLine("--path is required when --parent is specified.");
			return 1;
		}
		if (string.IsNullOrEmpty(mount))
		{
			Console.Error.WriteLine("--mount is required when --parent is specified.");
			return 1;
		}

		// Standalone-only flags — reject explicitly rather than silently ignore.
		var rejected = new List<string>();
		if (!string.IsNullOrEmpty(sizeRaw)) rejected.Add("--size");
		if (!string.IsNullOrEmpty(label)) rejected.Add("--label");
		if (dyn.HasValue) rejected.Add("--dynamic");
		if (fix.HasValue) rejected.Add("--fixed");
		if (!string.IsNullOrEmpty(filesystem)) rejected.Add("--filesystem");
		if (rejected.Count > 0)
		{
			Console.Error.WriteLine(
				$"The following options are not valid with --parent (the child inherits size/format from the parent): {string.Join(", ", rejected)}");
			return 1;
		}

		using var client = clientFactory(pipeName, timeout);

		var addDefender = await DefenderExclusionResolver.ResolveAsync(
			addDefenderExclusionRaw, client, ct);

		AnsiConsole.MarkupLine(
			$"[bold]vhdx create[/] [grey](child of {parent}, mount={mount}, defender={addDefender})[/]");

		using var progress = new ProgressRenderer();
		var resp = await client.CreateChildAsync(parent, path, mount, addDefender, progress.Handle, ct);

		if (resp.Success)
		{
			if (!string.IsNullOrEmpty(resp.VolumeGuidPath))
			{
				Console.WriteLine($"Volume GUID: {resp.VolumeGuidPath}");
			}
			if (!string.IsNullOrEmpty(resp.DefenderWarning))
			{
				Console.Error.WriteLine($"Warning: Defender exclusion not added: {resp.DefenderWarning}");
			}
			return 0;
		}

		Console.Error.WriteLine($"Failed: {resp.ErrorMessage}");
		return 1;
	}
}

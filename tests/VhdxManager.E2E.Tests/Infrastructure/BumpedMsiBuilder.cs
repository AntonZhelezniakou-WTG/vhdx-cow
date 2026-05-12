using System.Diagnostics;
using System.Text;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Builds a freshly-versioned MSI on the host for use as the "to" side of an
/// upgrade test. Takes the current MSI's version, bumps the patch component
/// by one, runs the same <c>dotnet publish</c> + WiX build chain that
/// <c>build.release.cmd</c> does, and returns the path to the produced MSI.
///
/// <para>The bumped MSI lands under <c>obj/upgrade-test/msi/</c>, NOT
/// <c>installer/bin/Release/</c>. This is deliberate: <see cref="MsiArtefact.LoadOrSkip"/>
/// picks the newest MSI in <c>installer/bin/Release/</c>, and a bumped MSI
/// left there would silently become the "current" MSI on the next test run,
/// breaking the per-MSI <c>installed-clean@&lt;sha8&gt;</c> checkpoint resolution.</para>
///
/// <para>Build time is dominated by the two <c>dotnet publish</c> steps (~30 s each
/// on a warm cache); the WiX <c>dotnet build</c> adds ~10 s. The whole call
/// is ~70-90 s — acceptable as a one-time cost in <c>OneTimeSetUp</c>.</para>
/// </summary>
public static class BumpedMsiBuilder
{
	public static async Task<BumpedMsi> BuildAsync(
		MsiArtefact baseMsi,
		string repoRoot,
		CancellationToken ct = default)
	{
		var baseVersion   = ParseVersionFromFileName(baseMsi.FileName);
		var bumpedVersion = BumpPatch(baseVersion);

		var workDir    = Path.Combine(repoRoot, "obj", "upgrade-test");
		var servicePub = Path.Combine(workDir, "publish", "service");
		var cliPub     = Path.Combine(workDir, "publish", "cli");
		var msiOutDir  = Path.Combine(workDir, "msi");

		Directory.CreateDirectory(servicePub);
		Directory.CreateDirectory(cliPub);
		Directory.CreateDirectory(msiOutDir);

		// BindPath values must end with a directory separator — the WiX wxs
		// references them as `!(bindpath.ServicePublish)<file>`, which is just
		// string concatenation. The default in the wixproj already ends in '\'.
		var servicePubBind = servicePub + Path.DirectorySeparatorChar;
		var cliPubBind     = cliPub     + Path.DirectorySeparatorChar;

		// 1) Re-publish Service with bumped version — propagates to FileVersion
		//    on VhdxManager.Service.exe, which the upgrade tests assert on.
		await RunDotnetAsync(repoRoot, new[]
		{
			"publish",
			"src/VhdxManager.Service/VhdxManager.Service.csproj",
			"-c", "Release",
			"-r", "win-x64",
			"--self-contained", "true",
			"-o", servicePub,
			$"/p:Version={bumpedVersion}",
		}, ct);

		// 2) Re-publish CLI with bumped version.
		await RunDotnetAsync(repoRoot, new[]
		{
			"publish",
			"src/VhdxManager.Cli/VhdxManager.Cli.csproj",
			"-c", "Release",
			"-r", "win-x64",
			"--self-contained", "true",
			"-o", cliPub,
			$"/p:Version={bumpedVersion}",
		}, ct);

		// 3) Build the MSI pointing the WiX BindPaths at our per-test publish
		//    dirs. The wixproj exposes ServicePublishDir and CliPublishDir
		//    properties precisely for this kind of out-of-tree build.
		//
		//    WiX's CoreCompile is incremental on the .wxs inputs, but the
		//    output filename is derived from $(Version) — so changing Version
		//    alone doesn't invalidate the up-to-date check, and the build
		//    silently skips CoreCompile and then fails copying
		//    `obj\Release\VhdxManager-<bumped>.msi` (which was never produced).
		//    `-t:Rebuild` would work but also runs Clean, which deletes
		//    `installer/bin/Release/VhdxManager-<base>.msi` — the very file
		//    other fixtures need (it's the MSI that the installed-clean
		//    checkpoint was created from).
		//
		//    Workaround: blow away `installer/obj/Release/` to force WiX to
		//    re-emit, while leaving `installer/bin/Release/` untouched so the
		//    base MSI survives.
		var installerObj = Path.Combine(repoRoot, "installer", "obj", "Release");
		if (Directory.Exists(installerObj))
			Directory.Delete(installerObj, recursive: true);

		await RunDotnetAsync(repoRoot, new[]
		{
			"build",
			"installer/VhdxManager.Installer.wixproj",
			"-c", "Release",
			$"/p:Version={bumpedVersion}",
			$"/p:ServicePublishDir={servicePubBind}",
			$"/p:CliPublishDir={cliPubBind}",
		}, ct);

		// The wixproj writes to its conventional bin/Release/ path; we move the
		// MSI under obj/upgrade-test/msi/ to keep installer/bin/Release/
		// uncontaminated for MsiArtefact.LoadOrSkip on future runs.
		var defaultMsi = Path.Combine(repoRoot, "installer", "bin", "Release",
			$"VhdxManager-{bumpedVersion}.msi");
		if (!File.Exists(defaultMsi))
		{
			throw new InvalidOperationException(
				$"Bumped MSI was not produced at expected path:\n  {defaultMsi}");
		}

		var stagedMsi = Path.Combine(msiOutDir, $"VhdxManager-{bumpedVersion}.msi");
		if (File.Exists(stagedMsi)) File.Delete(stagedMsi);
		File.Move(defaultMsi, stagedMsi);

		// Also move the wixpdb so a future clean build won't re-pick the bumped
		// pair as "current" (defensive — LoadOrSkip only globs *.msi but tidiness).
		var defaultPdb = Path.ChangeExtension(defaultMsi, ".wixpdb");
		if (File.Exists(defaultPdb))
		{
			var stagedPdb = Path.ChangeExtension(stagedMsi, ".wixpdb");
			if (File.Exists(stagedPdb)) File.Delete(stagedPdb);
			File.Move(defaultPdb, stagedPdb);
		}

		return new BumpedMsi(baseVersion, bumpedVersion, stagedMsi);
	}

	static string ParseVersionFromFileName(string fileName)
	{
		// VhdxManager-0.2.0.msi → 0.2.0
		const string prefix = "VhdxManager-";
		const string suffix = ".msi";
		if (!fileName.StartsWith(prefix, StringComparison.Ordinal) ||
		    !fileName.EndsWith(suffix, StringComparison.Ordinal))
		{
			throw new InvalidOperationException(
				$"Unexpected MSI filename '{fileName}'. Expected 'VhdxManager-<version>.msi'.");
		}
		return fileName.Substring(prefix.Length, fileName.Length - prefix.Length - suffix.Length);
	}

	static string BumpPatch(string version)
	{
		var parts = version.Split('.');
		if (parts.Length < 3)
		{
			throw new InvalidOperationException(
				$"Version '{version}' must be at least Major.Minor.Patch.");
		}
		if (!int.TryParse(parts[2], out var patch))
		{
			throw new InvalidOperationException(
				$"Patch component '{parts[2]}' of version '{version}' is not numeric.");
		}
		parts[2] = (patch + 1).ToString(System.Globalization.CultureInfo.InvariantCulture);
		return string.Join('.', parts);
	}

	static async Task RunDotnetAsync(string workingDir, string[] args, CancellationToken ct)
	{
		// Use ArgumentList rather than the single-string Arguments to avoid the
		// well-known trailing-backslash escaping pitfall: a value like
		// `D:\path\` passed via Arguments would be quoted as `"D:\path\"`,
		// CommandLineToArgvW then reads `\"` as an escaped quote and the
		// argument runs into the next one. ArgumentList serialises each entry
		// with PasteArguments rules that handle this correctly.
		var psi = new ProcessStartInfo("dotnet")
		{
			WorkingDirectory       = workingDir,
			RedirectStandardOutput = true,
			RedirectStandardError  = true,
			UseShellExecute        = false,
			CreateNoWindow         = true,
		};
		foreach (var a in args) psi.ArgumentList.Add(a);

		var stdout = new StringBuilder();
		var stderr = new StringBuilder();
		using var p = Process.Start(psi)
			?? throw new InvalidOperationException("Failed to start `dotnet`.");
		p.OutputDataReceived += (_, e) => { if (e.Data is not null) stdout.AppendLine(e.Data); };
		p.ErrorDataReceived  += (_, e) => { if (e.Data is not null) stderr.AppendLine(e.Data); };
		p.BeginOutputReadLine();
		p.BeginErrorReadLine();
		await p.WaitForExitAsync(ct);
		if (p.ExitCode != 0)
		{
			// Include the tail of the build output so a flaky build surfaces in
			// the test report instead of a generic "Setup failed" message.
			var stdoutText = stdout.ToString();
			var stderrText = stderr.ToString();
			var stdoutTail = TailLines(stdoutText, 30);
			var flatArgs = string.Join(' ', args);
			throw new InvalidOperationException(
				$"`dotnet {flatArgs}` (cwd={workingDir}) failed with exit code {p.ExitCode}.\n" +
				$"stdout tail:\n{stdoutTail}\n" +
				$"stderr:\n{stderrText}");
		}
	}

	static string TailLines(string text, int count)
	{
		var lines = text.Split('\n');
		var start = Math.Max(0, lines.Length - count);
		return string.Join('\n', lines, start, lines.Length - start);
	}
}

/// <summary>
/// Result of <see cref="BumpedMsiBuilder.BuildAsync"/>: the produced MSI's host
/// path plus the source and target version strings for use in test assertions.
/// </summary>
public sealed record BumpedMsi(string BaseVersion, string BumpedVersion, string HostPath)
{
	public string FileName => Path.GetFileName(HostPath);
}

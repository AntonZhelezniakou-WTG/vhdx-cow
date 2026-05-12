using System;
using System.IO;
using System.Text.Json;
using NUnit.Framework;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Per-fixture configuration: repo location, PowerShell helpers, guest
/// credentials. MSI lookup is intentionally *not* here — it lives on
/// <see cref="MsiArtefact"/> so smoke tests that only touch the VM can run
/// without a built installer.
///
/// <para>The static <see cref="LoadOrSkip"/> entry point is the only way to
/// construct one — any missing prereq turns the calling fixture into a
/// skip with a clear remediation message rather than a noisy failure. That
/// keeps <c>dotnet test</c> green on machines without the Hyper-V rig.</para>
/// </summary>
public sealed class E2EConfig
{
	public string RepoRoot          { get; }
	public string HelpersScriptPath { get; }
	public string VmName            { get; }
	public string GuestUsername     { get; }
	public string GuestPassword     { get; }

	E2EConfig(string repoRoot, string helpers, string vmName,
		string user, string password)
	{
		RepoRoot          = repoRoot;
		HelpersScriptPath = helpers;
		VmName            = vmName;
		GuestUsername     = user;
		GuestPassword     = password;
	}

	/// <summary>Loads the config or calls <see cref="Assert.Ignore(string)"/>
	/// — never returns null, never throws.</summary>
	public static E2EConfig LoadOrSkip()
	{
		var repoRoot = FindRepoRoot();
		if (repoRoot is null)
		{
			Assert.Ignore("Could not locate repo root (VhdxManager.slnx). " +
				"Run tests from inside the cloned repo.");
		}

		var helpers = Path.Combine(repoRoot!, "tests", "e2e", "lib", "Helpers.ps1");
		if (!File.Exists(helpers))
		{
			Assert.Ignore($"PowerShell helpers not found at {helpers}. " +
				"Did the tests/e2e directory get moved or deleted?");
		}

		var credsPath = Path.Combine(repoRoot!, "tests", "e2e", ".vm-creds.json");
		if (!File.Exists(credsPath))
		{
			Assert.Ignore($"VM credentials file not found at {credsPath}. " +
				"Run tests/e2e/Bootstrap-VM.ps1 first to create the test VM.");
		}

		VmCreds creds;
		try
		{
			creds = JsonSerializer.Deserialize<VmCreds>(File.ReadAllText(credsPath), JsonOpts)
				?? throw new InvalidOperationException("Empty creds file.");
		}
		catch (Exception ex)
		{
			Assert.Ignore($"Failed to parse {credsPath}: {ex.Message}. " +
				"Re-run Bootstrap-VM.ps1 to regenerate it.");
			throw; // unreachable — Assert.Ignore unwinds
		}

		if (string.IsNullOrWhiteSpace(creds.VmName) ||
		    string.IsNullOrWhiteSpace(creds.Username) ||
		    string.IsNullOrWhiteSpace(creds.Password))
		{
			Assert.Ignore($"{credsPath} is missing required fields (VmName/Username/Password). " +
				"Re-run Bootstrap-VM.ps1 to regenerate.");
		}

		return new E2EConfig(repoRoot!, helpers, creds.VmName!, creds.Username!, creds.Password!);
	}

	static readonly JsonSerializerOptions JsonOpts = new()
	{
		PropertyNameCaseInsensitive = true,
	};

	internal static string? FindRepoRoot()
	{
		// Walk up from the test binary's directory looking for the solution
		// file. AppContext.BaseDirectory points at bin\Debug\net10.0-windows\
		// win-x64 so we have to climb several levels.
		var dir = new DirectoryInfo(AppContext.BaseDirectory);
		while (dir is not null)
		{
			if (File.Exists(Path.Combine(dir.FullName, "VhdxManager.slnx")))
			{
				return dir.FullName;
			}
			dir = dir.Parent;
		}
		return null;
	}

	sealed record VmCreds(string? VmName, string? Username, string? Password);
}

using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using NUnit.Framework;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// The MSI under test, discovered on disk. Carries the path plus a SHA-256
/// digest used to key the per-MSI <c>installed-clean@&lt;sha8&gt;</c>
/// checkpoint — that way rebuilding the installer invalidates the cached
/// checkpoint automatically.
/// </summary>
public sealed class MsiArtefact
{
	public string Path     { get; }
	public string FileName { get; }
	public string Sha256   { get; }
	/// <summary>First 8 hex chars of <see cref="Sha256"/>. Used in checkpoint names.</summary>
	public string Sha8     { get; }

	MsiArtefact(string path, string sha256)
	{
		Path     = path;
		FileName = System.IO.Path.GetFileName(path);
		Sha256   = sha256;
		Sha8     = sha256[..8];
	}

	/// <summary>
	/// Locates the MSI under test, or skips the calling test with a
	/// remediation message. Lookup order:
	/// <list type="number">
	///   <item><c>VHDXMANAGER_E2E_MSI</c> env var (absolute path).</item>
	///   <item>Newest <c>installer/bin/Release/VhdxManager-*.msi</c> by mtime.</item>
	/// </list>
	/// </summary>
	public static MsiArtefact LoadOrSkip(string repoRoot)
	{
		var path = ResolveMsiPath(repoRoot);
		if (path is null)
		{
			Assert.Ignore("No MSI found. Build it with: " +
				"`dotnet build installer/VhdxManager.Installer.wixproj -c Release` " +
				"or set VHDXMANAGER_E2E_MSI to an absolute .msi path.");
		}
		return new MsiArtefact(path!, ComputeSha256(path!));
	}

	static string? ResolveMsiPath(string repoRoot)
	{
		// Env var wins so a developer can `setx VHDXMANAGER_E2E_MSI ...` and
		// point the suite at an out-of-tree MSI (signed artifact from a PR
		// build, an older version for compat testing, etc.).
		var envOverride = Environment.GetEnvironmentVariable("VHDXMANAGER_E2E_MSI");
		if (!string.IsNullOrWhiteSpace(envOverride))
		{
			return File.Exists(envOverride) ? envOverride : null;
		}

		var releaseDir = System.IO.Path.Combine(repoRoot, "installer", "bin", "Release");
		if (!Directory.Exists(releaseDir)) return null;

		// Newest by LastWriteTime — when a developer rebuilds the installer
		// they expect the freshly-baked MSI to be picked up automatically.
		return new DirectoryInfo(releaseDir)
			.EnumerateFiles("VhdxManager-*.msi", SearchOption.TopDirectoryOnly)
			.OrderByDescending(f => f.LastWriteTimeUtc)
			.FirstOrDefault()?.FullName;
	}

	static string ComputeSha256(string path)
	{
		using var sha = SHA256.Create();
		using var fs  = File.OpenRead(path);
		var bytes = sha.ComputeHash(fs);
		return Convert.ToHexString(bytes).ToLowerInvariant();
	}
}

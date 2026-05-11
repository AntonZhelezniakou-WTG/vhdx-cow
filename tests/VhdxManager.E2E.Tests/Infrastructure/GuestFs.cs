using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Thin wrappers over <c>Test-Path</c>, <c>Get-Content</c>, <c>Get-Command</c>
/// inside the guest. All methods accept a <see cref="GuestSession"/> and a
/// guest-absolute path. Strings are passed through PowerShell single-quoting
/// (apostrophe doubled) — paths containing newlines or unmatched quotes are
/// not supported (we never produce such paths from C#).
/// </summary>
public static class GuestFs
{
	public static Task<bool> ExistsAsync(GuestSession s, string path, CancellationToken ct = default)
		=> s.InvokeJsonAsync<bool>($"[bool](Test-Path -LiteralPath '{Esc(path)}')", ct);

	public static async Task AssertFileExistsAsync(GuestSession s, string path, CancellationToken ct = default)
	{
		// PathType Leaf rejects directories — we use this for executables and
		// config files where confusing "yes the directory exists" with "yes
		// the file exists" would mask a packaging regression.
		var exists = await s.InvokeJsonAsync<bool>(
			$"[bool](Test-Path -LiteralPath '{Esc(path)}' -PathType Leaf)", ct);
		exists.Should().BeTrue($"expected file inside guest: {path}");
	}

	public static async Task AssertDirExistsAsync(GuestSession s, string path, CancellationToken ct = default)
	{
		var exists = await s.InvokeJsonAsync<bool>(
			$"[bool](Test-Path -LiteralPath '{Esc(path)}' -PathType Container)", ct);
		exists.Should().BeTrue($"expected directory inside guest: {path}");
	}

	public static Task<string> ReadAllTextAsync(GuestSession s, string path, CancellationToken ct = default)
		=> s.InvokeJsonAsync<string>(
			$"Get-Content -LiteralPath '{Esc(path)}' -Raw", ct);

	/// <summary>
	/// True if <paramref name="exeName"/> resolves through PATH inside the
	/// guest. Uses <c>Get-Command -CommandType Application</c> so PowerShell
	/// functions/aliases with the same name don't shadow a missing exe.
	/// </summary>
	public static Task<bool> IsOnPathAsync(GuestSession s, string exeName, CancellationToken ct = default)
		=> s.InvokeJsonAsync<bool>(
			$"[bool](Get-Command -Name '{Esc(exeName)}' -CommandType Application -ErrorAction SilentlyContinue)",
			ct);

	private static string Esc(string s) => s.Replace("'", "''");
}

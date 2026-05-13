using System.Text;

namespace VhdxManager.E2E.Tests.Infrastructure;

/// <summary>
/// Opaque "session" carrying everything needed to talk to the guest:
/// VM name, credentials, plus the host-side <see cref="PowerShellRunner"/>.
///
/// <para>Each call opens a fresh <c>New-PSSession -VMName</c> inside the
/// PowerShell script and tears it down on the way out. That sounds wasteful
/// but is in fact the simplest model that works across separate
/// <c>powershell.exe</c> processes (PSSession handles aren't portable across
/// processes). Session creation over VMBus is ~0.5-1 s — acceptable given
/// we have a few dozen invocations per fixture, not thousands.</para>
///
/// <para>If a single fixture ever needs many back-to-back calls and the
/// per-call overhead bites, we can introduce a "ChainedScript" helper that
/// batches multiple operations into one PowerShell invocation sharing a
/// single PSSession. Not needed today.</para>
/// </summary>
public sealed class GuestSession
{
	readonly string           _vmName;
	readonly string           _user;
	readonly string           _password;
	readonly PowerShellRunner _ps;

	public GuestSession(string vmName, string user, string password, PowerShellRunner ps)
	{
		_vmName   = vmName;
		_user     = user;
		_password = password;
		_ps       = ps;
	}

	public Task<T>  InvokeJsonAsync<T>(string scriptBlock, CancellationToken ct = default)
		=> _ps.RunJsonAsync<T>(Wrap(scriptBlock), ct);

	public Task     InvokeVoidAsync(string scriptBlock, CancellationToken ct = default)
		=> _ps.RunVoidAsync(Wrap(scriptBlock), ct);

	/// <summary>
	/// Copy a file (or directory) from host to guest. <paramref name="guestPath"/>
	/// is an absolute Windows path inside the VM. Uses <c>Copy-Item -ToSession</c>
	/// over VMBus — no host↔guest network required.
	/// </summary>
	public Task CopyToGuestAsync(string hostPath, string guestPath, CancellationToken ct = default)
	{
		var script = $$"""
			$session = New-PSSession -VMName '{{_vmName}}' -Credential $__guestCred
			try {
			    Copy-Item -ToSession $session -Path '{{Esc(hostPath)}}' -Destination '{{Esc(guestPath)}}' -Recurse -Force
			} finally {
			    Remove-PSSession $session
			}
			""";
		return _ps.RunVoidAsync(WrapWithCred(script), ct);
	}

	string Wrap(string scriptBlock)
	{
		// Wraps the user script block so it runs inside Invoke-Command -VMName.
		// The block's last expression is what gets returned to the caller.
		//
		// Invoke-Command attaches "remoting metadata" (PSComputerName,
		// RunspaceId, PSShowComputerName) as NoteProperties on every returned
		// PSObject. ConvertTo-Json then serializes those alongside the real
		// value — which turns `hostname` (a string) into a JSON object like
		// `{"PSComputerName":"...","RunspaceId":"..."}`, blowing up the C#
		// deserializer. We sanitize on the way back out:
		//   * primitives (string/int/bool) — extract BaseObject.
		//   * arrays of those — extract BaseObject element-wise.
		//   * PSCustomObjects — drop the three note-property names.
		var sb = new StringBuilder();
		sb.AppendLine(CredPrelude());
		sb.AppendLine($"$__guestRaw = Invoke-Command -VMName '{_vmName}' -Credential $__guestCred -ScriptBlock {{");
		sb.AppendLine(scriptBlock);
		sb.AppendLine("}");
		sb.AppendLine("""
			function __Strip-PSRemoting($obj) {
			    if ($null -eq $obj) { return $null }
			    if ($obj -is [System.Collections.IEnumerable] -and $obj -isnot [string]) {
			        return ,@($obj | ForEach-Object { __Strip-PSRemoting $_ })
			    }
			    if ($obj -is [System.Management.Automation.PSObject]) {
			        foreach ($name in 'PSComputerName','RunspaceId','PSShowComputerName','PSSourceJobInstanceId') {
			            if ($obj.PSObject.Properties[$name]) {
			                $obj.PSObject.Properties.Remove($name)
			            }
			        }
			        $base = $obj.PSObject.BaseObject
			        # If the wrapper just hid a primitive, return the primitive — otherwise
			        # leave the (now-cleaned) PSObject alone so it serializes as an object.
			        if ($base -is [string] -or $base -is [ValueType]) { return $base }
			    }
			    return $obj
			}
			__Strip-PSRemoting $__guestRaw
			""");
		return sb.ToString();
	}

	string WrapWithCred(string script)
	{
		var sb = new StringBuilder();
		sb.AppendLine(CredPrelude());
		sb.AppendLine(script);
		return sb.ToString();
	}

	string CredPrelude()
	{
		// Build SecureString without ConvertTo-SecureString: that cmdlet lives
		// in Microsoft.PowerShell.Security, which sometimes fails to load when
		// PowerShell is spawned as a subprocess with -NoProfile.
		var appendChars = string.Concat(
			_password.Select(c => $"\n\t$__guestPw.AppendChar('{Esc(c.ToString())}')"));
		return $"""
			$__guestPw = [System.Security.SecureString]::new(){appendChars}
			$__guestPw.MakeReadOnly()
			$__guestCred = [System.Management.Automation.PSCredential]::new('{_vmName}\{Esc(_user)}', $__guestPw)
			""";
	}

	static string Esc(string s) => s.Replace("'", "''");
}

# VhdxManager.E2E.Tests

End-to-end tests that drive the WiX MSI and `vhmgr.exe` CLI against a
disposable Hyper-V VM (`VhdxManagerE2E`). Phase A (this PR) covers the
installer surface; Phase B will cover every CLI verb.

## Prerequisites

1. **Hyper-V** enabled on the developer box (the host runs `Restore-VMSnapshot`,
   `Start-VM`, `Checkpoint-VM`, `New-PSSession -VMName`).
2. **The test VM exists.** Run `tests/e2e/Bootstrap-VM.ps1` once — it creates
   the VM, takes the `pre-install-clean` checkpoint, and writes
   `tests/e2e/.vm-creds.json` (gitignored).
3. **The MSI is built.** From the repo root:
   ```powershell
   dotnet build installer\VhdxManager.Installer.wixproj -c Release
   ```
   The tests pick up the newest `installer\bin\Release\VhdxManager-*.msi`
   automatically. Override by setting `VHDXMANAGER_E2E_MSI` to an absolute
   path.

All four of these checks call `Assert.Ignore` with a specific remediation
message when something is missing — `dotnet test` on a machine without the
rig shows the E2E suite as **Skipped**, not **Failed**.

## Running the suite

From repo root in an elevated PowerShell (Hyper-V cmdlets need it):

```powershell
dotnet test tests\VhdxManager.E2E.Tests\VhdxManager.E2E.Tests.csproj `
    --filter "Category=E2E" `
    --logger "console;verbosity=detailed"
```

`Category=E2E-Smoke` runs just the in-process PowerShellRunner sanity
checks (no VM needed) — useful when iterating on the harness itself.

## Test order and runtime

### Phase A — installer

| Fixture | Checkpoint | First-run cost | Subsequent runs |
|---|---|---|---|
| `InstalledCleanCheckpointFixture` (Order -1) | `pre-install-clean` → install → `installed-clean@<sha8>` | ~3 min | no-op |
| `Installer_Tests` (Order 1) | `pre-install-clean` | ~2 min | ~2 min |
| `Uninstall_Tests` (Order 2) | `installed-clean@<sha8>` | ~1.5 min | ~1.5 min |
| `Reinstall_Tests` (Order 3) | `installed-clean@<sha8>` — runs `/i` over top | ~1.5 min | ~1.5 min |
| `Repair_Tests` (Order 4) | `installed-clean@<sha8>` — deletes `vhmgr.exe`, runs `/fp` | ~2 min | ~2 min |
| `Upgrade_Tests` (Order 5) | `installed-clean@<sha8>` — builds X+1 MSI on host, runs `/i` over X | ~5 min | ~5 min |

The installer test asserts these files/dirs exist post-install (mirrors the
WiX component layout — keep in sync when the installer changes):

| Path | What |
|---|---|
| `C:\Program Files\VhdxManager\Service\VhdxManager.Service.exe` | Service binary |
| `C:\Program Files\VhdxManager\Service\appsettings.json` | Service config (ships with the binary) |
| `C:\Program Files\VhdxManager\Cli\vhmgr.exe` | CLI on PATH |
| `C:\ProgramData\VhdxManager\logs` | Service log directory |

### Phase B — CLI verbs (scenario fixtures)

| Fixture | Verbs exercised | Checkpoint | Typical cost |
|---|---|---|---|
| `BasicVerbs_Tests` (Order 10) | `ping`, `list` (empty), `--help`, `--version` | `installed-clean@<sha8>` | ~1 min |
| `StandaloneVhdx_Tests` (Order 20) | `create`, `list`, `unmount`, `mount`, `delete` | `installed-clean@<sha8>` | ~2 min |
| `Differencing_Tests` (Order 30) | `create` (parent), `create --parent`, `status`, `reset`, `cleanup` | `installed-clean@<sha8>` | ~3 min |
| `Convert_Tests` (Order 40) | `convert`, `list` | `installed-clean@<sha8>` | ~2 min |
| `Logs_Tests` (Order 50) | `logs --since install`, `logs --output`, `logs --since 1h` | `installed-clean@<sha8>` | ~1 min |
| `DefenderExclusion_Tests` (Order 60) | `create --add-defender-exclusion true`, `Get-MpPreference` assertion | `installed-clean@<sha8>` | ~1 min |
| `Publish_Tests` (Order 70) | `create` (parent), `create --parent` ×2 (child + overlay), `publish`, marker propagation | `installed-clean@<sha8>` | ~3 min |

Each scenario fixture boots the VM once (~30 s) and runs its verb sequence
in `[Order(N)]` — sharing one boot across related steps. Per-verb-per-fixture
would have cost ~7-14 min total just on boots; the scenario shape brings
end-to-end Phase B to ~9-10 min.

CLI invocations always pass:
* `--mount ""` (empty string, not absent) when no mount is wanted —
  omitting `--mount` falls through to `InteractivePrompt.AskOptionalString`
  and the CLI hangs forever on a redirected stdin.
* `--add-defender-exclusion false` on every state-mutating verb — without
  it, `DefenderExclusionResolver` falls through to an interactive Spectre
  prompt and dies with "Cannot show selection prompt since the current
  terminal does not support ANSI escape sequences".
* `--filesystem NTFS` on `create` for small (≤256 MB) volumes — the CLI
  defaults to ReFS, which refuses tiny formats with "Format-Volume: Size
  Not Supported". `convert` doesn't expose `--filesystem` and always uses
  ReFS, so its tests run at 4 GB (smallest size ReFS reliably formats —
  256 MB fails with "Size Not Supported", 1 GB with return code 40000).
  The VHDX is dynamic by default, so the on-disk footprint is only a few
  MB regardless of the logical size.

Tests run strictly serially — `[assembly: NonParallelizable]` plus
`.runsettings` cap workers at 1. We have one VM and one PSSession.

## Architecture

```
Infrastructure/
├── PowerShellRunner     — host-side: spawns powershell.exe, JSON round-trip
├── E2EConfig            — loads .vm-creds.json, locates repo + helpers
├── MsiArtefact          — globs installer/bin/Release/*.msi, SHA-256
├── VmHost               — host-side Hyper-V wrappers (Restore-, Start-, Stop-, Checkpoint-)
├── GuestSession         — wraps every call in Invoke-Command -VMName + strips remoting metadata
├── GuestFs              — Test-Path / Get-Content / Get-Command in the guest
├── GuestService         — Get-CimInstance Win32_Service assertions
├── GuestProcess         — Start-Process -Wait in the guest, capture exit/stdout/stderr
├── MsiInstaller         — msiexec /i and /x, /qn, /l*v
├── InstalledCheckpoint  — naming + staging paths for per-MSI snapshot
├── E2EFixtureBase       — [OneTimeSetUp] restores snapshot, boots, opens GuestSession
├── InstalledFixtureBase — adds CheckpointName = installed-clean@<sha8> (Phase B base)
└── Vhmgr                — `vhmgr.exe` invocation helper (absolute path, redirected I/O)
```

The C# → PowerShell bridge spawns `powershell.exe -File <tempscript.ps1>`
per call. We avoid Microsoft.PowerShell.SDK (~70 MB of assemblies, brittle
to host PS version) and pwsh 7 (PowerShell Direct works best under WinPS
5.1). Each script body is wrapped to:
1. Dot-source `tests/e2e/lib/Helpers.ps1` (so `Wait-VmReady` etc. are in scope).
2. Set `$ErrorActionPreference='Stop'`.
3. Run the user script inside `try`/`catch`.
4. Emit either the result as JSON (`-Depth 8 -Compress`) or an error envelope.

Guest invocations additionally strip `PSComputerName` / `RunspaceId` /
`PSShowComputerName` from returned PSObjects so `ConvertTo-Json` doesn't
turn `hostname` (a string) into `{"PSComputerName":"…"}`.

## Skipped scenarios (deferred)

Documented here so the gap is visible at code-review time:

### Phase A — installer

* **ProgramData purge policy** — `logs/` and `appsettings.json` lifecycle
  on uninstall is not yet codified; WiX default leaves user data in place.

### Phase B — CLI

* **`publish` — multi-parent trees** — `Publish_Tests` covers the
  single-parent / two-child case. A tree with multiple parent generations
  (parent → child-A → grandchild-B) is not exercised; the merge semantics
  for deeper chains are untested.
* **Defender exclusion removal on delete** — the CLI (and service) have no
  `RemoveExclusionAsync` path; deleting a VHDX does not remove the Defender
  exclusion entry. A follow-up should add `Remove-MpPreference` to
  `DeleteCommand` (and `CleanupCommand`) and assert the path is gone after
  deletion. `DefenderExclusion_Tests` today covers only the *add* half.
* **`status` `Attached: True`** — empirically the service reports
  `Attached: False` for managed children once `create --parent` returns (the OpenVirt
  handle is closed; the OS volume mount survives independently). This
  may be a CLI/service contract bug — the line was previously expected
  to report True. Worth a follow-up with the service team; for now the
  test asserts only that status fills in Mount path / Parent / Volume GUID.

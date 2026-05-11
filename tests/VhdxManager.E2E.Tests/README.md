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

| Fixture | Checkpoint | First-run cost | Subsequent runs |
|---|---|---|---|
| `InstalledCleanCheckpointFixture` (Order -1) | `pre-install-clean` → install → `installed-clean@<sha8>` | ~3 min | no-op |
| `Installer_Tests` (Order 1) | `pre-install-clean` | ~2 min | ~2 min |
| `Uninstall_Tests` (Order 2) | `installed-clean@<sha8>` | ~1.5 min | ~1.5 min |

The installer test asserts these files/dirs exist post-install (mirrors the
WiX component layout — keep in sync when the installer changes):

| Path | What |
|---|---|
| `C:\Program Files\VhdxManager\Service\VhdxManager.Service.exe` | Service binary |
| `C:\Program Files\VhdxManager\Service\appsettings.json` | Service config (ships with the binary) |
| `C:\Program Files\VhdxManager\Cli\vhmgr.exe` | CLI on PATH |
| `C:\ProgramData\VhdxManager\logs` | Service log directory |

Tests run strictly serially — `[assembly: NonParallelizable]` plus
`.runsettings` cap workers at 1. We have one VM and one PSSession.

## Architecture

```
Infrastructure/
├── PowerShellRunner    — host-side: spawns powershell.exe, JSON round-trip
├── E2EConfig           — loads .vm-creds.json, locates repo + helpers
├── MsiArtefact         — globs installer/bin/Release/*.msi, SHA-256
├── VmHost              — host-side Hyper-V wrappers (Restore-, Start-, Stop-, Checkpoint-)
├── GuestSession        — wraps every call in Invoke-Command -VMName + strips remoting metadata
├── GuestFs             — Test-Path / Get-Content / Get-Command in the guest
├── GuestService        — Get-CimInstance Win32_Service assertions
├── GuestProcess        — Start-Process -Wait in the guest, capture exit/stdout/stderr
├── MsiInstaller        — msiexec /i and /x, /qn, /l*v
├── InstalledCheckpoint — naming + staging paths for per-MSI snapshot
└── E2EFixtureBase      — [OneTimeSetUp] restores snapshot, boots, opens GuestSession
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

* **Idempotent reinstall** — install over install. Need to confirm WiX
  behavior (uninstall-then-install vs upgrade vs error).
* **MSI repair** — `msiexec /fp`.
* **PATH cleanup after uninstall** — depends on reboot semantics for the
  machine PATH; needs a reboot-and-recheck step.
* **ProgramData purge policy** — `logs/` and `appsettings.json` lifecycle
  on uninstall is not yet codified.
* **Defender exclusions** — added by the *service runtime* (not the
  installer), so they belong in Phase B with the `init`/`create` verbs.

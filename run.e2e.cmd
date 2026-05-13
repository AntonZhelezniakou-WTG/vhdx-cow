@echo off
setlocal

REM ====================================================================
REM  run.e2e.cmd
REM
REM  Runs the full E2E test suite against a Hyper-V VM.
REM
REM  Prerequisites (all checked at runtime by the fixtures; any missing
REM  prerequisite surfaces as Skipped, not Failed):
REM    1. Hyper-V role enabled on this machine.
REM    2. VM `VhdxManagerE2E` exists — run tests\e2e\Bootstrap-VM.ps1 once.
REM    3. MSI is built — run build.release.cmd or:
REM         dotnet build installer\VhdxManager.Installer.wixproj -c Release
REM    4. Elevated prompt (Hyper-V cmdlets require admin).
REM
REM  All tests run serially (MaxCpuCount=1 in .runsettings + [NonParallelizable]
REM  at the assembly level) — the VM's single PSSession cannot be shared.
REM
REM  Optional: pass extra `dotnet test` arguments after the script name,
REM  e.g.:  run.e2e.cmd --logger "console;verbosity=detailed"
REM         run.e2e.cmd --filter "Category=E2E-Smoke"
REM ====================================================================

echo.
echo === VhdxManager E2E Tests ===
echo.

dotnet test "%~dp0tests\VhdxManager.E2E.Tests\VhdxManager.E2E.Tests.csproj" ^
    --filter "Category=E2E" ^
    --logger "console;verbosity=normal" ^
    %*

exit /b %ERRORLEVEL%

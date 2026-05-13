@echo off
setlocal

REM ====================================================================
REM  build.release.cmd
REM
REM  Produces a fresh, version-bumped MSI in installer\bin\Release\.
REM
REM  Workflow:
REM    1. scripts\Bump-Version.ps1 increments the patch component of
REM       <DefaultVersion> in installer\VhdxManager.Installer.wixproj and
REM       writes the new value back, so the .wixproj is always the single
REM       source of truth for the release version.
REM    2. `dotnet publish` for Service + CLI is invoked with the new
REM       /p:Version so the embedded EXE FileVersion matches the MSI's
REM       ProductVersion. This is what makes MajorUpgrade actually
REM       replace files at install time — Windows Installer's default
REM       REINSTALLMODE compares FileVersion, so the new binaries must
REM       be strictly higher than the on-disk install.
REM    3. `dotnet build` for the wixproj packages the freshly-published
REM       binaries with the same /p:Version (drives ProductVersion and
REM       the MSI output filename).
REM
REM  CI note: the GitHub Actions release workflow does NOT call this
REM  script — it derives the version from the pushed git tag and passes
REM  /p:Version directly. This script is for local dev iterations where
REM  you need a new MSI on each run (e.g. to manually test the
REM  installed → installed-newer upgrade path).
REM ====================================================================

REM Capture the new version printed by the bump script.
for /f "tokens=*" %%V in ('pwsh -NoProfile -ExecutionPolicy Bypass -File "%~dp0scripts\Bump-Version.ps1"') do set VERSION=%%V
if "%VERSION%"=="" (
    echo Failed to bump version - check scripts\Bump-Version.ps1 output.
    exit /b 1
)

echo.
echo === Building VhdxManager %VERSION% ===
echo.

dotnet publish src\VhdxManager.Service\VhdxManager.Service.csproj -c Release -r win-x64 --self-contained true -o publish\service /p:Version=%VERSION%
if errorlevel 1 exit /b 1

dotnet publish src\VhdxManager.Cli\VhdxManager.Cli.csproj -c Release -r win-x64 --self-contained true -o publish\cli /p:Version=%VERSION%
if errorlevel 1 exit /b 1

dotnet build installer\VhdxManager.Installer.wixproj -c Release /p:Version=%VERSION%
if errorlevel 1 exit /b 1

echo.
echo === Done: installer\bin\Release\VhdxManager-%VERSION%.msi ===
echo.
echo  installer\VhdxManager.Installer.wixproj was bumped to %VERSION%.
echo  - Commit it to release that version.
echo  - `git restore installer\VhdxManager.Installer.wixproj` to revert.

endlocal

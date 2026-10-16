# Building Downhole Studio

## Requirements
* .NET SDK 10.0.401 (pinned in `global.json`). `scripts/setup-dev.sh` installs it into `.tools/dotnet`
  without touching the system; then `source .tools/env.sh`.
* Docker — for the integration tests (they start a temporary `postgres:17-alpine` container named
  `dhs-pgtest-*` and remove it afterwards) and, on Linux, to compile the installer with Inno Setup
  under Wine (`amake/innosetup` image).
* On Windows instead: Inno Setup 6 (`ISCC.exe`) and, for signed builds, `signtool.exe`.

## One command
```bash
scripts/build.sh                 # build, test, validate, publish win-x64, compile the installer
scripts/build.sh --skip-tests    # packaging only
scripts/build.sh --version 1.0.1 --refresh-sample
```
```powershell
powershell -ExecutionPolicy Bypass -File scripts\build.ps1 -Version 1.0.0 [-Sign -CertificateThumbprint <thumbprint>]
```
Output: `artifacts/installer/DownholeStudio-Setup-<version>.exe` and `.sha256`.

## Step by step
```bash
source .tools/env.sh
dotnet build DownholeStudio.sln -c Release
dotnet test tests/DownholeStudio.Analysis.Tests          # and the other test projects, one at a time
dotnet run --project tools/DownholeStudio.Cli -- validate --out docs/validation
dotnet run --project tools/DownholeStudio.Cli -- sample --out "packaging/samples/Downhole Studio Sample.dhsx"
dotnet publish src/DownholeStudio.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none -o artifacts/publish/win-x64
docker run --rm -v "$PWD:/work" -w /work/packaging/windows amake/innosetup:latest "/DAppVersion=1.0.0" installer.iss
```
The application publishes self-contained (no .NET installation needed on the customer's computer)
with ReadyToRun pre-compilation for fast start-up. Cross-compiling for Windows on Linux is
supported.

## Running from source
`dotnet run --project src/DownholeStudio.App` (Windows; on Linux it runs with X11 for development).
Environment variables: `DOWNHOLE_STUDIO_DATA` (alternative data folder), `DOWNHOLE_STUDIO_DEBUG=1`
(debug logging).

## Projects
See [ARCHITECTURE.md](ARCHITECTURE.md). Warnings are errors (`Directory.Build.props`); package
versions are pinned centrally (`Directory.Packages.props`).

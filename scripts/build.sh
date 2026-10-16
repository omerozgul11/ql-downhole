#!/usr/bin/env bash
# Builds, tests and packages Downhole Studio for Windows from Linux.
#   scripts/build.sh [--version 1.0.0] [--skip-tests] [--refresh-sample] [--no-installer]
# Output: artifacts/installer/DownholeStudio-Setup-<version>.exe and its .sha256.
# Requires: .NET SDK (scripts/setup-dev.sh) and Docker (Inno Setup runs in the amake/innosetup image under Wine).
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
[ -f .tools/env.sh ] || scripts/setup-dev.sh
source .tools/env.sh

VERSION="$(grep -oP '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)"
TESTS=1; SAMPLE=0; INSTALLER=1
while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="$2"; shift ;;
    --skip-tests) TESTS=0 ;;
    --refresh-sample) SAMPLE=1 ;;
    --no-installer) INSTALLER=0 ;;
    *) echo "Unknown option $1"; exit 1 ;;
  esac
  shift
done
echo "== Downhole Studio $VERSION"

dotnet build DownholeStudio.sln -c Release -p:Version="$VERSION"
if [ "$TESTS" = 1 ]; then
  # Run suites one at a time: the integration suite starts its own PostgreSQL container.
  for t in SignalProcessing Formats Analysis Core Licensing; do
    dotnet test "tests/DownholeStudio.$t.Tests" -c Release --no-build
  done
  dotnet test tests/DownholeStudio.UiTests -c Release --no-build
  dotnet test tests/DownholeStudio.IntegrationTests -c Release --no-build
  dotnet run --project tools/DownholeStudio.Cli -c Release --no-build -- validate --out docs/validation
fi

if [ "$SAMPLE" = 1 ] || [ ! -f "packaging/samples/Downhole Studio Sample.dhsx" ]; then
  mkdir -p packaging/samples
  rm -f "packaging/samples/Downhole Studio Sample.dhsx"
  dotnet run --project tools/DownholeStudio.Cli -c Release --no-build -- sample --out "packaging/samples/Downhole Studio Sample.dhsx"
fi

rm -rf artifacts/publish/win-x64
dotnet publish src/DownholeStudio.App -c Release -r win-x64 --self-contained true -p:PublishReadyToRun=true -p:DebugType=none \
  -p:Version="$VERSION" -o artifacts/publish/win-x64
find artifacts/publish/win-x64 -name '*.pdb' -delete

if [ "$INSTALLER" = 1 ]; then
  mkdir -p artifacts/installer
  docker run --rm -v "$ROOT:/work" -w /work/packaging/windows amake/innosetup:latest \
    "/DAppVersion=$VERSION" installer.iss
  SETUP="artifacts/installer/DownholeStudio-Setup-$VERSION.exe"
  (cd artifacts/installer && sha256sum "$(basename "$SETUP")" > "$(basename "$SETUP").sha256")
  echo "== Installer: $SETUP ($(stat -c %s "$SETUP") bytes)"
  cat "$SETUP.sha256"
  echo "Unsigned build. For a signed release, build on Windows with scripts/build.ps1 -Sign (see DEPLOYMENT.md)."
fi

#!/usr/bin/env bash
# Installs the pinned .NET SDK into .tools/dotnet (no system changes). Usage: scripts/setup-dev.sh
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
SDK_VERSION="$(python3 -c "import json;print(json.load(open('$ROOT/global.json'))['sdk']['version'])")"
mkdir -p "$ROOT/.tools"
if [ ! -x "$ROOT/.tools/dotnet/dotnet" ] || ! "$ROOT/.tools/dotnet/dotnet" --list-sdks | grep -q "^$SDK_VERSION"; then
  curl -sSL https://dot.net/v1/dotnet-install.sh -o "$ROOT/.tools/dotnet-install.sh"
  bash "$ROOT/.tools/dotnet-install.sh" --version "$SDK_VERSION" --install-dir "$ROOT/.tools/dotnet"
fi
cat > "$ROOT/.tools/env.sh" <<'ENV'
export DOTNET_ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)/dotnet"
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1 DOTNET_NOLOGO=1 DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
ENV
echo "SDK ready. Run: source .tools/env.sh"

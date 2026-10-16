#!/usr/bin/env bash
# Optionally publishes the built installer as a versioned GitHub release as well. Customers download from
# download/DownholeStudio-Setup.exe in the repository (the README's single link); see DEPLOYMENT.md.
#   scripts/publish-release.sh [--version 1.0.0] [--notes-file notes.md] [--prerelease]
# Reads GITHUB_REPOSITORY and GITHUB_TOKEN from .env (see .env.example). The token needs
# "Contents: read and write" on the repository. Build first with scripts/build.sh or scripts/build.ps1.
set -euo pipefail
ROOT="$(cd "$(dirname "$0")/.." && pwd)"
cd "$ROOT"
if [ -f .env ]; then set -a; source .env; set +a; fi

VERSION="$(grep -oP '(?<=<VersionPrefix>)[^<]+' Directory.Build.props)"
NOTES=""; PRERELEASE=false
while [ $# -gt 0 ]; do
  case "$1" in
    --version) VERSION="$2"; shift ;;
    --notes-file) NOTES="$(cat "$2")"; shift ;;
    --prerelease) PRERELEASE=true ;;
    *) echo "Unknown option $1"; exit 1 ;;
  esac
  shift
done

: "${GITHUB_REPOSITORY:?Set GITHUB_REPOSITORY (owner/repo) in .env}"
: "${GITHUB_TOKEN:?Set GITHUB_TOKEN in .env}"
SETUP="artifacts/installer/DownholeStudio-Setup-$VERSION.exe"
[ -f "$SETUP" ] || { echo "Missing $SETUP; build it first."; exit 1; }
[ -f "$SETUP.sha256" ] || (cd artifacts/installer && sha256sum "$(basename "$SETUP")" > "$(basename "$SETUP").sha256")
HASH="$(cut -d' ' -f1 "$SETUP.sha256")"
SIGNED="unsigned"
if command -v osslsigncode >/dev/null && osslsigncode verify "$SETUP" >/dev/null 2>&1; then SIGNED="Authenticode-signed"; fi

API="https://api.github.com/repos/$GITHUB_REPOSITORY"
AUTH=(-H "Authorization: Bearer $GITHUB_TOKEN" -H "Accept: application/vnd.github+json" -H "X-GitHub-Api-Version: 2022-11-28")
TAG="v$VERSION"
[ -n "$NOTES" ] || NOTES="Downhole Studio $VERSION for Windows (64-bit)."
BODY="$NOTES

**Install:** download \`DownholeStudio-Setup.exe\` and run it. The installer is $SIGNED.

SHA-256: \`$HASH\`"

if curl -sf "${AUTH[@]}" "$API/releases/tags/$TAG" -o /dev/null; then
  echo "Release $TAG already exists; not changing it. Delete it on GitHub to republish."; exit 1
fi
PAYLOAD="$(python3 -c 'import json,sys; print(json.dumps({"tag_name":sys.argv[1],"name":"Downhole Studio "+sys.argv[2],"body":sys.argv[3],"prerelease":sys.argv[4]=="true","make_latest":"false" if sys.argv[4]=="true" else "true"}))' "$TAG" "$VERSION" "$BODY" "$PRERELEASE")"
RELEASE="$(curl -sf "${AUTH[@]}" -X POST "$API/releases" -d "$PAYLOAD")"
UPLOAD="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["upload_url"].split("{")[0])' <<<"$RELEASE")"
URL="$(python3 -c 'import json,sys; print(json.load(sys.stdin)["html_url"])' <<<"$RELEASE")"

upload() {
  curl -sf "${AUTH[@]}" -H "Content-Type: $3" --data-binary @"$1" "$UPLOAD?name=$2" -o /dev/null
  echo "Uploaded $2"
}
upload "$SETUP" "DownholeStudio-Setup.exe" "application/vnd.microsoft.portable-executable"
sed "s/DownholeStudio-Setup-$VERSION.exe/DownholeStudio-Setup.exe/" "$SETUP.sha256" > "artifacts/installer/DownholeStudio-Setup.exe.sha256"
upload "artifacts/installer/DownholeStudio-Setup.exe.sha256" "DownholeStudio-Setup.exe.sha256" "text/plain"
echo "Published $URL"

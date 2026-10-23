#!/usr/bin/env bash
# Stops the local test environment. --reset also deletes its database, licences, projects and settings.
set -euo pipefail
cd "$(dirname "$0")"
if [ "${1:-}" = "--reset" ]; then docker compose down --volumes --remove-orphans; rm -f .files-ready; else docker compose stop; fi

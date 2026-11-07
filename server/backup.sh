#!/usr/bin/env bash
# Saves the licensing database (customers, licences, activations, audit log) outside the repository.
#   server/backup.sh [folder]     default: ~/.config/quakelogic/downhole-licensing/backups; the newest 30 are kept
set -euo pipefail
cd "$(dirname "$0")"
DIR="${1:-$HOME/.config/quakelogic/downhole-licensing/backups}"
mkdir -p "$DIR"; chmod 700 "$DIR"
FILE="$DIR/dhs-licensing-$(date -u +%Y%m%dT%H%M%SZ).sql.gz"
docker compose exec -T postgres pg_dump -U dhs -d dhs_licensing | gzip > "$FILE"
[ "$(gzip -dc "$FILE" | grep -c 'PostgreSQL database dump complete')" = 1 ] || { echo "Backup is incomplete: $FILE" >&2; exit 1; }
chmod 600 "$FILE"
ls -1t "$DIR"/dhs-licensing-*.sql.gz | tail -n +31 | xargs -r rm -f
echo "$FILE"

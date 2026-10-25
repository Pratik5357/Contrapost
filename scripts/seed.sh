#!/usr/bin/env bash
# Seeds the local docker compose database with demo data (db/seed/dev.sql).
set -euo pipefail
root="$(cd "$(dirname "$0")/.." && pwd)"
docker compose -f "$root/deploy/docker-compose.yml" exec -T postgres \
  psql -U contrapost -d contrapost -v ON_ERROR_STOP=1 -q < "$root/db/seed/dev.sql"
echo "seeded: X-Client-Id 11111111-1111-1111-1111-111111111111"

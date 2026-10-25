#!/usr/bin/env bash
# Applies db/schema.sql to a throwaway PostgreSQL container and runs the guard tests.
# Needs only Docker; files are piped in, not mounted. Usage: scripts/db-test.sh [postgres image tag, default 16]
set -euo pipefail

root="$(cd "$(dirname "$0")/.." && pwd)"
image="postgres:${1:-16}"
name="contrapost-db-test-$$"

cleanup() { docker rm -f "$name" >/dev/null 2>&1 || true; }
trap cleanup EXIT

docker run -d --name "$name" \
  -e POSTGRES_PASSWORD=test -e POSTGRES_DB=contrapost \
  "$image" >/dev/null

# During init the entrypoint runs a temporary server on the Unix socket only,
# then restarts it. TCP answers only once the real server is up.
for _ in $(seq 1 60); do
  if docker exec "$name" pg_isready -h 127.0.0.1 -U postgres -q; then break; fi
  sleep 0.5
done

psql() { docker exec -i "$name" psql -U postgres -d contrapost -v ON_ERROR_STOP=1 -q "$@"; }

echo "== applying schema ($image)"
psql < "$root/db/schema.sql"
echo "== running guard tests"
psql < "$root/db/tests/guards.sql" 2>&1 | sed -E 's/^(psql:[^ ]+ )?NOTICE:  //'

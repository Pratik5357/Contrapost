#!/usr/bin/env bash
# Runs one k6 scenario (design doc §13.2) against the local compose stack.
#   tests/load/run.sh [uniform|hot_account|retry_storm|mixed]
# Light by default (VUS=5, DURATION=30s). Heavier runs are opt-in: VUS=32 DURATION=2m tests/load/run.sh uniform
# Needs Docker and k6. Starts only the ledger services, not the observability stack, unless OBSERVE=1.
set -euo pipefail

root="$(cd "$(dirname "$0")/../.." && pwd)"
scenario="${1:-mixed}"
compose() { docker compose -f "$root/deploy/docker-compose.yml" "$@"; }

if [ "${OBSERVE:-0}" = 1 ]; then
  compose up -d --wait >/dev/null 2>&1
else
  compose up -d --wait api worker consumers >/dev/null 2>&1
fi

# A dedicated client whose rate limit won't interfere with the measurement.
key="$("$root/scripts/seed.sh" "load-$scenario" 2>/dev/null | sed -n 's/^api_key: //p')"
[ -n "$key" ] || { echo "could not create an API key" >&2; exit 1; }
compose exec -T postgres psql -U contrapost -d contrapost -qtAc \
  "UPDATE api_clients SET rate_limit_rpm = 100000000 WHERE key_prefix = '${key%%.*}'" >/dev/null

mkdir -p "$root/tests/load/results"
k6 run --quiet \
  -e BASE_URL=http://localhost:8080 -e API_KEY="$key" -e SCENARIO="$scenario" \
  -e VUS="${VUS:-5}" -e DURATION="${DURATION:-30s}" \
  --summary-export "$root/tests/load/results/$scenario.json" \
  "$root/tests/load/ledger.js"

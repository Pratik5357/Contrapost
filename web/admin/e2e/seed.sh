#!/usr/bin/env bash
# Seeds the running Compose stack with what the smoke tests walk through: an account with more than
# one page of entries (wallet:smoke), a reversal, an open hold, and a reconciliation run.
set -euo pipefail
root="$(cd "$(dirname "$0")/../../.." && pwd)"
api=${API_URL:-http://localhost:8080}
compose() { docker compose -f "$root/deploy/docker-compose.yml" "$@"; }

key="$("$root/scripts/seed.sh" smoke 2>/dev/null | sed -n 's/^api_key: //p')"
[ -n "$key" ] || { echo "could not create an API key" >&2; exit 1; }

post() { # path body -> response
  curl -sf -H "Authorization: Bearer $key" -H 'Content-Type: application/json' \
    -H "Idempotency-Key: $(uuidgen 2>/dev/null || cat /proc/sys/kernel/random/uuid)" "$api$1" -d "$2"
}
id() { python3 -c 'import sys, json; print(json.load(sys.stdin)["id"])'; }
account() { curl -sf -H "Authorization: Bearer $key" -H 'Content-Type: application/json' "$api/v1/accounts" -d "$1" | id; }
transfer() { post /v1/transfers "{\"currency\":\"INR\",\"legs\":[{\"account_id\":\"$1\",\"direction\":\"debit\",\"amount\":$3},{\"account_id\":\"$2\",\"direction\":\"credit\",\"amount\":$3}]}"; }

funding=$(account '{"code":"funding:INR","currency":"INR","normal_side":"debit","prevent_overdraft":false}')
wallet=$(account '{"code":"wallet:smoke","currency":"INR","normal_side":"credit"}')
shop=$(account '{"code":"merchant:smoke","currency":"INR","normal_side":"credit"}')

transfer "$funding" "$wallet" 1000000 >/dev/null
for i in $(seq 1 25); do transfer "$wallet" "$shop" $((i * 100)) >/dev/null; done # with the rest below, 28 entries: two pages
payment=$(transfer "$wallet" "$shop" 5000 | id)
post "/v1/transfers/$payment/reversal" '{}' >/dev/null
post /v1/holds "{\"debit_account_id\":\"$wallet\",\"credit_account_id\":\"$shop\",\"currency\":\"INR\",\"amount\":2500,\"expires_in_seconds\":3600}" >/dev/null

compose run --rm migrate reconcile >/dev/null
echo "seeded wallet:smoke ($wallet)"

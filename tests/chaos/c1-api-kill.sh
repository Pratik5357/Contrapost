#!/usr/bin/env bash
# C1: SIGKILL the API under load, then restart it. Clients retry each transfer with the same
# idempotency key until it returns 201. Expected: every transfer lands exactly once (a request that
# committed before the kill is replayed, not posted again) and no balance drifts.
# Env: KILLS (default 3), DOWN_SECONDS (default 5), LOAD_SECONDS (default 60), PARALLEL (default 4).
source "$(dirname "$0")/lib.sh"
kills=${KILLS:-3}; down=${DOWN_SECONDS:-5}; load=${LOAD_SECONDS:-60}; parallel=${PARALLEL:-4}
wallets=20; funding=100000000

wait_ready() { until curl -sf -m 2 "$api/health/ready" >/dev/null; do sleep 0.2; done; }

fresh_stack
setup_wallets "$wallets"

log "load: ${load}s with $parallel retrying clients; $kills API kills, each down for ${down}s"
run_retrying_load "$load" "$parallel" &
load_pid=$!

gap=$(( load / (kills + 1) )); outages=()
for k in $(seq 1 "$kills"); do
  sleep "$gap"
  compose kill -s SIGKILL api >/dev/null 2>&1
  killed_at=$(date +%s)
  log "kill $k: API killed"
  sleep "$down"
  compose start api >/dev/null 2>&1
  wait_ready
  outages+=($(( $(date +%s) - killed_at )))
  log "kill $k: API ready again after ${outages[${#outages[@]}-1]}s"
done

wait "$load_pid"
wait_settled 300 || true

total=$(wc -l < "$results/load" | tr -d ' ')
retried=$(awk '$2 > 1' "$results/load" | wc -l | tr -d ' ')
replays=$(awk '$3 == "replay"' "$results/load" | wc -l | tr -d ' ')
echo
log "results: $total transfers, $retried needed retries, $replays answered by an idempotent replay, outages ${outages[*]}s"

check "transfers posted vs transfers the clients sent" \
  "$(sql "SELECT count(*) FROM transactions WHERE kind = 'transfer'")" "$((total + wallets))"
check "money held by the wallets (credits - debits)" \
  "$(sql "SELECT sum(b.credits_posted - b.debits_posted) FROM account_balances b JOIN accounts a ON a.id = b.account_id
          WHERE a.code LIKE 'wallet:%'")" "$((wallets * funding))"
verify

#!/usr/bin/env bash
# C4: add latency between the app and Postgres (Toxiproxy) under load, then remove it.
# Expected: latency rises while the toxic is on; every transfer still lands exactly once, no
# deadlocks, no balance drift. The API, Worker and Consumers all go through the proxy.
# Env: LATENCY_MS (default 200), BASELINE_SECONDS (default 20), LATENCY_SECONDS (default 40),
#      RECOVERY_SECONDS (default 20), PARALLEL (default 4).
source "$(dirname "$0")/lib.sh"
COMPOSE_OVERLAY="$root/tests/chaos/toxiproxy.compose.yml"
latency=${LATENCY_MS:-200}; parallel=${PARALLEL:-4}
wallets=20; funding=100000000
toxiproxy=http://localhost:8474

deadlocks() { sql "SELECT deadlocks FROM pg_stat_database WHERE datname = 'contrapost'"; }

phase() { # name seconds
  log "$1: ${2}s of load with $parallel retrying clients"
  run_retrying_load "$2" "$parallel"
  cp "$results/load" "$results/phase.$1"
}

# name: transfers, transfers/s, p50 and p95 of the final attempt, transfers that needed retries
summarize() { # name seconds
  python3 - "$results/phase.$1" "$1" "$2" <<'EOF'
import sys
rows = [line.split() for line in open(sys.argv[1]) if line.strip()]
secs = sorted(float(r[3]) * 1000 for r in rows)
pct = lambda p: secs[min(len(secs) - 1, int(p * len(secs)))] if secs else 0
retried = sum(1 for r in rows if int(r[1]) > 1)
print(f"  {sys.argv[2]:<9} {len(rows):>6} transfers  {len(rows) / float(sys.argv[3]):6.1f}/s"
      f"  p50 {pct(0.50):6.0f} ms  p95 {pct(0.95):6.0f} ms  retried {retried}")
EOF
}

fresh_stack
setup_wallets "$wallets"
wait_settled 60
deadlocks_before=$(deadlocks)

phase baseline "${BASELINE_SECONDS:-20}"

log "adding ${latency} ms latency to Postgres responses"
curl -sf -o /dev/null "$toxiproxy/proxies/postgres/toxics" -H 'Content-Type: application/json' \
  -d "{\"name\":\"latency\",\"type\":\"latency\",\"stream\":\"downstream\",\"attributes\":{\"latency\":$latency}}"
phase latency "${LATENCY_SECONDS:-40}"

log "removing the latency"
curl -sf -o /dev/null -X DELETE "$toxiproxy/proxies/postgres/toxics/latency"
phase recovery "${RECOVERY_SECONDS:-20}"

wait_settled 300 || true
total=$(wc -l < "$results/status" | tr -d ' ')
echo
log "results:"
summarize baseline "${BASELINE_SECONDS:-20}"
summarize latency "${LATENCY_SECONDS:-40}"
summarize recovery "${RECOVERY_SECONDS:-20}"

check "deadlocks detected by Postgres" "$(( $(deadlocks) - deadlocks_before ))" 0
check "transfers posted vs transfers the clients sent" \
  "$(sql "SELECT count(*) FROM transactions WHERE kind = 'transfer'")" "$((total + wallets))"
check "money held by the wallets (credits - debits)" \
  "$(sql "SELECT sum(b.credits_posted - b.debits_posted) FROM account_balances b JOIN accounts a ON a.id = b.account_id
          WHERE a.code LIKE 'wallet:%'")" "$((wallets * funding))"
verify

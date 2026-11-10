# Shared helpers for the chaos runs (design doc §13.3). Source this file; it needs Docker, curl and python3.
set -euo pipefail

root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"
api=http://localhost:8080
results="$(mktemp -d)"

# A run can add a Compose overlay by setting COMPOSE_OVERLAY to its path.
compose() { docker compose -f "$root/deploy/docker-compose.yml" ${COMPOSE_OVERLAY:+-f "$COMPOSE_OVERLAY"} "$@"; }
sql() { compose exec -T postgres psql -U contrapost -d contrapost -tAq -c "$1"; }
log() { printf '[%s] %s\n' "$(date +%H:%M:%S)" "$*"; }

fresh_stack() {
  log "starting a fresh stack"
  compose down -v --remove-orphans >/dev/null 2>&1 || true
  compose up -d --build --wait --wait-timeout 300 >/dev/null 2>&1
  KEY="$("$root/scripts/seed.sh" chaos 2>/dev/null | sed -n 's/^api_key: //p')"
  [ -n "$KEY" ] || { echo "could not create an API key" >&2; exit 1; }
  # The runs test recovery, not the per-client rate limit (C5 covers the limiter).
  sql "UPDATE api_clients SET rate_limit_rpm = 100000000 WHERE key_prefix = '${KEY%%.*}'" >/dev/null
}

create_account() { # code normal_side prevent_overdraft
  curl -sf -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' "$api/v1/accounts" \
    -d "{\"code\":\"$1\",\"currency\":\"INR\",\"normal_side\":\"$2\",\"prevent_overdraft\":$3}" |
    python3 -c 'import sys, json; print(json.load(sys.stdin)["id"])'
}

transfer() { # idempotency-key from to amount -> prints the HTTP status
  curl -s -o /dev/null -w '%{http_code}\n' -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' \
    -H "Idempotency-Key: $1" "$api/v1/transfers" \
    -d "{\"currency\":\"INR\",\"legs\":[{\"account_id\":\"$2\",\"direction\":\"debit\",\"amount\":$4},{\"account_id\":\"$3\",\"direction\":\"credit\",\"amount\":$4}]}"
}

setup_wallets() { # count
  local funding; funding="$(create_account funding:INR debit false)"
  WALLETS=()
  for i in $(seq 1 "$1"); do
    WALLETS+=("$(create_account "wallet:$i" credit true)")
    transfer "fund-$i" "$funding" "${WALLETS[${#WALLETS[@]}-1]}" 100000000 >/dev/null
  done
  log "created $1 funded wallets"
}

# Random transfers between the wallets from <parallel> loops for <seconds>; statuses go to $results/status.
run_load() { # seconds parallel
  local deadline=$(( $(date +%s) + $1 ))
  for p in $(seq 1 "$2"); do
    (
      n=0
      while [ "$(date +%s)" -lt "$deadline" ]; do
        from=${WALLETS[$((RANDOM % ${#WALLETS[@]}))]}
        to=${WALLETS[$((RANDOM % ${#WALLETS[@]}))]}
        [ "$from" = "$to" ] && continue
        n=$((n + 1))
        transfer "load-$p-$n-$RANDOM" "$from" "$to" $((RANDOM % 1000 + 1))
      done >> "$results/status.$p"
    ) &
  done
  wait
  cat "$results"/status.* > "$results/status"
}

# Like run_load, but each transfer keeps its idempotency key across retries (connection refused,
# 5xx, or 409 request-in-progress) for up to 60 s. One line per transfer in $results/load: final
# status, attempts, whether the 201 was a replay, and the final attempt's seconds. Statuses are
# appended to $results/status, so several calls add up.
run_retrying_load() { # seconds parallel
  local deadline=$(( $(date +%s) + $1 ))
  for p in $(seq 1 "$2"); do
    (
      n=0; headers="$results/headers.$p"
      while [ "$(date +%s)" -lt "$deadline" ]; do
        from=${WALLETS[$((RANDOM % ${#WALLETS[@]}))]}
        to=${WALLETS[$((RANDOM % ${#WALLETS[@]}))]}
        [ "$from" = "$to" ] && continue
        n=$((n + 1)); key="load-$p-$n-$RANDOM"; amount=$((RANDOM % 1000 + 1))
        attempts=0; give_up=$(( $(date +%s) + 60 ))
        while :; do
          attempts=$((attempts + 1))
          status=$(curl -s -m 10 -D "$headers" -o /dev/null -w '%{http_code} %{time_total}' \
            -H "Authorization: Bearer $KEY" -H 'Content-Type: application/json' -H "Idempotency-Key: $key" \
            "$api/v1/transfers" \
            -d "{\"currency\":\"INR\",\"legs\":[{\"account_id\":\"$from\",\"direction\":\"debit\",\"amount\":$amount},{\"account_id\":\"$to\",\"direction\":\"credit\",\"amount\":$amount}]}" ||
            true)
          seconds=${status#* }; status=${status%% *}
          case "$status" in 000|409|5??) [ "$(date +%s)" -lt "$give_up" ] && { sleep 0.2; continue; } ;; esac
          break
        done
        replayed=$(grep -qi '^Idempotent-Replayed: true' "$headers" && echo replay || echo fresh)
        echo "$status $attempts $replayed $seconds"
      done > "$results/load.$p"
    ) &
  done
  wait
  cat "$results"/load.* > "$results/load"; rm "$results"/load.*
  cut -d' ' -f1 "$results/load" >> "$results/status"
}

unpublished() { sql "SELECT count(*) FROM outbox_messages WHERE published_at IS NULL"; }
oldest_unpublished_age() { sql "SELECT coalesce(extract(epoch FROM now() - min(created_at))::int, 0) FROM outbox_messages WHERE published_at IS NULL"; }

# Consumers have caught up when each has handled every published money event.
posted_events() { sql "SELECT count(*) FROM outbox_messages WHERE type IN ('transfer.posted', 'transfer.reversed', 'hold.captured')"; }
handled() { sql "SELECT count(*) FROM inbox_messages WHERE consumer = '$1'"; }

wait_settled() { # timeout-seconds
  local deadline=$(( $(date +%s) + $1 ))
  while :; do
    local events; events="$(posted_events)"
    if [ "$(unpublished)" = 0 ] && [ "$(handled statements)" = "$events" ] && [ "$(handled notifier)" = "$events" ]; then
      return 0
    fi
    if [ "$(date +%s)" -ge "$deadline" ]; then
      log "not settled after $1s: unpublished=$(unpublished) events=$events statements=$(handled statements) notifier=$(handled notifier)"
      return 1
    fi
    sleep 1
  done
}

failures=0
check() { # description actual expected
  if [ "$2" = "$3" ]; then printf '  pass  %-58s %s\n' "$1" "$2"; else printf '  FAIL  %-58s got %s, expected %s\n' "$1" "$2" "$3"; failures=$((failures + 1)); fi
}

verify() {
  local events; events="$(posted_events)"
  log "verifying"
  check "API requests that did not return 201" "$(grep -vc '^201$' "$results/status" || true)" 0
  check "outbox rows still unpublished" "$(unpublished)" 0
  check "statements: events handled (exactly once after dedupe)" "$(handled statements)" "$events"
  check "notifier: events handled (exactly once after dedupe)" "$(handled notifier)" "$events"
  check "statement rows vs postings" "$(sql 'SELECT count(*) FROM account_statements')" "$(sql 'SELECT count(*) FROM postings')"
  check "accounts whose statement is incomplete" "$(sql '
    SELECT count(*) FROM account_balances b LEFT JOIN account_statement_heads h USING (account_id)
    WHERE b.entry_seq > 0 AND coalesce(h.complete_through_seq, 0) <> b.entry_seq')" 0
  check "accounts whose balance drifted from postings (I3)" "$(sql "
    SELECT count(*) FROM account_balances b CROSS JOIN LATERAL (
      SELECT coalesce(sum(amount) FILTER (WHERE direction = 'debit'), 0) d,
             coalesce(sum(amount) FILTER (WHERE direction = 'credit'), 0) c
      FROM postings p WHERE p.account_id = b.account_id) t
    WHERE b.debits_posted <> t.d OR b.credits_posted <> t.c")" 0
  [ "$failures" = 0 ] && log "PASSED" || { log "FAILED ($failures checks)"; return 1; }
}

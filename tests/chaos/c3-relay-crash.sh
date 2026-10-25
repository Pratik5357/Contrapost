#!/usr/bin/env bash
# C3: SIGKILL the relay (worker) repeatedly while it drains a backlog, aiming for mid-batch.
# Expected: nothing is lost; messages published but not yet marked are published again, and the
# consumers' inbox absorbs the duplicates. Env: KILLS (default 8), BACKLOG_SECONDS (default 30).
source "$(dirname "$0")/lib.sh"
kills=${KILLS:-8}; backlog_seconds=${BACKLOG_SECONDS:-30}

fresh_stack
setup_wallets 20
wait_settled 60

log "stopping the worker and building a backlog for ${backlog_seconds}s"
compose stop worker >/dev/null 2>&1
run_load "$backlog_seconds" 8
log "backlog: $(unpublished) unpublished outbox rows"

for k in $(seq 1 "$kills"); do
  before=$(unpublished)
  [ "$before" = 0 ] && { log "backlog drained after $((k - 1)) kills"; break; }
  compose start worker >/dev/null 2>&1
  # Kill as soon as the relay is visibly publishing, so the kill lands inside a batch.
  until [ "$(unpublished)" -lt "$before" ]; do sleep 0.05; done
  compose kill -s SIGKILL worker >/dev/null 2>&1
  log "kill $k: relay killed with $(unpublished) rows left"
done

compose start worker >/dev/null 2>&1
wait_settled 300 || true
duplicates=$(compose logs consumers 2>&1 | grep -c 'skipped duplicate delivery' || true)

echo
log "results: $(wc -l < "$results/status" | tr -d ' ') transfers, $duplicates duplicate deliveries absorbed by the inbox"
verify

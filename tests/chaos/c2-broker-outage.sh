#!/usr/bin/env bash
# C2: stop RabbitMQ under load. Expected: the API keeps accepting transfers, the outbox backs up,
# then drains once the broker returns, and every event reaches each consumer exactly once.
# Env: OUTAGE_SECONDS (default 120), LOAD_SECONDS (default OUTAGE_SECONDS + 40), PARALLEL (default 8).
source "$(dirname "$0")/lib.sh"
outage=${OUTAGE_SECONDS:-120}; load=${LOAD_SECONDS:-$((outage + 40))}; parallel=${PARALLEL:-8}

fresh_stack
setup_wallets 20

log "load: ${load}s with $parallel parallel clients; broker outage of ${outage}s starts after 15s"
run_load "$load" "$parallel" &
load_pid=$!

sleep 15
log "stopping rabbitmq"; compose stop rabbitmq >/dev/null 2>&1
max_backlog=0; max_age=0; stop_at=$(( $(date +%s) + outage ))
while [ "$(date +%s)" -lt "$stop_at" ]; do
  backlog=$(unpublished); age=$(oldest_unpublished_age)
  [ "$backlog" -gt "$max_backlog" ] && max_backlog=$backlog
  [ "$age" -gt "$max_age" ] && max_age=$age
  sleep 5
done
log "starting rabbitmq (backlog $(unpublished))"; compose start rabbitmq >/dev/null 2>&1
back_at=$(date +%s)
until [ "$(unpublished)" = 0 ] || [ $(( $(date +%s) - back_at )) -gt 300 ]; do sleep 1; done
drain=$(( $(date +%s) - back_at ))

wait "$load_pid"
wait_settled 300 || true

echo
log "results: $(wc -l < "$results/status" | tr -d ' ') transfers, max outbox backlog $max_backlog, oldest unpublished ${max_age}s, drained ${drain}s after the broker returned"
verify

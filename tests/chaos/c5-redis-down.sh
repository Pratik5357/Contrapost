#!/usr/bin/env bash
# C5: stop Redis under load. Expected (ADR-006): the rate limiter fails open, so transfers continue
# with no 429s and no errors; ledger_rate_limiter_failed_open_total rises and the
# RateLimiterFailingOpen alert fires; limiting resumes once Redis is back.
# The client gets a real limit (LIMIT_RPM) so the load runs into it while Redis is up.
# Env: LIMIT_RPM (default 300), PHASE_SECONDS (default 20), OUTAGE_SECONDS (default 30), PARALLEL (default 2).
source "$(dirname "$0")/lib.sh"
limit=${LIMIT_RPM:-300}; phase_seconds=${PHASE_SECONDS:-20}; outage=${OUTAGE_SECONDS:-30}; parallel=${PARALLEL:-2}
prometheus=http://localhost:9090

phase() { # name seconds
  log "$1: ${2}s of load with $parallel clients"
  run_load "$2" "$parallel"
  mv "$results/status" "$results/phase.$1"; rm "$results"/status.*
}
count() { grep -c "^$2\$" "$results/phase.$1" || true; } # phase status
promql() { # query -> first value, or 0
  curl -sf "$prometheus/api/v1/query" --data-urlencode "query=$1" |
    python3 -c 'import sys, json; r = json.load(sys.stdin)["data"]["result"]; print(round(float(r[0]["value"][1])) if r else 0)'
}
alert_state() {
  curl -sf "$prometheus/api/v1/alerts" |
    python3 -c 'import sys, json; a = [x["state"] for x in json.load(sys.stdin)["data"]["alerts"] if x["labels"]["alertname"] == "RateLimiterFailingOpen"]; print(a[0] if a else "inactive")'
}

fresh_stack
setup_wallets 20
wait_settled 60
sql "UPDATE api_clients SET rate_limit_rpm = $limit WHERE key_prefix = '${KEY%%.*}'" >/dev/null
log "client limit set to $limit requests a minute"

phase before "$phase_seconds"

log "stopping redis"; compose stop redis >/dev/null 2>&1
phase outage "$outage"
# The alert needs the counter exported (5 s), scraped (5 s) and two rule evaluations (15 s apart).
deadline=$(( $(date +%s) + 60 ))
until [ "$(alert_state)" = firing ] || [ "$(date +%s)" -ge "$deadline" ]; do sleep 2; done
alert=$(alert_state)
outage_requests=$(wc -l < "$results/phase.outage" | tr -d ' ')
until [ "$(promql 'sum(ledger_rate_limiter_failed_open_total)')" -ge "$outage_requests" ] || [ "$(date +%s)" -ge "$deadline" ]; do sleep 2; done
failed_open=$(promql 'sum(ledger_rate_limiter_failed_open_total)')
log "alert RateLimiterFailingOpen: $alert; requests let through unchecked: $failed_open"

log "starting redis"; compose start redis >/dev/null 2>&1
compose exec -T redis sh -c 'until redis-cli ping >/dev/null 2>&1; do sleep 0.2; done'
phase after "$phase_seconds"

wait_settled 300 || true
sleep 12   # one more export and scrape
# Requests that still failed open after Redis was back, until the API's Redis client reconnected.
reconnect=$(( $(promql 'sum(ledger_rate_limiter_failed_open_total)') - failed_open ))
echo
log "results:"
for p in before outage after; do
  printf '  %-7s %6s requests  %5s x 201  %5s x 429  %3s other\n' "$p" \
    "$(wc -l < "$results/phase.$p" | tr -d ' ')" "$(count "$p" 201)" "$(count "$p" 429)" \
    "$(grep -vEc '^(201|429)$' "$results/phase.$p" || true)"
done

check "limiter enforced before the outage (429s seen)" "$([ "$(count before 429)" -gt 0 ] && echo yes || echo no)" yes
check "429s while Redis was down (fails open)" "$(count outage 429)" 0
check "outage requests that did not return 201" "$(grep -vc '^201$' "$results/phase.outage" || true)" 0
log "  $reconnect requests failed open after Redis returned, before the API reconnected"
check "failed-open counter covers every outage request" "$([ "$failed_open" -ge "$outage_requests" ] && echo yes || echo no)" yes
check "RateLimiterFailingOpen alert" "$alert" firing
check "limiter enforced again after Redis returned (429s seen)" "$([ "$(count after 429)" -gt 0 ] && echo yes || echo no)" yes
# The shared checks treat any non-201 as a failure; 429s are expected outside the outage.
cat "$results"/phase.* | grep -v '^429$' > "$results/status"
verify

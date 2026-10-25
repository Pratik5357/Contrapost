# Load scenarios (milestone M6)

Design doc §13.2. The scenarios are in `tests/load/ledger.js`, run with `tests/load/run.sh <scenario>`:

| Scenario | Shape |
|---|---|
| `uniform` | Transfers between 200 random wallets (low contention) |
| `hot_account` | 80% of transfers pay into one settlement account, so they queue on its balance row lock |
| `retry_storm` | 30% of requests resend an earlier transfer with its idempotency key; each must replay exactly |
| `mixed` | 60% transfers, 20% holds, 15% captures, 5% balance reads |

**Thresholds (the run fails if any is crossed):**
- fewer than 1% failed requests (business rejections, 409 and 422, count as answers, not failures)
- transfer p99 under 500 ms (the alert target in §12)
- zero replay mismatches

**Settings:** the defaults are deliberately light, so the runs don't load the laptop heavily: **5 virtual users for 30 s** per scenario, one scenario at a time, with only the ledger services running (no observability stack).

**Hardware:** Apple M3, 8 GB RAM. Docker Desktop has 8 CPUs and 3.8 GB, and runs the API, Worker, Consumers, PostgreSQL 16, RabbitMQ and Redis. k6 runs on the same machine. These are laptop numbers: useful for comparing scenarios against each other, not for capacity planning.

## Results

Latencies are measured by k6 (client side) and include the round trip through Docker's network. The request counts include setup (200 accounts and their funding transfers).

| Scenario | Requests | Requests/s | Transfer p50 | p95 | p99 | max | Failed | Replay mismatches |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| `uniform` | 22,675 | 736 | 5.2 ms | 13.2 ms | 27.6 ms | 227 ms | 0% | — |
| `hot_account` | 14,673 | 475 | 7.6 ms | 24.6 ms | 50.2 ms | 352 ms | 0% | — |
| `retry_storm` | 28,682 | 931 | 5.3 ms | 11.7 ms | 20.5 ms | 120 ms | 0% | 0 |
| `mixed` | 21,061 | 681 | 5.6 ms | 12.1 ms | 19.1 ms | 125 ms | 0% | — |

All four passed every threshold.

## What the numbers show

- **Hot-account contention is visible even at 5 users.** Throughput falls by about a third against `uniform` (475 vs 736 requests/s) and transfer p99 nearly doubles (50 ms vs 28 ms). Most transfers serialise on one `account_balances` row lock: strategy A (§8.1) behaving as designed. The locking-strategy benchmark and hot-account sharding (§8.2) start from this baseline.
- **Retries are cheap.** `retry_storm` has the highest throughput, because a replay is one indexed read of `idempotency_keys` and never touches the ledger or takes a lock. All replays matched their originals.
- **Holds and captures cost about the same as transfers.** `mixed` stays close to `uniform`.

## Running heavier

The defaults are light on purpose. For a fuller picture, ideally on a machine that isn't also running the load generator:

```bash
VUS=32 DURATION=2m tests/load/run.sh hot_account
OBSERVE=1 tests/load/run.sh mixed   # also starts Grafana (localhost:3300) to watch the run's dashboards
```

CI runs `mixed` for 60 s with 5 users on every pull request and fails the build on the thresholds above (design doc §14).

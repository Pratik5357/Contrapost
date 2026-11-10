# Chaos runs C1, C4 and C5 (milestone M6)

Design doc §13.3. Like C2 and C3 ([results](chaos-m4.md)), each run starts from a fresh Compose stack (`docker compose down -v`) and ends with the shared checks: every outbox row published, each consumer's events handled exactly once, statements complete, and no balance drift (invariant I3).

**Hardware:** Apple M3, 8 GB RAM, Docker Desktop 29.8.1 with 8 CPUs and 3.8 GB memory. The load generator and the whole stack share this one machine. The runs use light defaults; the numbers show that the system recovers, not how fast it is.

**Rate limit:** each run's client has its per-client limit raised (as the k6 runner does), so the default 600 requests a minute doesn't turn the load into `429`s. C5 tests the limiter itself.

## C1: API killed under load

```
tests/chaos/c1-api-kill.sh    # KILLS=3 DOWN_SECONDS=5 LOAD_SECONDS=60 PARALLEL=4 by default
```

Four clients send random transfers between 20 wallets. Each transfer keeps its idempotency key and is retried every 200 ms after a connection error, `5xx` or `409 request-in-progress`, for up to 60 s. The script `SIGKILL`s the API container three times, leaves it down for 5 s, starts it again and waits for `/health/ready`.

| Result | Value |
|---|---|
| Transfers sent (4 clients, 60 s) | 6,204, all ending in `201` |
| API kills / time until ready again | 3 / 7 s, 7 s, 7 s |
| Transfers that needed retries | 8 |
| Transfers answered by an idempotent replay | 0 |
| Transfers posted / transfers sent (plus 20 funding transfers) | 6,224 / 6,224 |
| Money held by the wallets | 2,000,000,000, unchanged |
| Events handled per consumer | 6,224, exactly once each |
| Statement rows / postings | 12,448 / 12,448 |
| Balance drift (I3) | 0 accounts |
| Outcome | **Passed** |

Every retried transfer was posted exactly once. A request that was inside its database transaction when the API died was rolled back by Postgres when the connection dropped, so the retry ran it again from the start.

None of the retries in this run were replays. That would need a kill to land after a transfer's commit but before its response was sent, a window of well under a millisecond. The replay path is covered by the M5 concurrency run and the idempotency integration tests; C1 shows that a crash never leaves a half-posted transfer or a key stuck in progress.

## C4: 200 ms of Postgres latency under load

```
tests/chaos/c4-db-latency.sh  # LATENCY_MS=200, 20 s baseline, 40 s with latency, 20 s recovery, PARALLEL=4
```

The run adds a Compose overlay (`tests/chaos/toxiproxy.compose.yml`) that puts Toxiproxy between Postgres and the API, Worker and Consumers; migrations still connect directly. The same four retrying clients as in C1 run through three phases, and a Toxiproxy `latency` toxic adds 200 ms to every Postgres response during the middle one.

| Phase | Transfers | Transfers/s | p50 | p95 | Retried |
|---|---|---|---|---|---|
| Baseline (20 s) | 2,445 | 122 | 8 ms | 18 ms | 0 |
| 200 ms latency (40 s) | 68 | 1.7 | 2,247 ms | 3,480 ms | 0 |
| Recovery (20 s) | 2,868 | 143 | 7 ms | 14 ms | 0 |

| Check | Value |
|---|---|
| Transfers sent, all ending in `201` | 5,381 |
| Transfers posted / sent (plus 20 funding transfers) | 5,401 / 5,401 |
| Deadlocks reported by Postgres (`pg_stat_database`) | 0 |
| Money held by the wallets | 2,000,000,000, unchanged |
| Events handled per consumer | 5,401, exactly once each |
| Statement rows / postings | 10,802 / 10,802 |
| Balance drift (I3) | 0 accounts |
| Outcome | **Passed** |

Correctness held and nothing deadlocked: ordered locking means slower transactions only wait longer, never in a cycle. No transfer hit the 5 s `lock_timeout`, so none needed a retry. Throughput recovered as soon as the toxic was removed.

**Observation for the cloud deploy:** a median of 2.2 s at 200 ms per round trip means a transfer makes roughly 11 sequential round trips to Postgres (a little less, since new pooled connections also pay the latency during their handshake). That is invisible on a local socket, but with a managed database in another zone (1–2 ms per round trip) it adds 10–20 ms to every transfer. Batching the statements in a transfer (Npgsql batching, or one function call) would cut that if it shows up in production numbers. The API and the database should be deployed in the same region.

## C5: Redis stopped under load

```
tests/chaos/c5-redis-down.sh  # LIMIT_RPM=300, 20 s before, 30 s outage, 20 s after, PARALLEL=2
```

Here the client gets a real limit of 300 requests a minute, so the load (two plain clients, no retries) runs into it while Redis is up. The run stops Redis for 30 s, then starts it again.

| Phase | Requests | `201` | `429` | Other |
|---|---|---|---|---|
| Redis up (20 s) | 3,323 | 259 | 3,064 | 0 |
| Redis stopped (30 s) | 4,280 | 4,280 | 0 | 0 |
| Redis back (20 s) | 3,315 | 534 | 2,781 | 0 |

| Check | Value |
|---|---|
| `ledger_rate_limiter_failed_open_total` at the end of the outage | 4,280, one per outage request |
| `RateLimiterFailingOpen` alert (new, `deploy/observability/alerts.yml`) | Firing by the end of the outage |
| Requests that failed open after Redis was back, before the API reconnected | 234 (about 1.6 s at the outage throughput) |
| Events handled per consumer | 5,093, exactly once each |
| Statement rows / postings | 10,186 / 10,186 |
| Balance drift (I3) | 0 accounts |
| Outcome | **Passed** |

As ADR-006 intends, the outage cost availability nothing: no errors, no `429`s, and a throughput of 143 transfers/s against the 13/s the limit allowed before. Limiting resumed by itself. The "Redis back" phase allowed more than 300 because the API's Redis client takes a moment to reconnect (an earlier run measured 1.3 s from Redis being ready to the last fail-open warning), and requests in that gap also failed open.

The alert is new in this PR. ADR-006 called for one, and until now the counter had nothing watching it.

**Found while building C1:** the M4 chaos load (C2, C3) predates the M5 rate limiter and now gets `429`s after the first 600 requests a minute. `tests/chaos/lib.sh` now raises the limit for the run's client, so all chaos scripts work against the current API.

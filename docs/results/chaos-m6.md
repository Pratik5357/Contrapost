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

**Found while building C1:** the M4 chaos load (C2, C3) predates the M5 rate limiter and now gets `429`s after the first 600 requests a minute. `tests/chaos/lib.sh` now raises the limit for the run's client, so all chaos scripts work against the current API.

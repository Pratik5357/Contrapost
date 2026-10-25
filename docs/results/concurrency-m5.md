# Headline concurrency run (milestone M5)

Design doc §13.1. `HeadlineConcurrencyTests` runs workers that each loop over a random mix of operations on one client's shared accounts:

| Share | Operation | Allowed outcomes |
|---|---|---|
| 45% | Transfer between wallets, or to a merchant, of 1–30,000 (some deliberately overdraw) | 201, 422 |
| 15% | Hold from a wallet to a merchant, expiring in 1–20 s | 201, 422 |
| 12% | Capture a random hold (partial, or the rest), possibly one another worker created | 200, 422 |
| 6% | Void a random hold | 200, 422 |
| 7% | Reverse a random transfer | 201, 409, 422 |
| 15% | **Replay** an earlier request with its idempotency key and body | must equal the original status and JSON, with `Idempotent-Replayed: true` |

The hold expiry job runs every 500 ms throughout. Afterwards, the remaining holds are expired and reconciliation (I1–I8) runs over the whole database.

**Pass criteria:** no status outside the allowed set (so no 5xx), every replay identical to its original, reconciliation `clean` with 0 findings, and the client's posted debits equal its posted credits.

```
CONTRAPOST_SOAK_SECONDS=300 CONTRAPOST_SOAK_WORKERS=64 CONTRAPOST_SOAK_WALLETS=1000 \
  dotnet test tests/Contrapost.IntegrationTests -c Release --filter HeadlineConcurrency --logger "console;verbosity=detailed"
```

**Hardware:** Apple M3, 8 GB RAM. The API runs in-process (ASP.NET Core TestServer); PostgreSQL 16 runs in a Testcontainers container under Docker Desktop (8 CPUs, 3.8 GB).

## Run 1: found a bug

| Result | Value |
|---|---|
| Requests | 264,180 in 300 s (880/s) |
| Reconciliation | clean, 0 findings, 1,021 accounts |
| Outcome | **Failed**: 17 requests returned 500 |

All 17 were `55P03 lock timeout` while locking balance rows. The idempotency scope set `SET LOCAL lock_timeout` to bound the wait on a duplicate key, but `SET LOCAL` lasts for the whole transaction. So under contention on hot accounts, the ledger's own row-lock waits timed out too. Every failed transaction rolled back, which is why reconciliation stayed clean, but the clients got errors.

**Fix:** reset `lock_timeout` to the default right after the key is claimed. Ordered locking already makes balance-row waits deadlock-free. A regression test holds a balance row for longer than the key timeout and checks that the transfer waits, then succeeds.

## Run 2: after the fix

| Result | Value |
|---|---|
| Workers / wallets / merchants | 64 / 1,000 / 20 |
| Requests | 256,357 in 300 s (854/s) |
| Transfers | 44,874 posted, 70,283 refused for insufficient funds |
| Holds | 18,349 created, 20,059 refused; 2,781 captures and 1,820 voids succeeded |
| Reversals | 8,936 posted, 3,054 already reversed, 6,098 refused (money already spent, or not reversible) |
| Replays | 38,591, every one identical to its original response |
| Server errors | 0 |
| Reconciliation | **clean, 0 findings, 1,021 accounts** |
| Outcome | **Passed** |

The high refusal rates are intended: 64 workers drain 1,000 wallets of 1,000.00 quickly, so most of the run exercises the overdraft rules and the closed-hold paths under contention.

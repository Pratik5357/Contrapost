# ADR-003: Pessimistic, ordered row locking for ledger writes

- **Status:** Accepted
- **Date:** 2026-10-25

## Context

Every transfer, capture and reversal reads the balances of the accounts it touches, decides whether the overdraft rules allow it, and writes new balances. Concurrent writers on the same accounts must not both act on the same stale balance. Design doc §8.1 lists three ways to prevent that:

- **A:** lock the rows with `SELECT … FOR UPDATE` in a fixed order.
- **B:** optimistic version checks with retry.
- **C:** `SERIALIZABLE` transactions with retry.

All three are implemented behind one interface and were benchmarked against contention ([results](../results/locking-benchmark.md)).

## Decision

Ledger writes use **A**:
- Lock the `account_balances` rows with `SELECT … FOR UPDATE`, always in ascending `account_id` order, under `READ COMMITTED`.
- Make every decision only after the locks are held.
- Hold operations lock the hold row first, then the balance rows in the same order.

## Consequences

- **No deadlocks between ledger writers.** Every writer takes balance locks in the same global order. The hold expiry job locks each batch's balance rows in one sorted statement for the same reason.
- **No lost work under contention.** With every transfer colliding, A sustained 388 transfers/s at a p99 of 30 ms with zero abandoned transfers. B abandoned 243 and C fell to 28/s with a p99 of 5 s. Without contention, all three are within 5%.
- **The cost is queueing on hot rows.** Writers to the same account run one at a time, so a single very hot account (a settlement account, say) caps throughput. That's the case for hot-account sharding (§8.2), not for a different strategy: every strategy is limited by the same hot row, and the retrying ones degrade worse.
- **Waits must be allowed to finish.** Lock timeouts must not apply to these waits. That's why the idempotency key's `lock_timeout` is reset once the key is claimed (found by the M5 concurrency run).
- **B and C stay in the codebase** (`LockingStrategies.cs`), with a correctness test each, so the comparison can be re-run whenever the write path changes.

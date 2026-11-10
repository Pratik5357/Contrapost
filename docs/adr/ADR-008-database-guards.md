# ADR-008: Database guards as a second line of defence

- **Status:** Accepted
- **Date:** 2026-11-10 (decided at design time; recorded in M8)

## Context

The application validates every rule before it writes: balanced legs, overdraft limits, hold transitions, tenant ownership. But a ledger outlives its code. A bug, a careless migration, a new code path or a manual fix in `psql` can skip an application check, and a ledger that has once been wrong can't be trusted again.

## Decision

Enforce the invariants that must never break in the schema as well, so that the database rejects a bad write even if the application tries to make it.

| Rule | Mechanism |
|---|---|
| Balanced transactions with at least two postings (I1) | Deferred constraint triggers, checked at commit (`CP001`) |
| Postings and transactions are append-only | Statement triggers on `UPDATE`, `DELETE` and `TRUNCATE` (`CP002`), and the services' role `contrapost_app` has only `SELECT` and `INSERT` on them |
| Hold lifecycle | Row trigger on `holds` (`CP003`) |
| Only transfers and captures can be reversed | Row trigger (`CP004`) |
| Same tenant and currency everywhere | Composite foreign keys |
| No forbidden overdraft (I4) | `CHECK` constraints on `account_balances` |
| At most one reversal per transaction (I8), gapless `account_seq` | Unique indexes |

Every guard has a test in `db/tests/guards.sql`, run by `scripts/db-test.sh` in CI against a throwaway Postgres.

## Consequences

- **A guard firing means a bug.** In normal operation the application rejects these cases first with a clear `4xx`, so a `CPxxx` error in production is logged and treated as a defect, not a validation result.
- **Reconciliation still checks them.** I1, I4 and I8 are also checked by the reconciliation job, in case a constraint is ever dropped.
- **Some cost on writes.** Deferred triggers run at every commit and composite keys add index checks. The locking benchmark and the load tests were all measured with the guards in place.
- **Two barriers for history.** The services log in as a member of `contrapost_app` (migration 005), not as the owner, so they can't disable the triggers either: an `UPDATE` on postings fails with `permission denied` before any trigger runs. Only `migrate` uses the owner. The integration tests run every host as such a login, which also proves the role has every privilege the services need.
- **The schema is the contract.** Rules live in two places (C# and SQL), and both have tests. That duplication is deliberate.

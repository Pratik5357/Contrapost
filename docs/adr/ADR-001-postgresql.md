# ADR-001: PostgreSQL rather than SQL Server

- **Status:** Accepted
- **Date:** 2026-11-10 (decided at design time; recorded in M8)

## Context

The ledger's correctness rests on the database: row locks taken in a fixed order, constraints that hold at commit time, and a work queue for the outbox relay. My previous project (RelayForge) used SQL Server, which would have been the familiar choice. The database also has to be cheap to host for a public demo.

## Decision

Use PostgreSQL 16 for everything: the ledger, the idempotency keys, the outbox and inbox, and the read models.

## Why

- **Deferred constraints.** The balanced-transaction guard (I1, `CP001`) is a `DEFERRABLE INITIALLY DEFERRED` constraint trigger: postings are inserted one by one, and the check runs once at commit, when the whole transaction is visible. SQL Server has no deferrable constraints, so the same guard would need application code or a different write shape.
- **`FOR UPDATE SKIP LOCKED`.** Several relay instances can claim outbox batches without blocking each other (design doc §9.2). SQL Server's `READPAST` hints can do something similar, but `SKIP LOCKED` is the plain, documented idiom.
- **Expressive guards.** `CHECK` constraints for the overdraft rules, composite foreign keys for tenant and currency, statement-level triggers that make postings append-only, and enum types for statuses. All of them are tested in `db/tests/guards.sql`.
- **Hosting.** Managed Postgres has free or near-free tiers on every container host I considered.
- **Breadth.** RelayForge already shows SQL Server. A second database is more useful in the portfolio than a second project on the same one.

## Consequences

- The schema uses Postgres-specific features throughout (`jsonb`, enums, deferred triggers, `make_interval`), so moving to another database would be a rewrite of `db/`, not a configuration change. That is accepted.
- `lock_timeout` and `SET LOCAL` give per-transaction control over waiting, which the idempotency rules depend on (§7.3).

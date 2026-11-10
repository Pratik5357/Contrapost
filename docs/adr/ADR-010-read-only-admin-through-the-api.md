# ADR-010: The admin dashboard is read-only and reads only through the API

- **Status:** Accepted
- **Date:** 2026-11-10

## Context

Operators need to see the ledger across every client: totals, accounts, transactions, holds, the outbox and reconciliation results (design doc §5.2). The dashboard could query Postgres directly, which is quick to build, or go through the API like any other client.

## Decision

The dashboard reads only through `/v1/admin/*`, with an API key holding the `admin:read` scope, and v1 has no admin writes.

The endpoints are the only ones that read across tenants, and they reuse the client-scoped reads wherever they can. A detail endpoint finds the owning client first, then calls the same `AccountStore`, `TransferStore` or `HoldStore` method that serves that client's own request. That way an operator sees exactly the JSON the client would. The admin-only queries (search, overview, outbox) are plain autocommit `SELECT`s: no transaction, no row locks, keyset-paged in id order.

## Consequences

- **One place for the rules.** Paging limits, cursor validation and enum labels live in the API. The dashboard can't drift from them, and it never holds database credentials.
- **Auditable.** Every admin read is logged with the dashboard's client id and the operator session it sends in `X-Operator-Session` (design doc §11).
- **Can't hurt the write path.** Admin queries take no locks and could point at a read replica later without the dashboard noticing.
- **Corrections still go through the public API**, with idempotency keys. Admin writes (reversals, freezing accounts) need an audit trail and stronger operator authentication, and are a stretch goal (§16).
- **The cost is one more hop**, and a new admin page needs an endpoint before it can ship. At operator traffic that's negligible.

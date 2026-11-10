# ADR-004: A transactional outbox instead of publishing from the request

- **Status:** Accepted
- **Date:** 2026-11-10 (decided at design time; recorded in M8)

## Context

Every committed transfer, capture or reversal must produce exactly one event for downstream consumers (goal G6). Publishing to RabbitMQ from the request handler is a dual write: the database commit and the broker publish can't be made atomic. Publish first and the commit may fail, leaving an event for money that never moved. Commit first and the process may die before publishing, losing the event for money that did move.

## Decision

Write each event as a row in `outbox_messages` **in the same database transaction** as the ledger change. A separate relay in the Worker process reads unpublished rows with `FOR UPDATE SKIP LOCKED`, publishes them with publisher confirms, and marks them published only after the broker confirms. Consumers deduplicate through an `inbox_messages` table, committing the inbox row and their own effect in one transaction before acking.

## Consequences

- **No lost and no phantom events.** An event exists if and only if its ledger change committed. Reconciliation checks this as invariant I7.
- **The write path never waits for the broker.** In chaos run C2 the broker was down for 120 s while the API kept accepting transfers (21,936, all `201`); the outbox backed up to 18.8k rows and drained in 100 s once the broker returned ([results](../results/chaos-m4.md)).
- **Delivery is at-least-once.** A relay that dies after publishing but before marking a row publishes it again. Chaos run C3 killed the relay mid-batch eight times; 674 duplicates reached the consumers and the inbox absorbed every one.
- **Order isn't guaranteed.** Consumers that need order use each leg's gapless `account_seq`, not arrival order (design doc §9.1).
- **Costs.** An extra table that grows, polling latency of up to 200 ms, and a relay process to run and monitor (the `OutboxLagHigh` alert). A partial index keeps the relay's query on unpublished rows fast, and a daily job deletes published rows older than 7 days, outside the window reconciliation checks.

# ADR-005: RabbitMQ rather than Kafka for v1

- **Status:** Accepted
- **Date:** 2026-11-10 (decided at design time; recorded in M8)

## Context

The outbox relay needs a broker to fan events out to the consumers (statement projector, low-balance notifier). Event volume is modest, two consumers each need their own copy of every money event, poison messages must be parked rather than block a queue, and the whole stack has to run on one laptop.

## Decision

Use RabbitMQ: a `ledger.events` topic exchange, one quorum queue per consumer, publisher confirms on the relay, and a dead-letter queue per consumer queue with a delivery limit of 5.

## Why not Kafka

- **The ordering and replay Kafka is best at aren't needed.** Consumers already get per-account order from `account_seq`, and the outbox table is the replayable record within its retention window.
- **Per-message handling fits better.** Acks, redelivery and dead-lettering of a single poison message are built in. In Kafka a bad record blocks its partition unless the consumer builds its own retry and dead-letter topics.
- **Lighter to run.** One container with a management UI, against a Kafka cluster (KRaft or ZooKeeper) on an 8 GB laptop that also runs Postgres, Redis, the services and the observability stack.

## Consequences

- Quorum queues give replicated, durable queues once there is more than one node; locally there is one.
- Consumers dedupe through the inbox, because delivery is at-least-once either way (ADR-004).
- If event volume or the number of independent readers grew a lot, Kafka's retained log would start to pay off. A Kafka variant of the relay is listed as a stretch goal for comparison.

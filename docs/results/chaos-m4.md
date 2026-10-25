# Chaos runs C2 and C3 (milestone M4)

Design doc §13.3. Both runs start from a fresh Compose stack (`docker compose down -v`) and end with the same checks:

- every outbox row published
- each consumer's inbox holds exactly one row per published money event (`transfer.posted`, `transfer.reversed`, `hold.captured`)
- one statement row per posting, and every account's statement complete through its latest `account_seq`
- no account whose stored balance differs from its postings (invariant I3)

**Hardware:** Apple M3, 8 GB RAM, Docker Desktop 29.8.1 with 8 CPUs and 3.8 GB memory. The load generator (`curl` loops), the API, Postgres, RabbitMQ, the Worker and the Consumers all share this one machine, so the throughput numbers below are much lower than a real deployment would show. They are recorded to show that the system recovers, not how fast it is. Throughput is measured properly in M6.

## C2: broker outage under load

```
OUTAGE_SECONDS=120 tests/chaos/c2-broker-outage.sh
```

| Result | Value |
|---|---|
| Transfers posted during the run (8 parallel clients, 160 s) | 21,936, all `201` |
| Broker outage | 120 s |
| Peak outbox backlog | 18,674 rows during the outage, 18,798 when the broker came back |
| Oldest unpublished row | 124 s |
| Time to drain the outbox after the broker returned | 100 s |
| Events handled per consumer | 21,956, exactly once each |
| Statement rows / postings | 43,912 / 43,912 |
| Balance drift (I3) | 0 accounts |
| Outcome | **Passed** |

The API kept accepting transfers throughout. The write path never talks to the broker, and that is the point of the outbox. While the broker was down, the relay logged `Broker unavailable, retrying in 1s…`, backing off up to 10 s. It did not count attempts against the rows, so the backlog was due the moment the broker returned. The consumers recovered their connections automatically; 36 duplicate deliveries around the reconnect were absorbed by the inbox.

## C3: relay killed mid-batch

```
KILLS=8 tests/chaos/c3-relay-crash.sh
```

| Result | Value |
|---|---|
| Backlog built with the Worker stopped (30 s of load) | 8,377 unpublished rows |
| `SIGKILL`s of the Worker while draining | 8, each fired as soon as the relay was seen publishing |
| Duplicate deliveries absorbed by the inbox | 674 |
| Events handled per consumer | 8,397, exactly once each |
| Statement rows / postings | 16,794 / 16,794 |
| Balance drift (I3) | 0 accounts |
| Outcome | **Passed** |

Each kill landed inside a batch: messages had been published and confirmed, but `published_at` hadn't been committed. On restart the relay published them again. The 674 duplicates show that this really happened, and the inbox absorbed every one.

## Observations for M6

- **Consumer throughput is the bottleneck under this load.** Each consumer handles one message at a time, each in its own database transaction, so the consumers finished several minutes after the outbox drained. Options: higher consumer dispatch concurrency (with the projector's legs sorted by account id, so head-row locks are always taken in the same order), or batching several messages per transaction.
- **Relay drain rate** was about 190 events/s in C2, while competing with the load generator and both consumers for the same CPUs.

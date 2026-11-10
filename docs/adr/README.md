# Architecture decision records

Each record states a decision, the context it was made in, and what it costs. Several were decided at design time and written down in M8; their dates say so.

| ADR | Decision |
|---|---|
| [001](ADR-001-postgresql.md) | PostgreSQL rather than SQL Server |
| [002](ADR-002-integer-minor-units.md) | Money is an integer number of minor units |
| [003](ADR-003-pessimistic-ordered-locking.md) | Pessimistic, ordered row locking for ledger writes |
| [004](ADR-004-transactional-outbox.md) | A transactional outbox instead of publishing from the request |
| [005](ADR-005-rabbitmq.md) | RabbitMQ rather than Kafka for v1 |
| [006](ADR-006-rate-limiter-fails-open.md) | The rate limiter fails open when Redis is unavailable |
| [007](ADR-007-holds-record-pending-money.md) | Holds are the record of pending money; postings record only money that moved |
| [008](ADR-008-database-guards.md) | Database guards as a second line of defence |
| [009](ADR-009-minimal-apis.md) | Minimal APIs instead of controllers |
| [010](ADR-010-read-only-admin-through-the-api.md) | The admin dashboard is read-only and reads only through the API |

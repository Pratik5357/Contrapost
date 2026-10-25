# Contrapost

A double-entry payments ledger in .NET: correct balances under concurrent load, exactly-once effects for retried requests, and reliable event publishing through a transactional outbox.

The design is in [`Contrapost-Design-Doc.md`](Contrapost-Design-Doc.md).

## Status

Milestone M6 (in progress):
- Accounts, multi-leg transfers with ordered row locking and overdraft rules, balances and keyset-paged history.
- API-key authentication with per-endpoint scopes.
- Idempotency keys on every money-moving POST. The key is claimed in the same database transaction as the ledger writes, so a retry replays the stored response and never posts twice.
- Holds: authorize, capture in full or in several partial captures, void, or let the Worker expire them.
- Reversals of transfers and captures: at most once, and subject to overdraft rules.
- Per-client rate limiting: a Redis sliding window returns `429` with `Retry-After`, and lets requests through if Redis is down ([ADR-006](docs/adr/ADR-006-rate-limiter-fails-open.md)).
- Events: a transactional outbox, relayed to RabbitMQ with publisher confirms, consumed by a statement projector and a low-balance notifier that deduplicate through an inbox. Quorum queues with dead-letter queues hold poison messages.
- Reconciliation of invariants I1–I8, nightly in the Worker or on demand, with the results readable at `/v1/admin/reconciliation/*`.
- Verification: a headline concurrency test (random mixed operations, then a reconciliation that must be clean) and FsCheck property tests of the domain rules.

- Observability: OpenTelemetry traces (one trace per transfer, from the API through the outbox relay to the consumers), the `ledger_*` metrics, and a Grafana dashboard with Prometheus alerts.

Not yet: load tests, the locking-strategy benchmark and the remaining chaos runs (rest of M6), the admin dashboard (M7).

## Getting started

Prerequisites: Docker and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Run the whole stack: postgres, rabbitmq, redis, migrate (one-shot), api, worker, consumers
docker compose -f deploy/docker-compose.yml up -d --build

curl -i localhost:8080/health/live    # 200 once the process is up
curl -i localhost:8080/health/ready   # 200 once the database is reachable and migrated
```

Try the API. `scripts/seed.sh` creates a client and prints its API key once:

```bash
scripts/seed.sh                     # prints api_key: cp_dev_xxxxxxxx.<secret>
H='Authorization: Bearer <api_key>'

curl -s -H "$H" -H 'Content-Type: application/json' localhost:8080/v1/accounts \
  -d '{"code":"funding:INR","currency":"INR","normal_side":"debit","prevent_overdraft":false}'
curl -s -H "$H" -H 'Content-Type: application/json' localhost:8080/v1/accounts \
  -d '{"code":"wallet:alice","currency":"INR","normal_side":"credit"}'

# Money-moving POSTs need an Idempotency-Key; repeating the request replays the first response.
curl -s -H "$H" -H 'Content-Type: application/json' -H 'Idempotency-Key: top-up-1' localhost:8080/v1/transfers \
  -d '{"currency":"INR","reference":"top-up-1","legs":[
        {"account_id":"<funding id>","direction":"debit","amount":100000},
        {"account_id":"<alice id>","direction":"credit","amount":100000}]}'

curl -s -H "$H" localhost:8080/v1/accounts/<alice id>/balance
curl -s -H "$H" "localhost:8080/v1/accounts/<alice id>/entries?limit=20"
```

Amounts are integers in minor units (paise), so `100000` is ₹1,000.

Holds and reversals:

```bash
# Reserve ₹300 from alice for a merchant; expires in an hour unless captured or voided
curl -s -H "$H" -H 'Content-Type: application/json' -H 'Idempotency-Key: hold-1' localhost:8080/v1/holds \
  -d '{"debit_account_id":"<alice id>","credit_account_id":"<merchant id>","currency":"INR","amount":30000,"expires_in_seconds":3600}'

curl -s -H "$H" -H 'Content-Type: application/json' -H 'Idempotency-Key: cap-1' \
  localhost:8080/v1/holds/<hold id>/capture -d '{"amount":10000}'     # omit amount to capture the rest
curl -s -H "$H" -H 'Idempotency-Key: void-1' -X POST localhost:8080/v1/holds/<hold id>/void

# Undo a transfer or capture with a reversal transaction
curl -s -H "$H" -H 'Idempotency-Key: undo-1' -X POST localhost:8080/v1/transfers/<transfer id>/reversal
```

Create clients with other scopes directly: `docker compose -f deploy/docker-compose.yml run --rm migrate create-client <name> [scope ...]`, where the scopes are `accounts:read accounts:write transfers:write holds:write admin:read`.

Develop against the containerised infrastructure:

```bash
docker compose -f deploy/docker-compose.yml up -d postgres rabbitmq redis
dotnet tool restore
dotnet run --project src/Contrapost.Api -- migrate   # apply migrations, then exit
dotnet run --project src/Contrapost.Api
```

Tests:

```bash
dotnet test Contrapost.slnx    # unit, property and integration tests (PostgreSQL and RabbitMQ via Testcontainers)

# The full headline concurrency run (design doc §13.1)
CONTRAPOST_SOAK_SECONDS=300 CONTRAPOST_SOAK_WORKERS=64 CONTRAPOST_SOAK_WALLETS=1000 \
  dotnet test tests/Contrapost.IntegrationTests -c Release --filter HeadlineConcurrency --logger "console;verbosity=detailed"

# Reconcile a running stack on demand: exit code 0 clean, 2 drift found
docker compose -f deploy/docker-compose.yml run --rm migrate reconcile
scripts/db-test.sh             # database guard tests (Docker only)

# Chaos runs against a fresh compose stack (design doc §13.3); each ends with exactly-once checks
tests/chaos/c2-broker-outage.sh   # stop RabbitMQ under load (OUTAGE_SECONDS, default 120)
tests/chaos/c3-relay-crash.sh     # SIGKILL the relay mid-batch while it drains a backlog (KILLS, default 8)
```

Local tools:

| Tool | URL |
|---|---|
| Grafana (dashboard and traces) | http://localhost:3300 |
| Prometheus (metrics and alerts) | http://localhost:9090 |
| RabbitMQ management (contrapost / contrapost) | http://localhost:15672 |

Grafana is on 3300 because 3000 is often taken by a frontend dev server.

## Repository layout

| Path | Contents |
|---|---|
| `src/Contrapost.Api` | HTTP API (Minimal APIs, see [ADR-009](docs/adr/ADR-009-minimal-apis.md)); also the `migrate` command |
| `src/Contrapost.Worker` | Background jobs: hold expiry, outbox relay |
| `src/Contrapost.Consumers` | Event consumers: statement projector, low-balance notifier |
| `src/Contrapost.Domain` | Entities and rules, no I/O |
| `src/Contrapost.Application` | Use cases |
| `src/Contrapost.Infrastructure` | EF Core, SQL, broker and cache adapters; migrations embed `db/schema.sql` |
| `db/` | Baseline schema, numbered migrations, guard tests |
| `deploy/` | Docker Compose |
| `tests/chaos/` | Chaos runs C2 and C3 |
| `docs/adr/` | Architecture decision records |

## Development workflow

`main` is always releasable and is only changed through pull requests.

### Branches

Branch from the latest `main` and name the branch `<type>/<short-description>`:

| Prefix | Use for |
|---|---|
| `feat/` | New functionality |
| `fix/` | Bug fixes |
| `perf/` | Performance improvements |
| `refactor/` | Code changes that don't change behaviour |
| `test/` | Tests only |
| `docs/` | Documentation only |
| `chore/` | Tooling, CI, dependencies |

Example: `feat/holds-partial-capture`, `fix/outbox-backoff-overflow`.

### Commits

Commit messages follow [Conventional Commits](https://www.conventionalcommits.org/):

```
<type>(<scope>): <imperative summary, 72 chars max>

<body: what changed and why>
```

Scopes in use: `api`, `db`, `worker`, `domain`, `design`, `ci`, `deps`.

### Pull requests

1. Rebase the branch on `origin/main` before opening the PR and again before merging.
2. Fill in the PR template, including how the change was tested.
3. Merge with **squash** or **rebase**, so `main` keeps a linear history.
4. Delete the branch after merging.

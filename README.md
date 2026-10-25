# Contrapost

A double-entry payments ledger in .NET: correct balances under concurrent load, exactly-once effects for retried requests, and reliable event publishing through a transactional outbox.

The design is in [`Contrapost-Design-Doc.md`](Contrapost-Design-Doc.md).

## Status

Milestone M2:
- Accounts, multi-leg transfers with ordered row locking and overdraft rules, balances and keyset-paged history.
- API-key authentication with per-endpoint scopes.
- Idempotency keys on transfers. The key is claimed in the same database transaction as the ledger writes, so a retry replays the stored response and never posts twice.

Not yet: holds and reversals (M3), event publishing (M4), reconciliation and rate limiting (M5).

## Getting started

Prerequisites: Docker and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

```bash
# Run the whole stack: postgres, rabbitmq, redis, migrate (one-shot), api, worker
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
dotnet test Contrapost.slnx    # unit tests, plus integration tests on PostgreSQL via Testcontainers
scripts/db-test.sh             # database guard tests (Docker only)
```

## Repository layout

| Path | Contents |
|---|---|
| `src/Contrapost.Api` | HTTP API (Minimal APIs, see [ADR-009](docs/adr/ADR-009-minimal-apis.md)); also the `migrate` command |
| `src/Contrapost.Worker` | Background jobs host |
| `src/Contrapost.Domain` | Entities and rules, no I/O |
| `src/Contrapost.Application` | Use cases |
| `src/Contrapost.Infrastructure` | EF Core, SQL, broker and cache adapters; migrations embed `db/schema.sql` |
| `db/` | Schema (source of truth) and guard tests |
| `deploy/` | Docker Compose |
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

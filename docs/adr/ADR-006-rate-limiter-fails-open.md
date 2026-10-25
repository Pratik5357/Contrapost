# ADR-006: The rate limiter fails open when Redis is unavailable

- **Status:** Accepted
- **Date:** 2026-10-25

## Context

Each API client has a `rate_limit_rpm`. The API enforces it with a sliding-window log in Redis (design doc §11). Redis is the only shared state the limiter has, so when Redis is down or slow, the API has to choose:

- **Fail closed:** reject requests (429 or 503) because the limit can't be checked.
- **Fail open:** let requests through unlimited until Redis returns.

## Decision

Fail open. When Redis can't be reached, the request proceeds and the API logs `Rate limiter unavailable, allowing request …` at warning level. A metric follows in M6.

The connection is set to fail fast, so the outage costs nothing per request:

- `BacklogPolicy.FailFast`: commands fail immediately while disconnected, instead of queuing.
- Connect timeout 1 s; command timeouts 250 ms.

## Consequences

- **A Redis outage can't take the ledger down.** The rate limiter protects the API from overload; it isn't part of correctness. Moving money is guarded by Postgres (locks, constraints, idempotency keys), none of which depends on Redis.
- **During an outage, clients can exceed their limits.** The risk is load, not wrong balances. The warning log (and later the metric and alert) makes the window visible. Chaos run C5 exercises it.
- **Measured** on the Compose stack: with Redis stopped, requests took 3–4 ms. Before fail-fast was set, they took 0.7–1 s each, because commands waited in the client's backlog. After Redis restarted, limiting resumed without intervention.
- **If abuse during outages became a real concern,** a local in-memory limiter could take over as a fallback, at the cost of per-instance (not global) limits.

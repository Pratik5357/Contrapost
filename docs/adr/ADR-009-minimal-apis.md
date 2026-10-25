# ADR-009: Minimal APIs instead of controllers

- **Status:** Accepted
- **Date:** 2026-10-25
- **Resolves:** design doc §18, "controllers or Minimal APIs?"

## Context

The API has about a dozen endpoints (design doc §7.1). Every money-moving endpoint shares the same pipeline: API-key authentication, scope check, rate limiting, `Idempotency-Key` handling and RFC 9457 Problem Details errors. The handlers themselves should be thin calls into `Contrapost.Application` use cases.

## Decision

Use ASP.NET Core Minimal APIs, grouped by resource (`/v1/accounts`, `/v1/transfers`, `/v1/holds`) with `MapGroup`. Cross-cutting behaviour is attached to groups as endpoint filters and metadata, not inherited from a base controller.

## Consequences

- The shared pipeline is explicit per group (`.RequireAuthorization("transfers:write").AddEndpointFilter<IdempotencyFilter>()`), so it's visible exactly which endpoints require an idempotency key.
- .NET 10 includes built-in validation and OpenAPI generation for Minimal APIs, which removes the main reasons controllers used to be preferred.
- Less framework surface: no model-binding conventions, action filters or controller discovery to reason about on the hot write path.
- Large endpoint files are the main risk. Each resource gets its own static `Map…Endpoints` class to keep `Program.cs` short.

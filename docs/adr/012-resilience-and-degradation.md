# ADR-008: Resilience mechanisms and degradation rules

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Partial failures are normal in distributed systems; each dependency needs defined behaviour.

## Decision

- Every service-to-service `HttpClient` uses `AddStandardResilienceHandler()` (rate limiter, total timeout, retry, circuit breaker, attempt timeout); retries apply only to idempotent HTTP methods.
- `/health/live` and `/health/ready` (ready checks the database) wired to Container Apps probes.
- ProblemDetails errors; exception details never returned to clients.
- ASP.NET Core rate limiting per user on hold and queue-join endpoints. nginx stays as the entry point.
- Degradation in Sprint 2: Waiting room down → high-demand shows pause, others unaffected; Inventory down → holds stop and publishing fails with a clear retry message, browsing continues; Identity down → cached organizer status keeps Catalog working briefly.

## Consequences

**Positive**

- Predictable behaviour under failure with standard libraries.

**Negative / trade-offs**

- Timeouts need tuning from load-test data.

## References

- Microsoft.Extensions.Http.Resilience: https://github.com/dotnet/extensions/blob/main/src/Libraries/Microsoft.Extensions.Http.Resilience/README.md

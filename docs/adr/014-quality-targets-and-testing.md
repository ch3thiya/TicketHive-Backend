# ADR-010: Quality targets and testing strategy

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Fault tolerance and concurrency claims must be measurable and demonstrated.

## Decision

- Targets: zero oversell; hold p95 < 500 ms and queue status p95 < 300 ms with 1,000 virtual users; expired holds released ≤ 30 s; each expired hold released exactly once with two instances.
- xUnit unit tests for domain rules; Testcontainers integration tests against real Postgres; JMeter load tests.
- **Test-first for the concurrency hot path:** for Inventory and Waiting room, the required concurrency tests are written first, confirmed failing, then implemented until they pass. Elsewhere, tests are written when the feature code is complete.
- A flaky concurrency test is treated as a race in the code, never fixed with retries or sleeps.

## Consequences

**Positive**

- Claims are evidenced, not asserted.

**Negative / trade-offs**

- Integration tests need Docker in CI.

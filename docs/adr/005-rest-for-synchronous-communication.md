# ADR-003: REST for synchronous communication

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Events for every interaction would be too much infrastructure for the team's timeline, and synchronous chains reduce availability. A clear rule is needed.

## Decision

- Services talk over REST for request/response needs.
- Synchronous service calls in Sprint 2: Catalog → Inventory (initialize show, idempotent, before Catalog commits Published), Catalog → Identity (organizer status, cached) and Waiting room → Catalog (sales rules, cached).
- Every synchronous call uses timeouts, retry with jitter only for idempotent operations, and a circuit breaker.
- Asynchronous messaging is not used in Sprint 2; it will be decided when checkout and notifications are built.

## Consequences

**Positive**

- Simple to build and debug.
- Few synchronous dependencies on the customer path.

**Negative / trade-offs**

- Publishing a show fails while Inventory is down (acceptable: checkout needs Inventory anyway).

## Alternatives considered

- Events for everything — rejected as infeasible for the team and timeline.

# ADR-008: Hold expiry via a database sweeper

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

The Sprint 2 task SCRUM-69 planned an in-process timer. Timers die with their instance and double-fire with two replicas.

## Decision

- Every hold stores `expires_at` (duration from the show's sales rules, default about 10 minutes).
- Each Inventory instance runs a sweeper every ~10 s that claims expired holds with `FOR UPDATE SKIP LOCKED` and returns stock and quota in the same transaction.
- Expired holds can never be converted or reused.

## Consequences

**Positive**

- Safe with any number of instances; no single point of failure.
- Failure direction is safe: a stopped sweeper under-sells temporarily, never over-sells.

**Negative / trade-offs**

- GA stock returns up to one sweep interval late.

## Alternatives considered

- In-process timers — rejected (SCRUM-69 task rewritten).

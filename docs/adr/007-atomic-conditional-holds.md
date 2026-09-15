# ADR-005: Atomic conditional holds

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Many customers try to take the same stock at once. A check followed by a separate update would oversell.

## Decision

- GA stock: `UPDATE stock SET available = available - @qty WHERE ... AND available >= @qty` — zero rows affected means not enough stock.
- The quota check, stock change and hold insert happen in one transaction; any refusal rolls back all of it.
- When several categories are held in one request, they are processed in a fixed `category_id` order to avoid deadlocks.
- Seat-level locking is decided when seat maps are built (Sprint 3); allocation sits behind `IAllocationStrategy` so it can be added without rework.

## Consequences

**Positive**

- No oversell across any number of instances without distributed locks.
- Fixed ordering prevents deadlocks.

**Negative / trade-offs**

- Concurrency behaviour must be proven with integration tests (ADR-014).

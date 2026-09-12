# ADR-004: PostgreSQL is the inventory source of truth; Redis can be added later

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Correctness under concurrency is the top quality goal. Distributed locks in Redis are unsafe for correctness under process pauses and clock issues; managed Redis also costs money.

## Decision

- GA categories: one counter row per show and category (`capacity`, `available`). Holds in `holds` and `hold_items`; per-customer quotas in `customer_quotas`. Seat-level storage is decided with seat maps in Sprint 3.
- No Redis now. Queue position reads and availability reads sit behind interfaces (`IQueueStore`, `IAvailabilityReader`) so a Redis implementation can be added later if load tests show a need, without changing business logic.

## Consequences

**Positive**

- One source of truth with real transactions.
- Zero extra infrastructure cost for Sprint 2.

**Negative / trade-offs**

- A hot counter row serialises updates — mitigated by the waiting room's admission rate.

## Alternatives considered

- Redis as primary stock store or Redlock for correctness — rejected.

## References

- Martin Kleppmann, How to do distributed locking: https://martin.kleppmann.com/2016/02/08/how-to-do-distributed-locking.html

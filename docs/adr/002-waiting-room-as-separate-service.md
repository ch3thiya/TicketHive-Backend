# ADR-002: The waiting room is its own service

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

During an on-sale, thousands of customers poll their queue position while only admitted customers place holds. If both ran in one service, queue traffic could starve the hold path of CPU and database connections — the failure mode seen in large public on-sales.

## Decision

The waiting room is a separate service. It never calls Inventory; Inventory only verifies the admission token signature (bulkhead pattern).

## Consequences

**Positive**

- Queue overload cannot take down checkout for admitted customers.
- The waiting room can scale independently.

**Negative / trade-offs**

- One more deployable.
- Both still share one Postgres server on the student budget (documented in ADR-013).

## Alternatives considered

- Waiting room inside Inventory — rejected for isolation reasons.

## References

- SeatGeek virtual waiting room (AWS Architecture Blog): https://aws.amazon.com/blogs/architecture/build-a-virtual-waiting-room-with-amazon-dynamodb-and-aws-lambda-at-seatgeek/

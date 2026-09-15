# ADR-007: Waiting room: queue numbers, serving counter and admission tokens

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

High-demand on-sales need fair, persistent, multi-instance queueing without extra infrastructure.

## Decision

- Only shows the organizer marks as high-demand use the queue. Joining requires login; one entry per account per show.
- **Pre-queue with randomisation**: arrivals before on-sale get random positions at on-sale; later arrivals are appended FIFO.
- Each entry gets a queue number; a **serving number** advances at a configured rate. Position = queue number − serving number (one cheap read).
- One instance advances the counter at a time using `pg_try_advisory_lock`; another takes over on the next tick if it dies.
- Admitted customers receive a signed JWT (about 15 minutes, user + show); Inventory verifies it with the public key.
- Clients poll with jitter and see position and estimated wait. The queue closes when the show sells out.
- A traffic-triggered safety-net mode may be added later.

## Consequences

**Positive**

- Fair, persistent and multi-instance using only Postgres.
- Waiting room and Inventory stay decoupled.

**Negative / trade-offs**

- Admission rate must be tuned by load testing.

## Alternatives considered

- Pure FIFO — rejected for scheduled sales (rewards connection speed).
- Redis sorted sets — deferred (ADR-006).

## References

- AWS Virtual Waiting Room solution: https://aws.amazon.com/solutions/implementations/virtual-waiting-room-on-aws/
- SeatGeek virtual waiting room: https://aws.amazon.com/blogs/architecture/build-a-virtual-waiting-room-with-amazon-dynamodb-and-aws-lambda-at-seatgeek/

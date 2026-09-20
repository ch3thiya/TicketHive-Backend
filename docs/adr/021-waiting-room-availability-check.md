# ADR-021: The waiting room's one read of Inventory's availability

- **Status:** Accepted
- **Date:** 2026-09-19

## Context

ADR-002 established that the waiting room never calls Inventory, so queue overload can never affect checkout. That rule holds for the customer path: joining a queue and polling a position never make a runtime call to Inventory.

But a waiting room with no way to learn a show has sold out keeps admitting customers toward tickets that no longer exist. There is no messaging between services until Sprint 3 (ADR-013), so Inventory cannot tell the waiting room a show sold out, and the waiting room cannot ask on the customer path without reintroducing the coupling ADR-002 removed.

## Decision

The waiting room reads Inventory's public, anonymous availability endpoint (`GET /api/inventory/shows/{showId}/availability`) on a slow, fixed interval — a background worker, not a request handler. When every category is at zero, the waiting room closes the queue and reports it as sold out; when the read fails or times out, the queue is left open and the worker tries again on the next tick. This is a narrow, documented exception to ADR-002's no-runtime-call rule, not a reversal of it:

- Read-only. The check never writes to Inventory and Inventory never calls the waiting room.
- On a timer only — never invoked from the join endpoint or the admission path, so it cannot add latency or load to a customer request.
- Through a typed client with the standard resilience handler (timeout, retry, circuit breaker), same as every other synchronous call in this system.
- Fails open: an unreachable Inventory leaves the queue open. A health check failure must never look like a sell-out to a waiting customer.

ADR-002 itself is not edited; accepted records are not rewritten. This entry amends it.

## Consequences

**Positive**

- Waiting customers are told a show sold out instead of being admitted toward nothing.
- The exception is narrow enough that Inventory's checkout path keeps the isolation ADR-002 was written for.

**Negative / trade-offs**

- A small polling load on Inventory's availability endpoint, bounded by the interval and by the number of currently open queues, not by queue traffic.
- A sold-out show can take up to one interval to be reflected in the waiting room.

## Future

Sprint 3's messaging removes this poll: Inventory publishes a sell-out event and the waiting room reacts to it, restoring ADR-002's no-runtime-call rule without exception.

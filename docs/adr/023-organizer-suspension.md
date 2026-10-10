# ADR-023: Organizer suspension is enforced by live lookups

- **Status:** Proposed for Product Owner and engineering review
- **Date:** 2026-10-10

## Context

An admin must be able to suspend an organizer so a problem account can no longer sell tickets, and reinstate it later. The sprint specification was not available when this was built, so the rules below come from the brief, ADR-003 and the existing services. ADR-003 expected a projection table in Catalog; the code has always used live HTTP lookups instead, and this decision keeps that approach.

## Decision

- **Identity owns the state.** `user_accounts.approval_status` gains the value `suspended` (`approved` ↔ `suspended` only). Each change writes an append-only row to `organizer_status_audit` (actor `sub`, reason, `timestamptz`) in the same transaction, under a row lock. Repeating a request is a harmless no-op that keeps the first reason. The audit table has no foreign key so history survives deletion of the account.
- **Internal contract.** `GET /internal/identity/organizers/{sub}`, `/by-id/{id}` and `POST /internal/identity/organizers/status` return `approved` or `suspended`. Callers treat any other value as unavailable and fail closed.
- **Catalog management.** `ActiveOrganizer` refuses a suspended organizer with 403 ProblemDetails (`code: OrganizerSuspended`) on every write. `OrganizerRead` lets a suspended organizer read their own events, cancellation progress and `GET /api/catalog/organizer/status`. Admin cancel endpoints are unaffected. Suspension reasons are never returned to organizers or customers.
- **Sales.** Inventory asks Catalog `GET /internal/catalog/shows/{id}/sales-eligibility` after its local admission gate and before the hold transaction. Catalog checks that the event is Published, the show is Active and the organizer is active (live Identity lookup). Nothing is cached on this path. If the answer cannot be obtained the hold is refused with 503 and `Retry-After`.
- **Separate from cancellation.** Suspension writes nothing in Inventory. The `cancelled_shows` tombstone from ADR-022 is untouched, so reinstating an organizer never reopens a cancelled show, and drafts stay drafts.
- **Existing holds (provisional, needs Product Owner confirmation).** Holds and orders already in checkout when the organizer is suspended may finish. Freeze, convert and release do not look at organizer status. Nothing is refunded, cancelled or released automatically. New holds are refused.
- **Display.** Public event responses carry `salesSuspended`, computed from a batch Identity lookup cached for `Services:Identity:ListingCacheSeconds` (default 5 s) through `TimeProvider`.

## Consequences

**Positive**

- A status change applies to the next management request or hold with no restart and no invalidation.
- Reversible, auditable, and independent of cancellation.

**Negative / trade-offs**

- **Propagation boundaries.** Management and hold checks are immediate. Customer pages can lag by up to the listing cache window. A hold whose eligibility check passed just before the suspension committed can still be created: the window is one request round trip and is accepted. Closing it would need an Inventory-side fence updated by a durable push from Identity.
- Every hold attempt that passes the local gate makes a Catalog call, which makes an Identity call and two Catalog queries. This is on the high-demand path and should be load tested.
- If Catalog or Identity is down, new holds are refused (503) even for healthy organizers.
- **Waiting room (agreed boundary, no rule change).** The waiting room asks Catalog only for on-sale time and the high-demand flag, never for organizer status. Customers can still join a queue and be admitted for a suspended organizer's high-demand show; the hold they then attempt is refused with 409 and the event page shows the sales-unavailable notice. Event pages already disable Buy Now for suspended organizers, so queues form only for customers who reach the queue without that page state. Making the waiting room refuse joins is a separate decision for the Product Owner.
- **Entry validation.** Booking's ticket reads and validation do not depend on organizer status, so sold tickets stay viewable and scannable during suspension.
- Asgardeo's approval attribute is not changed; Identity's database is authoritative and tokens stay valid until each request is checked.
- Seated tickets are out of scope (see ADR-022).

## Alternatives considered

- **Identity pushes status to Inventory with an outbox and Inventory-local fence under a lock** — no race window, but needs a second durable pipeline and new service credentials. Revisit if the window is unacceptable.
- **Cache organizer status in Inventory or Catalog** — rejected for management and holds because it delays enforcement; allowed only for display.
- **Release all unpaid holds on suspension** — rejected for now: it could void payments in flight.

## References

- ADR-003, ADR-005, ADR-009, ADR-012, ADR-022
# ADR-027: Seat holds follow the hold lifecycle

- **Status:** Proposed for engineering review
- **Date:** 2026-10-11

## Context

Seated shows (ADR-026) need customers to choose exact seats. A seat must belong to at most one active hold or sold ticket, a multi-seat selection is all-or-nothing, availability is never cached, and seats must follow the same expiry, freeze, payment and cancellation rules as general-admission (GA) tickets.

## Decision

- **Seats sit alongside the counters.** A seated hold still writes `hold_items` (one row per category, quantity = seats in it) and decrements `stock.available`, so quotas, the expiry sweeper, freeze, convert, release and cancellation keep working unchanged. It additionally sets `show_seats.status = 'Held'` and `show_seats.hold_id`. A check constraint keeps a seat `Available` exactly when it has no hold.
- **The hold's status drives the seat, in the database.** A trigger on `holds` moves the seats in the same transaction: `Expired` or `Cancelled` returns them to `Available` (this also returns sold seats when an order is cancelled), `Converted` makes them `Sold`. Every caller, present and future, gets the behaviour, and a repeat transition changes nothing.
- **Seated allocation sits behind the allocation seam.** `SeatedAllocationStrategy` decrements stock only for `Seated` stock (the GA strategy still requires `GA`) and claims seats. GA code paths and SQL are unchanged.
- **Lock order is seats, then stock.** A hold locks the requested seats in id order and refuses at once if any is not `Available`, so it only goes on to lock stock when no other hold's lifecycle can touch those seats. Replacing a layout locks seats before stock for the same reason. This rules out the deadlocks between new holds, the sweeper, cancellation and enabling a layout.
- **A seat request is all-or-nothing.** If any requested seat is missing or taken, nothing is held, the quota and stock changes roll back, and the 409 names the unavailable seats.
- **Modes are exclusive.** A seated show accepts only seat ids; a GA show accepts only items.
- **Limits.** The seat count counts toward `max_per_customer`.
- **Seat map is public and uncached.** `GET /api/inventory/shows/{id}/seats` (structure and states) and `/seats/states` (states only) carry no customer data and `Cache-Control: no-store`; the gateway's per-client rate limit applies. Clients poll as they do for availability.
- **Tickets carry the seat.** The internal hold response lists the seats; Booking stores them on the order and gives each ticket one seat (section, row, number).
- **Real sales.** Seated shows are only safe to sell once this lifecycle, the Booking ticket seats and the cancellation return are all deployed. Until then no show should be enabled as seated outside test environments.

## Consequences

- Availability counters and seat states can be reconciled with a query (`available = capacity - held - sold` per category).
- Seated holds of one category serialise on its stock rows, as GA holds do.

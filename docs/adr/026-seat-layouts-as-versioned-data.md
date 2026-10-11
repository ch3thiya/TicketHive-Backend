# ADR-026: Seat layouts are versioned, validated data owned by Catalog

- **Status:** Proposed for engineering review
- **Date:** 2026-10-11

## Context

A show is either general admission (GA) or seated, never both. An organizer asks for a seat map; an admin defines the layout (sections, rows, seats per row), maps ticket categories to sections and enables it. Seats are only ever sold from Inventory (ADR-006), and a layout must not change once any seat has been held or sold. Drag-and-drop editing, organizer-drawn layouts and shared templates are out of scope.

## Decision

- **Catalog owns requests and layouts.** `seat_map_requests` records the organizer's request (Pending, Completed or Rejected, with the rejection reason). `seat_layouts` stores each version as validated JSONB: a list of sections, each with a name, a ticket category, a row count and a seats-per-row count. `shows.seating_mode` is `GA` or `Seated`. One Pending request per show and one Enabled layout per show are enforced by partial unique indexes; versions are numbered per show under a lock on the show row.
- **Seats are generated, not drawn.** Row labels run A, B, C, ... Z, AA, AB, ... and seat numbers run 1..n within each row, so duplicate seats cannot be produced by one section. Duplicate section names are rejected.
- **Validation rules** (pure code in `Models/`): at least one section; unique section names; every section has an existing, active category of the same show; positive, bounded dimensions; the seats mapped to each category equal that category's capacity; the total equals the show's ticket total.
- **Inventory owns seats and the lock.** Enabling calls `PUT /internal/inventory/shows/{id}/seats`. In one transaction Inventory locks the show's stock rows, refuses with 409 if any hold is open or any ticket is sold, switches the stock rows to `allocation_mode = 'Seated'` and inserts one `show_seats` row per seat. A repeat call for the same version changes nothing. Because GA holds take the same stock row lock, a hold and an enable cannot interleave.
- **Enabled seated shows reject GA holds.** The GA allocation update requires `allocation_mode = 'GA'`. Selling seats arrives with the seat-selection story; until then an enabled seated show cannot be sold.
- **Enable is a two-step, retry-safe flow.** Catalog validates, calls Inventory (idempotent per show and version), then commits Enabled/Completed/Seated in one transaction. If Inventory is unreachable the answer is 503 and the layout stays Draft; the admin retries.
- **A new version** can replace an enabled one only while every seat is still Available; Inventory refuses otherwise (the lock).
- Ticket category capacity and price changes are refused while a show is Seated.

## Consequences

- The layout is data, so a layout bug is fixed by a new version, not a migration.
- Inventory never trusts Catalog's arithmetic: it re-checks seat counts against its own stock capacities.
- Existing GA behaviour, cancellation and suspension rules are unchanged.

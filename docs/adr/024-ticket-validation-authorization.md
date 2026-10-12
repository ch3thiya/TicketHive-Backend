# ADR-024: Ticket validation is limited to the owning organizer and admins

- **Status:** Proposed for Product Owner and engineering review
- **Date:** 2026-10-10

## Context

`POST /api/booking/tickets/{code}/validate` only required a signed-in user, so any customer could mark any ticket used. The already-used response also named the user who scanned it. A ticket knows its show but not its organizer; ownership lives in Catalog and Identity (ADR-003). Door staff accounts (an organizer with more than one user) are planned but not built.

## Decision

- The route requires an `Organizer` or `Admin` group in the token. Customers get 403 before anything else runs.
- **Admins** may validate tickets for any show.
- **Organizers** may validate only tickets for shows they own. Booking asks Catalog (`GET /internal/catalog/shows/{id}/entry-access?sub=`), which resolves the caller's organizer through Identity and compares it with the show's event. The check runs on every request and is never cached.
- **Suspended organizers keep this right.** Suspension never voids tickets or stops entry (ADR-023), so Catalog treats approved and suspended organizers alike. Cancelled shows are not special-cased: a voided ticket is refused by its own status.
- **Fail closed.** If Catalog or Identity cannot answer, including resilience timeouts and an open circuit, validation returns 503 with `Retry-After` and writes nothing.
- A caller who is not allowed gets 403 before any ticket status is revealed, so they cannot tell whether a ticket is used or voided. Unknown codes still return 404, without echoing the code.
- The already-used response gives the time only, never the identifier of whoever scanned it.
- Door staff stay out of scope. `EntryAccessService` in Catalog is the single place to add staff membership later; Booking will not need to change.

## Consequences

**Positive**

- Only people with a legitimate claim to a show can mark its tickets used, and the decision is always current.
- The atomic `validate_ticket` function, the order lock and cancellation behaviour are unchanged.

**Negative / trade-offs**

- Every scan makes a Catalog call, which makes an Identity call. Door queues are bursty, so this should be load tested. A short positive cache or storing the organizer on the ticket at issuance are the options if latency is a problem; both were declined for now.
- An Identity or Catalog outage stops non-admin scanning. Admins are unaffected.
- Booking's service token needs `catalog:read`, and `CatalogService:BaseUrl` must be set. Deploy Catalog before Booking, otherwise validation returns 503 (safe, not open).

## Alternatives considered

- **Check the organizer role only** — rejected: any organizer could scan any show.
- **Cache positive decisions for 60 seconds** — declined: ownership must be checked on every request.
- **Return 404 for non-owners** — declined: 403 is clearer for door staff and the code is unguessable.

## References

- ADR-003, ADR-023
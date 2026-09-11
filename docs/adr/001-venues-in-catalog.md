# ADR-001: Venues live in the Catalog service

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Venues are reference data that change rarely and are read constantly, mostly together with events and shows. The Sprint 1 code has no venue storage at all (shows accept a free-text venue GUID), so a home for venues must be chosen.

## Decision

Venues are owned by the **Catalog** service; there is no separate Venue service. Shows reference a validated `venue_id`. (Where seat layouts live will be decided when seat maps are built in Sprint 3.)

## Consequences

**Positive**

- One fewer deployable, CI pipeline and database for a four-person team.
- Shows can validate `venue_id` locally (fixes the Sprint 1 free-text GUID).

**Negative / trade-offs**

- Catalog grows; keep venue code in its own files (`VenuesController`, `VenueService`, `VenueRepository`) so it can be extracted later if needed.

## Alternatives considered

- Separate Venue service — rejected: adds operational overhead for about four endpoints.

# ADR-006: Live availability via polling

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Azure SignalR's free tier allows only 20 concurrent connections and 20,000 messages per day; Standard costs roughly USD 50 per month. A load test would exceed the free tier immediately.

## Decision

- Availability is a lightweight endpoint the client polls about every 3 seconds, read directly from the database.
- SignalR can replace polling later without changing Inventory's data model.

## Consequences

**Positive**

- Zero cost, trivial to scale horizontally, satisfies SCRUM-78's no-caching rule.

**Negative / trade-offs**

- Up to a few seconds of staleness; more requests than push.

## Alternatives considered

- Azure SignalR Service Standard — deferred for cost.

## References

- Azure SignalR Service limits: https://github.com/MicrosoftDocs/azure-docs/blob/main/includes/signalr-service-limits.md

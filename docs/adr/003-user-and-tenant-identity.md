# ADR-002: User ID is the Asgardeo sub; the organizer ID is a separate tenant ID

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Sprint 1 Catalog stores an MD5 hash of the Asgardeo user ID while Identity uses its own UUID, which breaks cross-service references. TicketHive is multi-tenant, and door staff (US13) will create organizers with more than one user.

## Decision

- **User ID = Asgardeo `sub`**, stored as text in every service. Identity's internal UUID stays internal.
- **Tenant ID = organizer ID**, issued by Identity on approval, separate from the user ID.
- Catalog keeps a local table mapping `sub` → organizer ID + status, refreshed through Identity (cached lookups / events), so suspension takes effect quickly and authorization and tenant scoping happen in one lookup.

## Consequences

**Positive**

- Any service reads the user from the token without lookups.
- Staff accounts per organizer need no schema rewrite later.

**Negative / trade-offs**

- A small projection table to maintain in Catalog.

## Alternatives considered

- Using the `sub` as the tenant ID — rejected: breaks as soon as an organizer has two users.

## References

- Azure Architecture Center, multitenant solutions (tenants are distinct from users): https://learn.microsoft.com/en-us/azure/architecture/guide/multitenant/overview

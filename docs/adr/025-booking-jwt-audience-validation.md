# ADR-025: Booking validates JWT audience and token type per scheme

- **Status:** Proposed for engineering review
- **Date:** 2026-10-11

## Context

Booking had one JWT scheme with `ValidateAudience = false`, shared by user tokens and the machine-to-machine (M2M) tokens used for internal cancellation calls. Any token from the tenant's issuer was accepted, whatever application it was minted for. The role gate on ticket validation reads the `groups` claim, so the gap mattered.

Verified Asgardeo evidence: user access tokens carry `aud` = the SPA client ID, `typ` = `at+jwt`, `aut` = `APPLICATION_USER`. Catalog's M2M tokens carry `aud` = Catalog's client ID, `typ` = `at+jwt`, `aut` = `APPLICATION`. Not verified: tokens of the backend M2M client, organizer/admin token claims, and the deployed settings.

## Decision

- Two schemes. `Bearer` (default) is for users, with `groups` as the role claim. `Internal` is for M2M callers. The `CancellationInternal` policy now names `Internal`, so a user token never reaches it, and a machine token never reaches user endpoints.
- Both schemes validate issuer, signature, lifetime, an explicit audience allowlist, and the token type `at+jwt` (ID tokens are rejected).
- Allowlists are configuration: `Jwt:UserAudiences` (plus the legacy `Jwt:Audience`) and `Jwt:InternalAudiences`. Values may be an array or comma-separated.
- Startup fails when either list is empty, holds a wildcard or whitespace, or when the two lists share a value. There is no default audience and no way to switch validation off.
- The shared policy helper takes an optional scheme, so other services keep their behaviour until they are moved separately. Identity and Catalog hardening is out of scope.

## Consequences

- Every Booking environment needs `Jwt__InternalAudiences` set to the client ID of each service that calls Booking's internal endpoints. Until it is set Booking will not start.
- Adding a new internal caller means adding its client ID to the list.
- Catalog's M2M app must also be granted `booking:write`, otherwise internal cancellation is refused by the policy (403), not by the scheme.
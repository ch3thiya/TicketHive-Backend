# ADR-015: Modularity and decoupling rules inside each codebase

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Decoupling between services is not enough; code inside each service and the frontend must stay modular so features can change independently.

## Decision

- A class never uses another area's repository or tables directly — only its service interface (e.g. venue code never queries event tables).
- `Models/` references no infrastructure (no Npgsql, ASP.NET Core, HTTP); it depends on interfaces implemented in `Db/` and `Clients/` (dependency inversion).
- `TimeProvider` instead of `DateTime.UtcNow`; Options pattern for configuration.
- Services never reference each other's projects. Each caller owns its HTTP request/response models (tolerant reader).
- Rules enforced by NetArchTest architecture tests in CI (e.g. `Models` must not reference Npgsql; Inventory must not reference Catalog; BuildingBlocks must not reference any service).
- Frontend: existing folders, reuse shared components, protected calls only through the auth helper.

## Consequences

**Positive**

- Changes stay local; business rules are unit-testable.
- Violations fail the build instead of relying on review.

**Negative / trade-offs**

- More interfaces and a little more code up front.

# ADR-015: Code structure and shared libraries

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Seven services by four people need consistency; Sprint 1 scattered business rules through service methods and used public init-db endpoints for schema creation.

## Decision

- Two repos: backend (all services) and frontend. The **existing service layout is kept**: `Controllers/`, `Services/`, `Models/`, `Db/` (plus `Clients/`). Domain entities, state machines and rules live in `Models/`; DbUp scripts in `Db/Migrations/`.
- `services/BuildingBlocks`: technical plumbing only (OpenTelemetry, health checks, ProblemDetails, resilience defaults, TimeProvider) exposed through `AddServiceDefaults()` and `MapDefaultEndpoints()`.
- Versioned SQL migrations with DbUp; public init-db endpoints removed.
- Tests: `tests/<Name>.Service.Tests/` (unit at root, `Integration/` for Testcontainers), `tests/Architecture.Tests/`.
- Frontend keeps its existing folders; protected API calls go through the authenticated fetch helper.

## Consequences

**Positive**

- Familiar layering, consistent across services.
- Shared plumbing written once.

**Negative / trade-offs**

- Shared libraries must stay free of business logic (enforced by ADR-020).

## Alternatives considered

- Feature folders or four-project Clean Architecture per service — rejected: the team rule is not to restructure the repository.

# ADR-016: Engineering standards

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Quality must not depend on individual habits.

## Decision

- APIs follow the existing convention: public `/api/<service>/<resource>` (e.g. `/api/inventory/holds`), internal `/internal/<service>/<resource>` (never routed by nginx). OpenAPI per service, ProblemDetails, `Idempotency-Key` on retryable commands.
- Internal endpoints require Asgardeo client-credentials tokens.
- No secrets in code; Container Apps secrets and untracked `.env`.
- Nullable reference types, `.editorconfig` + `dotnet format` in CI; ESLint on the frontend.
- Git: developer-created `feature/` or `fix/` branches from `dev` (with `scrum-<n>` when a Jira item exists) → pull request to `dev` → `main` per sprint; one approval and green CI.
- Commits: prefix, short subject, 1–3 line description, `Refs:` footer; committed in small logical parts.
- ADRs in `/docs/adr` using MADR; accepted records are not rewritten — changes are new records that amend or supersede them.

## Consequences

**Positive**

- Consistent, reviewable, secure codebase.

**Negative / trade-offs**

- Initial setup effort.

## References

- MADR: https://adr.github.io/madr/

# ADR-004: Data conventions for every service

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Sprint 1 deleted and re-inserted ticket categories on edit, changing their IDs. Inventory, holds and tickets will reference those IDs, so data rules must be consistent across services.

## Decision

- Entity IDs are **UUIDv7** generated in code (`Guid.CreateVersion7()`), never recycled.
- Referenced rows are never deleted and re-inserted; they change state instead (for example Active → Retired).
- Customer-facing codes are separate from IDs: short readable order references; long, random, unguessable ticket codes.
- Money is `NUMERIC` with a currency code (LKR); prices are snapshotted into holds; client-supplied prices are never trusted.
- All timestamps are `timestamptz` in UTC, displayed in the venue's time zone.
- Every tenant-scoped row carries `organizer_id`; every query filters on it (Postgres row-level security may be added later).

## Consequences

**Positive**

- Stable references across services.
- Time-ordered IDs keep indexes efficient and can be used in idempotency records and logs before the row is inserted.

**Negative / trade-offs**

- Existing Sprint 1 test data is reset rather than migrated.

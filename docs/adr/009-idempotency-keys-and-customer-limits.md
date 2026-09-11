# ADR-009: Idempotency keys and per-customer ticket limits

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Clients retry after timeouts, and one customer can send parallel requests. Both could double-hold or bypass ticket limits.

## Decision

- `POST /api/inventory/holds` requires an `Idempotency-Key`, stored with a unique constraint; a retry returns the original hold.
- The per-customer, per-show ticket limit (default about 6) is enforced with a conditional update on a small quota row inside the hold transaction.
- The price is copied into the hold; admission tokens are required for high-demand shows.

## Consequences

**Positive**

- Safe retries for clients and services.
- Limits hold under parallel requests.

**Negative / trade-offs**

- Idempotency records need a retention clean-up job.

## References

- Stripe, designing robust APIs with idempotency: https://stripe.com/blog/idempotency

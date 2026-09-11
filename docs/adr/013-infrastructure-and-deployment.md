# ADR-009: Infrastructure and deployment

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

The deployment must fit the Azure for Students budget.

## Decision

- Services on **Azure Container Apps** (already used); Inventory and Waiting room keep at least 1 replica and run 2 during demos to prove multi-instance behaviour.
- **One Azure Database for PostgreSQL Flexible Server (Burstable B1MS)** with one database per service; capped connection pools; stop the server when idle.
- No managed Redis or SignalR.
- Migrations run as a Container Apps job (DbUp) before rollout.
- Local: docker-compose with all services, Postgres and the Aspire Dashboard.
- CI: no image pushes from feature branches; one deploy workflow.
- Messaging infrastructure is decided with Sprint 3 work (team rule: Kafka is self-hosted, never managed).

## Consequences

**Positive**

- Sprint 2 needs no new Azure services.
- Stays within student credit plus free tiers.

**Negative / trade-offs**

- The shared database server weakens isolation between services — documented and accepted.

## References

- Azure for Students: https://azure.microsoft.com/en-us/free/students

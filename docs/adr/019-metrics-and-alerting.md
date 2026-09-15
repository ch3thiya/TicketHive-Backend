# ADR-019: Business metrics, dashboard and alerting

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Standard request metrics do not show whether stock, queues and payments behave correctly.

## Decision

- Business metrics in Sprint 2: holds created / rejected (sold out, limit) / expired; stock invariant (`0 ≤ available ≤ capacity`, checked every minute); queue length and admission rate.
- Alerts (few, actionable): stock invariant violated; error rate > 5%; failing health probe.
- One "on-sale" Azure Workbook dashboard for load tests and the sprint demo.

## Consequences

**Positive**

- Fault-tolerance claims are visible in real time.

**Negative / trade-offs**

- Metric and alert maintenance.

# ADR-013: Telemetry backends within budget

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Production-grade monitoring must stay within free ingestion allowances.

## Decision

- Local: standalone Aspire Dashboard container in docker-compose (development only, in-memory).
- Azure: workspace-based Application Insights via Microsoft's OpenTelemetry distro; daily cap; sampling of successful traces (keep errors); Warning-level logs; no duplicate Container Apps log shipping.

## Consequences

**Positive**

- Rich local debugging; production monitoring within the 5 GB/month free ingestion.

**Negative / trade-offs**

- Aspire Dashboard is not for production use.

## Alternatives considered

- Self-hosted Prometheus/Grafana/Jaeger — rejected: more infrastructure to run.

## References

- Standalone Aspire Dashboard: https://learn.microsoft.com/dotnet/aspire/fundamentals/dashboard/standalone
- Azure Monitor pricing: https://azure.microsoft.com/en-us/pricing/details/monitor/

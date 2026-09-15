# ADR-017: OpenTelemetry as the observability standard

- **Status:** Accepted
- **Date:** 2026-09-11

## Context

Diagnosing distributed failures requires correlated traces, metrics and logs across services.

## Decision

- Every service uses the OpenTelemetry SDK (ASP.NET Core, HttpClient and Npgsql instrumentation), configured once in `AddServiceDefaults()`; the OTLP exporter is enabled only when an endpoint is configured.
- W3C trace context propagates across HTTP calls between services, so a publish (Catalog → Inventory) appears as one trace.
- Structured logging with message templates; every log carries the trace ID; no tokens, secrets or full emails in logs.

## Consequences

**Positive**

- Vendor-neutral; backend can change without code changes.
- End-to-end traces across async flows.

**Negative / trade-offs**

- Instrumentation adds a few dependencies to BuildingBlocks.

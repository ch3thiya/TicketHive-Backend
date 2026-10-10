# Architecture Decision Records — Sprint 2 baseline

Format: [MADR](https://adr.github.io/madr/). All records below: **Accepted** (2026-09-11).
Only decisions that govern Sprint 2 work are recorded here. Decisions for later work (seat layouts, checkout and payment, messaging) are added as new records when that work starts.
Accepted records are never rewritten; a changed decision is a new record that amends or supersedes the old one.

| ADR | Decision |
|---|---|
| [001](001-venues-in-catalog.md) | Venues live in the Catalog service |
| [002](002-waiting-room-as-separate-service.md) | The waiting room is its own service |
| [003](003-user-and-tenant-identity.md) | User ID is the Asgardeo sub; the organizer ID is a separate tenant ID |
| [004](004-data-conventions.md) | Data conventions for every service |
| [005](005-rest-for-synchronous-communication.md) | REST for synchronous communication |
| [006](006-postgres-as-inventory-source-of-truth.md) | PostgreSQL is the inventory source of truth; Redis can be added later |
| [007](007-atomic-conditional-holds.md) | Atomic conditional holds |
| [008](008-hold-expiry-via-database-sweeper.md) | Hold expiry via a database sweeper |
| [009](009-idempotency-keys-and-customer-limits.md) | Idempotency keys and per-customer ticket limits |
| [010](010-availability-via-polling.md) | Live availability via polling |
| [011](011-waiting-room-queue-and-admission-tokens.md) | Waiting room: queue numbers, serving counter and admission tokens |
| [012](012-resilience-and-degradation.md) | Resilience mechanisms and degradation rules |
| [013](013-infrastructure-and-deployment.md) | Infrastructure and deployment |
| [014](014-quality-targets-and-testing.md) | Quality targets and testing strategy |
| [015](015-code-structure-and-shared-libraries.md) | Code structure and shared libraries |
| [016](016-engineering-standards.md) | Engineering standards |
| [017](017-opentelemetry.md) | OpenTelemetry as the observability standard |
| [018](018-telemetry-backends.md) | Telemetry backends within budget |
| [019](019-metrics-and-alerting.md) | Business metrics, dashboard and alerting |
| [020](020-modularity-and-decoupling-rules.md) | Modularity and decoupling rules inside each codebase |
| [021](021-waiting-room-availability-check.md) | The waiting room's one read of Inventory's availability (amends 002) |
| [023](023-organizer-suspension.md) | Organizer suspension enforced by live lookups |

## Template

Copy `template.md` for new decisions and add a row above in the same pull request.

# Mutation testing

TicketHive uses Stryker.NET to measure whether important unit tests detect faults in business rules, rather than relying on line coverage alone. The tool is pinned in the repository's local .NET tool manifest, so local and CI runs use the same version.

## Scope

The mutation job samples deterministic, high-risk rules across four services:

| Service | Production rules | Risk covered |
| --- | --- | --- |
| Catalog | Event and show transitions, sales eligibility, show and venue seat maps | Invalid state changes, capacity limits, and seat-layout boundaries |
| Booking | Cancellation rules and show-sales aggregation | Incorrect cancellation authorization/state and incorrect totals |
| Identity | Organizer status transitions | Invalid organizer state changes |
| Notification | Reminder recipient, timing, and retry rules | Bad recipients and incorrect reminder scheduling |

This is deliberately a focused mutation scope, not every line of every service. Mutating controllers, generated code, database wiring, and infrastructure would make the job much slower while largely duplicating integration tests. The selected methods are deterministic decision logic with meaningful boundary conditions and fast unit tests, so a surviving mutant is actionable.

Catalog string mutations are excluded. Tests treat stable error codes as the contract; changes to explanatory message wording should not fail a business-rule test.

## Quality gate

Each suite has these thresholds:

- 90% or above: high
- 80% to 89.99%: acceptable but highlighted
- Below 80%: fail the mutation job

Measured on 20 October 2026 with Stryker.NET 5.0.0:

| Suite | Killed | Survived | Timeout | Score |
| --- | ---: | ---: | ---: | ---: |
| Catalog | 122 | 11 | 3 | 91.91% |
| Booking | 27 | 0 | 0 | 100.00% |
| Identity | 5 | 0 | 0 | 100.00% |
| Notification | 40 | 4 | 0 | 90.91% |

Stryker counts timed-out mutants as detected. Catalog's three timeouts are arithmetic changes to finite row-label/seat-expansion loops. They are retained in the report rather than hidden.

The remaining Catalog survivors affect diagnostic message selection or redundant validation flow; the stable error codes and accept/reject outcome do not change. The remaining Notification survivors are equivalent integer-boundary or timestamp-branch mutations: later email checks still reject the same inputs, and equal timestamp branches return the same value. Reports remain available as CI artifacts for review.

## Test improvements driven by mutation analysis

Initial runs revealed assertions that ordinary passing tests and coverage did not expose. Tests were added for:

- every canonical and non-canonical organizer status;
- payment-pending and failed-order cancellation refusals;
- aggregation of multiple used-ticket rows in one category;
- email dot placement and the exact 255-character limit;
- seat-section count, name length, dimensions, total capacity, and venue-capacity boundaries;
- stable layout error codes instead of incidental message text.

Booking and Identity consequently reached 100%; Notification reached 90.91%; and the broader Catalog sample reached 91.91%.

## Running locally

From the repository root:

```bash
dotnet tool restore
dotnet restore TicketHive.slnx
cd tests/Booking.Service.Tests
dotnet stryker
```

Replace the test-project directory with any of the four suites in the table. HTML and JSON reports are written below that project's ignored `StrykerOutput` directory.

## Continuous integration

`.github/workflows/mutation-tests.yml` runs the four suites independently on GitHub-hosted runners for relevant pull requests and manual runs. Each suite has a 30-minute limit, fails below the 80% gate, and uploads its HTML and JSON reports even when the job fails. It does not deploy services or contact the Azure production environment, so mutation testing does not consume the project's Azure runtime resources.

# Mutation testing

TicketHive uses Stryker.NET to measure whether important unit tests detect faults in business rules, rather than relying on line coverage alone. The tool is pinned in the repository's local .NET tool manifest, so local and CI runs use the same version.

## Scope

The mutation job samples testable rules in every backend service, plus Gateway and the shared BuildingBlocks project:

| Service | Production rules | Risk covered |
| --- | --- | --- |
| Catalog | Event and show transitions, sales eligibility, show and venue seat maps | Invalid state changes, capacity limits, and seat-layout boundaries |
| Booking | Cancellation rules and show-sales aggregation | Incorrect cancellation authorization/state and incorrect totals |
| Identity | Organizer status transitions | Invalid organizer state changes |
| Notification | Reminder recipient, timing, and retry rules | Bad recipients and incorrect reminder scheduling |
| Inventory | Stock initialization and sales eligibility | Invalid stock, lost field values, and stale availability |
| WaitingRoom | Queue joining/transition and token configuration | Incorrect queue numbering or missing signing key |
| Payment | PayHere checkout and signature rules | Wrong payment parameters or acceptance of tampered callbacks |
| Gateway | Client rate limits and CORS cleanup | Incorrect client partitioning or callback throttling |
| BuildingBlocks | PostgreSQL URL normalization and internal-token settings | Wrong connection details or missing service credentials |

This covers all backend projects, but the scores apply only to the selected production files and their focused unit tests. It is not a claim that every line in each service has been mutation-tested. Mutating controllers, generated code, database wiring, and integration paths would make the job much slower while largely duplicating other test layers. The selected methods contain deterministic decisions with meaningful boundary conditions, so a surviving mutant is actionable.

Catalog and Inventory string mutations are excluded. Tests treat stable error codes and accept/reject results as the contract; changes to explanatory exception or validation prose should not fail a business-rule test.

## Quality gate

Each suite has these thresholds:

- 90% or above: high
- 80% to 89.99%: acceptable but highlighted
- Below 80%: fail the mutation job

Measured on 20 October 2026 with Stryker.NET 5.0.0:

| Suite | Killed | Survived | No coverage | Timeout | Score |
| --- | ---: | ---: | ---: | ---: | ---: |
| Catalog | 122 | 11 | 0 | 3 | 91.91% |
| Booking | 27 | 0 | 0 | 0 | 100.00% |
| Identity | 5 | 0 | 0 | 0 | 100.00% |
| Notification | 40 | 4 | 0 | 0 | 90.91% |
| Inventory | 24 | 0 | 0 | 0 | 100.00% |
| WaitingRoom | 44 | 0 | 0 | 0 | 100.00% |
| Payment | 29 | 0 | 0 | 0 | 100.00% |
| Gateway | 9 | 0 | 0 | 0 | 100.00% |
| BuildingBlocks | 46 | 3 | 1 | 0 | 92.00% |

Stryker counts timed-out mutants as detected. Catalog's three timeouts are arithmetic changes to finite row-label/seat-expansion loops. They are retained in the report rather than hidden.

The remaining Catalog survivors affect diagnostic message selection or redundant validation flow; the stable error codes and accept/reject outcome do not change. The remaining Notification survivors are equivalent integer-boundary or timestamp-branch mutations: later email checks still reject the same inputs, and equal timestamp branches return the same value. BuildingBlocks has four equivalent or unreachable changes in PostgreSQL URL parsing: splitting user info always returns at least one element, and a URI port is either positive or `-1`. Reports remain available as CI artifacts for review.

## Test improvements driven by mutation analysis

Initial runs revealed assertions that ordinary passing tests and coverage did not expose. Tests were added for:

- every canonical and non-canonical organizer status;
- payment-pending and failed-order cancellation refusals;
- aggregation of multiple used-ticket rows in one category;
- email dot placement and the exact 255-character limit;
- seat-section count, name length, dimensions, total capacity, and venue-capacity boundaries;
- stable layout error codes instead of incidental message text;
- zero-price stock initialization, full stock-field mapping, injected-clock expiry sweeping, and failure logging;
- queue opening and on-sale transition boundaries;
- PayHere configured URLs, two-decimal hashing, and invalid-signature warnings;
- gateway client-IP partitions and precise callback exemptions;
- individual missing service credentials and PostgreSQL URL defaults.

The resulting scores for all nine backend projects are shown above.

## Running locally

From the repository root:

```bash
dotnet tool restore
dotnet restore TicketHive.slnx
cd tests/Booking.Service.Tests
dotnet stryker
```

Replace the test-project directory with any of the nine suites in the table. HTML and JSON reports are written below that project's ignored `StrykerOutput` directory.

## Continuous integration

`.github/workflows/mutation-tests.yml` runs the nine suites independently on GitHub-hosted runners for relevant pull requests and manual runs. Each suite has a 30-minute limit, fails below the 80% gate, and uploads its HTML and JSON reports even when the job fails. It does not deploy services or contact the Azure production environment, so mutation testing does not consume the project's Azure runtime resources. GitHub Actions minutes still apply.

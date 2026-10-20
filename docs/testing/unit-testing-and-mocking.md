# Unit testing and mocking

This suite tests the whole backend rather than one feature. The CI split is intentional:

- **Unit and architecture tests** run without Docker and isolate collaborators with Moq, in-memory handlers, fake clocks, and in-process HTTP hosts.
- **Integration tests** use Testcontainers and real PostgreSQL behavior for repositories, constraints, migrations, concurrency, and API boundaries.
- **Load tests** are the separate k6 stage under `load-tests/`.

## Current unit scope

Run on 20 October 2026 from `feature/unit-testing` with:

```bash
dotnet test TicketHive.slnx --configuration Release \
  --filter "FullyQualifiedName!~Integration"
```

| Test assembly | Passing cases |
|---|---:|
| Architecture | 9 |
| BuildingBlocks | 25 |
| Gateway | 26 |
| Identity | 98 |
| Catalog | 323 |
| Inventory | 80 |
| WaitingRoom | 30 |
| Booking | 65 |
| Payment | 28 |
| Notification | 51 |
| **Total** | **735** |

The count is not the quality target by itself. The important cases assert domain outcomes, failure paths, authorization boundaries, time behavior, and collaborator calls.

The later mutation-testing branch adds boundary and interaction tests across the same backend projects. On the combined branch, the command above passes **778 non-integration cases** (20 October 2026).

## Mocking and isolation matrix

| Area | Representative tests | Isolation/evidence |
|---|---|---|
| Shared HTTP/auth | `InternalServiceTokenClientTests` | Protected `HttpMessageHandler` mock, token caching, expiry, malformed and failed responses |
| Identity | `OrganizerSuspensionServiceTests`, registration/sync tests | Repository and SCIM client mocks; fake time; verifies local/remote side effects |
| Catalog | `EventServiceTests`, `SeatMapServiceTests` | Repository, Inventory, organizer, and logger mocks; status transitions and compensation paths |
| Inventory | `HoldServiceTests`, `SeatHoldServiceTests`, eligibility tests | Repository/client mocks and fake time; stock, hold, admission, and sales rules |
| Waiting room | `QueueServiceTests`, client tests | Repository and downstream HTTP isolation; queue/admission outcomes and degraded responses |
| Booking | `TicketServiceTests`, PayHere and access-client tests | Repository, producer, external client, and fake-time isolation; duplicate prevention and ticket rules |
| Payment | `PaymentControllerTests`, `PayHereServiceTests` | Strict repository/gateway/Kafka mocks; callback tampering, signatures, persistence, events, defaults, and fixed time |
| Notification | `NotificationServiceTests`, reminder unit tests | Mock HTTP transport, repositories, email behavior, retry/eligibility rules |
| Gateway | `EdgeRoutingTests`, `RateLimitingTests` | In-process host with controlled upstream behavior; routing, CORS stripping, and client budgets |
| Architecture | `Architecture.Tests` | Reflection/static rules for layering, service independence, domain purity, and time-provider use |

Strict mocks in the Payment tests check both sides of an interaction: expected calls must occur, and rejected callbacks must never persist state or publish an event. This guards against tests that only assert an HTTP status while missing a harmful side effect.

## Coverage evidence

CI collects Cobertura for the unit and integration runs separately. Unit coverage uses `tests/unit-coverage.runsettings` so its denominator represents unit-testable business/application logic; startup wiring, generated code, migrations, and PostgreSQL repositories belong to the integration report.

The targeted Payment work changed its raw service-package unit coverage from **15.15% lines / 9.31% branches** to **26.48% lines / 20.80% branches**. That raw package number still includes infrastructure outside unit scope. With `tests/unit-coverage.runsettings` applied, the Payment unit scope is **72.95% lines / 85.89% branches**. The main classes report:

| Class/method group | Line coverage | Branch coverage |
|---|---:|---:|
| `PaymentController.CreateCheckout` | 100% | 100% |
| `PaymentController.PaymentNotification` | 100% | 90% |
| `PaymentController.ConfirmSandboxPayment` | 100% | 50% |
| `PayHereService` | 100% | 100% |

The remaining sandbox branch is the current fallback for an unknown transaction. It should be handled as a product decision (return 404 versus creating a zero-value success), not hidden by a test written to bless questionable behavior.

## Reproduce CI locally

```bash
dotnet restore TicketHive.slnx
dotnet build TicketHive.slnx --configuration Release --no-restore

dotnet test TicketHive.slnx --configuration Release --no-build \
  --filter "FullyQualifiedName!~Integration" \
  --settings tests/unit-coverage.runsettings \
  --collect:"XPlat Code Coverage" \
  --results-directory ./test-results

dotnet test TicketHive.slnx --configuration Release --no-build \
  --filter "FullyQualifiedName~Integration" \
  --collect:"XPlat Code Coverage" \
  --results-directory ./test-results
```

The generated `coverage.cobertura.xml` files and TRX files are uploaded by the CI `test-results` artifact for marking evidence and SonarQube import.

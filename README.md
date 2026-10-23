# TicketHive Backend

TicketHive is a ticket booking platform designed to handle events, seat availability, waiting rooms, bookings, payments, and notifications.

This repository contains the backend services for TicketHive. The backend is implemented using ASP.NET Core and follows a microservices architecture.

The planned system consists of six main services:

- Identity Service
- Catalog Service
- Inventory Service (planned)
- Booking Service (planned)
- Payment Service (planned)
- Notification Service (planned)

Identity and Catalog are currently implemented in this repository. Inventory, Booking, Payment, and Notification are planned for future sprints.

Development of the services will be completed incrementally across the project sprints.

## Technologies

- ASP.NET Core
- PostgreSQL
- Apache Kafka
- Docker
- Azure
- GitHub Actions
- React

> **Note:** React is primarily used by the TicketHive frontend. It is listed here to document the technologies used across the overall TicketHive system.

## Architecture

TicketHive uses a microservices architecture.

```text
                         TicketHive Frontend
                                |
                                v
                    +------------------------+
                    |      Backend APIs      |
                    +------------------------+
                                |
            +---------------------+
            |          |          |
            v          v          v
       Identity    Catalog   Future services
       Service     Service   (planned)

                 Apache Kafka
          ----------------------------
          Asynchronous communication

                 PostgreSQL
          ----------------------------
          Service-specific databases

                    Docker
          ----------------------------
          Containerized services

                     Azure
          ----------------------------
          Cloud deployment
```

Each microservice is responsible for its own functionality and data.

## Repository Structure

The backend repository is organized around the individual microservices.

```text
TicketHive-Backend/
├── TicketHive.slnx
├── services/
│   ├── Identity/
│   │   ├── Identity.Service.csproj
│   │   └── Dockerfile
│   └── Catalog/
│       ├── Catalog.Service.csproj
│       └── Dockerfile
├── .dockerignore
├── README.md
└── .github/
    └── workflows/
```

The exact structure may change as the project develops.

## Branching Strategy

TicketHive follows a structured Git branching strategy:

```text
main
     ↑
dev
     ↑
feature/* / fix/*
```

### `main`

`main` contains stable releases of the backend.

- Direct pushes are not allowed.
- Changes must be submitted through a Pull Request.
- Required CI checks must pass.
- Code review is required.

### `dev`

`dev` contains the latest integrated development version. It is used to prepare upcoming stable releases. Changes should be introduced through Pull Requests.

### `feature/*`

Feature branches are used for new functionality.

Naming format:

```text
feature/<description>
```

Examples:

```text
feature/user-registration
feature/event-management
feature/seat-availability
feature/booking-service
```

### `fix/*`

Fix branches are used for bug fixes.

Naming format:

```text
fix/<description>
```

Examples:

```text
fix/duplicate-seat-allocation
fix/booking-validation
fix/catalog-api-error
```

## Commit Conventions

TicketHive follows a simple commit message convention.

| Prefix | Usage | Example |
| --- | --- | --- |
| `feat:` | Adding a new feature | `feat: add waiting room service` |
| `fix:` | Fixing a bug | `fix: prevent duplicate seat allocation` |
| `test:` | Adding or modifying tests | `test: add booking service tests` |
| `docs:` | Documentation changes | `docs: update API documentation` |
| `ci:` | CI/CD and automation changes | `ci: add backend deployment pipeline` |

Additional conventional prefixes such as `refactor:` and `chore:` may be used when appropriate. Keep commit messages short, clear, and descriptive.

## Prerequisites

Install the following before starting backend development:

- Git
- .NET SDK
- Docker
- PostgreSQL
- PostgreSQL client/tool (optional)
- Azure CLI for Azure-related development (optional)

Check the installed versions:

```bash
git --version
dotnet --version
docker --version
psql --version
```

## Local Setup

### 1. Clone the Repository

```bash
git clone <TICKET_HIVE_BACKEND_REPOSITORY_URL>
cd TicketHive-Backend
```

### 2. Restore Dependencies

```bash
dotnet restore
```

### 3. Build the Solution

```bash
dotnet build
```

The build should complete without errors.

### 4. Run Tests

```bash
dotnet test
```

All available tests should pass before creating a Pull Request.

Focused mutation tests and their quality gate are documented in
[`docs/testing/mutation-testing.md`](docs/testing/mutation-testing.md).
Whole-backend unit and mocking evidence is documented in
[`docs/testing/unit-testing-and-mocking.md`](docs/testing/unit-testing-and-mocking.md).

### 5. Run a Service Locally

Run a service from the repository root. For example:

```bash
dotnet run --project services/Identity
```

The terminal will display the URL where the service is running. The same process can be used for other services.

## Health Checks

Backend services should provide a health endpoint:

```http
GET /health
```

Test a running service with:

```bash
curl http://localhost:<PORT>/health
```

A healthy service should return a successful HTTP response. These endpoints will later be used by Azure deployment and smoke-testing pipelines.

## PostgreSQL

TicketHive uses PostgreSQL for persistent data storage. Each microservice should own and manage its required data independently.

Local development may require PostgreSQL to be running before starting services that depend on a database.

Database connection configuration should be provided through environment variables or local configuration files that do not contain committed secrets.

Do not commit production database credentials to Git.

Configuration values may include:

```text
Database server
Database name
Database username
Database password
```

Sensitive values should be provided through environment variables, local development configuration, or the project's approved secret-management solution.

## Apache Kafka

Apache Kafka is used for asynchronous communication between TicketHive microservices.

Example event flow:

```text
Service A
   |
   | Kafka event
   v
Apache Kafka
   |
   v
Service B
```

Examples of system events include:

```text
InventoryHoldRequested
InventoryHeld
InventoryHoldFailed
PaymentRequested
PaymentSucceeded
PaymentFailed
BookingConfirmed
TicketIssued
```

Kafka configuration should be provided through environment-specific configuration. Do not commit Kafka credentials or other sensitive configuration to Git.

## Docker

TicketHive backend services are containerized using Docker.

### Build a Service Image

Run these commands from the backend repository root:

```bash
docker build -f services/Identity/Dockerfile -t tickethive-identity .
docker build -f services/Catalog/Dockerfile -t tickethive-catalog .
```

### Run a Service Container

```bash
docker run -p 8080:8080 tickethive-identity
```

The exact port and environment configuration may differ depending on the service.

### View Running Containers

```bash
docker ps
```

### Stop a Container

```bash
docker stop <container_id>
```

Docker configuration should be kept consistent across development and deployment environments.

## GitHub Actions CI

The backend uses GitHub Actions for Continuous Integration. The CI pipeline automatically validates code submitted through Pull Requests.

The pipeline may perform the following checks:

```text
Pull Request
     ↓
Checkout code
     ↓
Restore dependencies
     ↓
Dependency checks
     ↓
Code quality checks
     ↓
Build
     ↓
Unit tests
     ↓
Docker build
```

A Pull Request should not be merged until the required CI checks pass.

## CI Checks

The backend CI pipeline should verify:

### Dependencies

```bash
dotnet restore
```

### Build

```bash
dotnet build
```

### Tests

```bash
dotnet test
```

### Code Quality

Configured formatting and analyzer checks should be executed where applicable.

### Docker

Docker images should be built successfully to ensure the service can be containerized.

## SE3112 testing pipeline

The four assignment parts cover the backend as a system, with mutation and load testing focused where they provide the most useful evidence. All changes are proposed to `dev`; none of these test workflows deploys to Azure production.

| Owner | Part | Implementation and evidence |
| --- | --- | --- |
| Mahima | Unit testing and mocking | [Whole-backend unit-test guide](docs/testing/unit-testing-and-mocking.md), `tests/`, and the CI unit stage |
| Chethiya | Mutation testing | [Mutation guide](docs/testing/mutation-testing.md) and the mutation-tests workflow |
| Manula | Load and performance testing | [Local k6 guide](load-tests/README.md) and the load-tests workflow |
| Ashen | Code quality and metrics | SonarCloud, coverage reports, and the CI quality gate documented below |

The CI workflow runs on pull requests to `dev` or `main`, on merges to `dev`, and on manual dispatch. It calls the reusable mutation and load workflows, then combines all three jobs' artifacts into a **unified-test-report**. Open that artifact or the CI run's **Summary** for unit/integration totals, coverage, mutation scores, and all six k6 verdicts and scenario details. The individual artifacts remain available for drill-down. Mutation and load also support manual runs for individual demonstrations; they stay separate because they use different tools, runners, and time budgets. Load tests target an ephemeral local stack in CI, not production services or Azure resources. A full CI run consumes more GitHub Actions minutes than a unit-only run.

## SE3112 Code Quality and Coverage

This part of the testing pipeline measures automated test coverage, analyzes the code with SonarCloud, and checks the analysis against a quality gate. Static analysis examines source code without executing it, complementing tests that execute code to check behaviour. It helps the team find untested code and code-quality or security problems before merging changes. One SonarCloud project analyzes all backend services in this solution.

### Tools

- **SonarCloud** hosts the analysis results, issues, metrics, and quality-gate status.
- **SonarScanner for .NET** wraps the .NET build and analysis steps and submits their data to SonarCloud.
- **Coverlet** collects test coverage through the VSTest `XPlat Code Coverage` collector. The workflow asks it for OpenCover files, which SonarCloud's C# coverage importer reads, and Cobertura files, which ReportGenerator uses for the HTML and Markdown reports.
- **ReportGenerator** combines the Cobertura input files and creates a browsable HTML report and a Markdown summary.
- **GitHub Actions** restores, formats, builds, tests, runs the analysis, reports coverage, and uploads the results for each applicable workflow run.

### Configuration

The CI workflow expects a GitHub Actions repository secret named `SONAR_TOKEN`. A repository maintainer configures it in **Settings → Secrets and variables → Actions → New repository secret**. The secret value must not be committed to the repository or printed in logs. The workflow also supplies the existing SonarCloud project key `ch3thiya_TicketHive-Backend` and organization key `ch3thiya`.

### GitHub Actions execution

These are the commands used by `.github/workflows/ci.yml` on the GitHub Actions runner. They describe the CI pipeline; they are not a claim that the same commands have been run successfully on every developer's computer.

```bash
dotnet restore TicketHive.slnx
dotnet format TicketHive.slnx --verify-no-changes --no-restore

dotnet tool install --global dotnet-sonarscanner --version 12.0.0
dotnet tool install --global dotnet-reportgenerator-globaltool

dotnet sonarscanner begin \
  /k:"ch3thiya_TicketHive-Backend" \
  /o:"ch3thiya" \
  /d:sonar.host.url="https://sonarcloud.io" \
  /d:sonar.token="$SONAR_TOKEN" \
  /d:sonar.cs.opencover.reportsPaths="TestResults-CI/**/coverage.opencover.xml" \
  /d:sonar.qualitygate.wait=true \
  /d:sonar.exclusions="coverage-report/**,TestResults*/**"

dotnet build TicketHive.slnx --configuration Release --no-restore

for project in tests/*/*.csproj; do
  suite="$(basename "$(dirname "$project")")"
  dotnet test "$project" --configuration Release --no-build \
    --filter "FullyQualifiedName!~Integration" \
    --settings tests/unit-coverage.runsettings \
    --collect:"XPlat Code Coverage" \
    --logger "trx;LogFileName=unit.trx" \
    --results-directory "./TestResults-CI/unit/$suite"
done

for project in tests/*/*.csproj; do
  suite="$(basename "$(dirname "$project")")"
  dotnet test "$project" --configuration Release --no-build \
    --filter "FullyQualifiedName~Integration" \
    --collect:"XPlat Code Coverage;Format=opencover,cobertura" \
    --logger "trx;LogFileName=integration.trx" \
    --results-directory "./TestResults-CI/integration/$suite"
done

dotnet sonarscanner end /d:sonar.token="$SONAR_TOKEN"
```

The unit and integration stages use separate directories for every test project so their result files do not overwrite each other. Unit coverage also uses `tests/unit-coverage.runsettings` to exclude infrastructure files from its business-logic measure; integration coverage is collected separately. SonarScanner imports the OpenCover files at `TestResults-CI/**/coverage.opencover.xml`. The `sonar.qualitygate.wait=true` setting makes the analysis wait for SonarCloud's gate result; a failed gate makes the Sonar end step and build job fail.

### Coverage reports

Each test stage requests both OpenCover and Cobertura output from Coverlet. The workflow looks for Cobertura reports from both test stages and passes all matches to ReportGenerator:

```bash
reportgenerator \
  "-reports:TestResults-CI/**/coverage.cobertura.xml" \
  "-targetdir:coverage-report" \
  "-reporttypes:Html;MarkdownSummaryGithub"
```

ReportGenerator merges multiple input coverage files into one report. The HTML report is generated under `coverage-report/` (open `coverage-report/index.html`). The workflow appends `coverage-report/SummaryGithub.md` to the GitHub Actions run summary. The downloadable artifact is named `test-results` and includes `TestResults-CI/` and `coverage-report/`; download it from the completed workflow run's **Artifacts** section.

**Line coverage** is the percentage of coverable source lines executed by tests. **Branch coverage** is the percentage of decision outcomes (such as true and false paths) exercised by tests. Higher coverage can reveal untested paths, but it does not prove the tests are complete or correct.

### Quality gate

SonarCloud's gate conditions apply to new code, while coverage metrics on the project overview also describe overall code. The feature-branch dashboard shows the latest analysis; as a dated example, the `feature/se3112-code-quality` analysis from 21 October 2026 was **OK** and reported **55.5% overall coverage**, **58.1% line coverage**, and **46.5% branch coverage**. That analysis reports zero new lines to cover, so its status response has no new-coverage value. The green gate therefore does not mean overall coverage is at least 80%.

View the current metrics on the [TicketHive-Backend feature branch dashboard](https://sonarcloud.io/project/overview?id=ch3thiya_TicketHive-Backend&branch=feature%2Fse3112-code-quality).

The SonarCloud gate conditions reported for the project are:

- Reliability rating must be no worse than `1`.
- Security rating must be no worse than `1`.
- Maintainability rating must be no worse than `1`.
- Coverage must be at least `80%`.
- Duplicated lines density must not exceed `3%`.
- Reviewed security hotspots must be `100%`.

The CI workflow waits for the result and fails the job when the gate fails. The 80% coverage threshold is for new code, not an overall-code coverage threshold. This is a failing CI check; whether GitHub blocks a merge also depends on repository branch-protection settings, which are managed separately from SonarCloud and are not documented here as verified.

A green gate applies to the gate's new-code conditions. It does not mean the entire existing codebase has perfect quality or coverage. The SonarCloud overview also reports overall metrics and existing issues, which should be reviewed separately.

### Ashen's responsibility: Code Quality & Metrics

My assigned responsibility is to maintain the SonarCloud analysis and quality-gate integration, use Coverlet to collect test coverage, and use ReportGenerator to make coverage results available as an HTML report and GitHub Actions summary. I can demonstrate the CI workflow, the SonarCloud dashboard, the coverage report, and how a failed quality gate is reported.

## Pull Request Workflow

All backend changes should follow the Pull Request workflow:

```text
Create branch
      ↓
Make changes
      ↓
Run tests locally
      ↓
Commit changes
      ↓
Push branch
      ↓
Create Pull Request
      ↓
CI checks
      ↓
Code review
      ↓
Merge
```

Direct pushes to protected branches are not allowed.

## Developer Setup Guide

Every developer should follow these steps when starting new work.

### 1. Clone the Repository

```bash
git clone <TICKET_HIVE_BACKEND_REPOSITORY_URL>
cd TicketHive-Backend
```

### 2. Create a Feature Branch

```bash
git checkout -b feature/<description>
```

Example:

```bash
git checkout -b feature/seat-availability
```

For a bug fix:

```bash
git checkout -b fix/duplicate-seat-allocation
```

### 3. Make Changes

Implement the assigned functionality. Follow the existing microservice structure and coding conventions. Make sure changes remain within the responsibility of the relevant service.

### 4. Run Tests Locally

```bash
dotnet restore
dotnet build
dotnet test
```

If the service uses a database or Kafka locally, make sure the required dependencies are running.

### 5. Commit Using the Convention

For a feature:

```bash
git add .
git commit -m "feat: add seat availability endpoint"
```

For a bug fix:

```bash
git add .
git commit -m "fix: prevent duplicate seat allocation"
```

### 6. Push the Branch

```bash
git push origin feature/seat-availability
```

### 7. Create a Pull Request

Create a Pull Request from the feature branch to `dev`. The Pull Request should contain:

- A clear title
- A description of the changes
- Testing performed
- Relevant issues or tasks
- Any known limitations

### 8. Wait for CI

GitHub Actions will automatically run the configured checks:

```text
Pull Request
     ↓
Dependency checks
     ↓
Build
     ↓
Tests
     ↓
Quality checks
     ↓
Docker build
```

If CI fails, fix the issue and push another commit.

### 9. Get Review

Wait for the required code review and address review comments before merging.

### 10. Merge

Once CI checks pass, the required review is approved, and review comments are addressed, the Pull Request can be merged according to the team's branching strategy.

## Security Guidelines

- Never commit passwords.
- Never commit database credentials.
- Never commit Kafka credentials.
- Never commit JWT secrets.
- Never commit API keys.
- Never commit Azure credentials.
- Never hardcode production secrets in source code.
- Use environment variables or the approved secret-management solution.
- Keep sensitive configuration outside the repository.

## Azure

Azure is used as the cloud deployment environment for TicketHive. The deployment architecture will include containerized services and supporting infrastructure.

The general deployment flow is:

```text
GitHub
   ↓
GitHub Actions
   ↓
Build
   ↓
Test
   ↓
Docker image
   ↓
Azure
   ↓
Running service
   ↓
Health check
```

Deployment configuration will be expanded as each service becomes ready.

## Monitoring and Health Checks

Deployed services should expose health endpoints such as:

```http
GET /health
```

Health checks will be used by the deployment and monitoring process to verify that services are running. Azure monitoring and application logs will help identify service failures and operational problems.

## Environment Configuration

Environment-specific configuration should not be hardcoded into the application.

Examples include:

```text
Database connection strings
Kafka configuration
JWT configuration
Azure configuration
External service credentials
```

Use environment variables, Azure configuration, or approved secret-management mechanisms. Never commit production secrets to Git.

## Development Guidelines

- Follow the microservice boundaries.
- Do not directly push to protected branches.
- Create a feature or fix branch for each task.
- Keep commits small and meaningful.
- Follow the commit convention.
- Write and maintain unit tests.
- Run `dotnet build` and `dotnet test` before creating a Pull Request.
- Make sure Docker builds successfully when changing service or container configuration.
- Do not commit secrets.
- Keep Pull Requests focused on one task where possible.
- Update documentation when changing project setup or development processes.

## Contribution Workflow

The standard development process is:

```text
1. Clone repository
2. Create feature/fix branch
3. Make changes
4. Run tests locally
5. Commit using convention
6. Push branch
7. Create Pull Request
8. Wait for CI
9. Get code review
10. Merge
```

All team members should follow this process to maintain a consistent and reliable development workflow.

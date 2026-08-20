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

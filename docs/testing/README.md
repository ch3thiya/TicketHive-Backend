# Test automation pipeline

This guide covers the SE3112 testing work: local setup, commands, CI execution, evidence, and team ownership. Run commands from the repository root unless a command changes directory. The testing workflows do not deploy to Azure.

## Team ownership

| Member | Area | Main evidence |
| --- | --- | --- |
| Mahima | Unit testing and mocking across the backend | [`tests/`](../../tests/), [unit-testing guide](unit-testing-and-mocking.md), CI unit/architecture results |
| Chethiya | Focused mutation testing | [mutation guide](mutation-testing.md), nine Stryker.NET suites and reports |
| Manula | Load and performance testing | [k6 guide](../../load-tests/README.md), six scenario reports |
| Ashen | Code quality and metrics | Coverlet/ReportGenerator coverage, SonarCloud analysis and quality gate |

## Tool setup

| Tool | Purpose | Local setup |
| --- | --- | --- |
| .NET 10 SDK | Build and run xUnit tests | Install the .NET 10 SDK; check with `dotnet --version` |
| Docker | PostgreSQL Testcontainers and the local load-test stack | Install Docker and ensure `docker info` succeeds |
| Stryker.NET | Mutation testing | Run `dotnet tool restore` for the pinned local tool |
| Node.js 18+ and k6 | Local token issuer and load scenarios | Install both; check with `node --version` and `k6 version` |
| Python 3 | Unified report generator and its tests | Check with `python3 --version` |
| SonarCloud / SonarScanner / ReportGenerator | CI quality gate and coverage reporting | CI installs the scanner and ReportGenerator; the repository needs a GitHub Actions `SONAR_TOKEN` secret |

The .NET test projects include the coverage collector, so a separate local Coverlet installation is not needed. CI installs Python `coverage` to measure the unified-report generator. Never commit or print the SonarCloud token.

## Local commands

Restore and build before running tests:

```bash
dotnet restore TicketHive.slnx
dotnet build TicketHive.slnx --configuration Release --no-restore
```

Run the same name-based test groups used by CI. Integration tests require a working Docker daemon because they use Testcontainers:

```bash
dotnet test TicketHive.slnx --configuration Release --no-build \
  --filter "FullyQualifiedName!~Integration" \
  --settings tests/unit-coverage.runsettings \
  --collect:"XPlat Code Coverage"

dotnet test TicketHive.slnx --configuration Release --no-build \
  --filter "FullyQualifiedName~Integration" \
  --settings tests/integration-coverage.runsettings \
  --collect:"XPlat Code Coverage;Format=opencover,cobertura"
```

These filters sort by fully qualified test name, not by test behaviour. In particular, the Gateway's in-memory HTTP tests run in the unit/architecture group because their names do not contain `Integration`.

Run one focused mutation suite, then repeat with another of the nine test-project directories as needed:

```bash
dotnet tool restore
cd tests/Booking.Service.Tests
dotnet stryker
```

Return to the repository root before starting the isolated local load stack:

```bash
load-tests/scripts/start-stack.sh
load-tests/scripts/run.sh oversell smoke
load-tests/scripts/stop-stack.sh --all
```

Use `load-tests/scripts/run.sh <scenario> <profile>` for `holds`, `oversell`, `availability`, `queue`, `browse`, or `rate-limit`. The CI default profile is `smoke`; the larger `load` and `spike` profiles can be run manually. The scripts reject non-loopback targets, so they cannot load-test Azure production. Stop the stack after local runs to avoid leaving containers and services running.

## GitHub Actions pipeline

The [`CI` workflow](../../.github/workflows/ci.yml) starts on a push to `dev`, a pull request to `dev` or `main`, or manual dispatch from **Actions → CI → Run workflow**. On pull requests it also checks branch/commit conventions. The other jobs are:

1. **Build:** restore, format check, build, unit/architecture and integration tests, Python report-generator tests, coverage, and the SonarCloud quality gate.
2. **Mutation:** the [reusable mutation workflow](../../.github/workflows/mutation-tests.yml) runs nine Stryker.NET suites as a matrix. A score below 80% fails its suite.
3. **Load:** the [reusable load workflow](../../.github/workflows/load-tests.yml) starts a stack on the runner and runs all six k6 smoke scenarios through the Gateway. No deployed environment is targeted.
4. **Report:** after build, mutation, and load finish, it downloads their artifacts and runs `python3 scripts/unified_test_report.py > unified-test-report.md` to combine the results from the same run.

The `secrets` job scans for exposed credentials. A separate `changes` job selects changed services for the `images` build matrix; image builds do not push or deploy images. The report job runs even if an upstream test job fails, so missing or failed evidence remains visible. Mutation and load workflows also allow standalone manual dispatch, but the full `CI` run is the one that produces the combined report.

## Where to find results

Open the completed **Actions → CI** run and its **Summary** page. The `report` job's summary and downloadable `unified-test-report` artifact contain unit/integration totals, coverage, nine mutation scores, six load verdicts, and scenario details. Use the `test-results`, `mutation-*`, and `load-test-results-smoke` artifacts for detailed evidence. SonarCloud's [`dev` dashboard](https://sonarcloud.io/dashboard?id=ch3thiya_TicketHive-Backend&branch=dev) displays the quality-gate result and new-code coverage. The ReportGenerator HTML coverage report is inside `test-results/coverage-report/`.

For a presentation screenshot of the full pipeline, capture the **CI run's job graph or jobs matrix** with the build, mutation, load, and report results visible. Expand the mutation matrix if the nine suites need to be shown; keep the run number and commit visible so the screenshot matches the unified report.

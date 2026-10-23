#!/usr/bin/env python3
"""Summarize artifacts produced by the CI, mutation, and load jobs in one run.

Uses only Python's standard library so it runs on a GitHub-hosted runner without
installing another reporting tool. Missing artifacts are reported, not hidden.
"""

import json
import os
from collections import Counter
from pathlib import Path
from xml.etree import ElementTree


MUTATION_SUITES = (
    "Catalog.Service.Tests",
    "Booking.Service.Tests",
    "Identity.Service.Tests",
    "Notification.Service.Tests",
    "Inventory.Service.Tests",
    "WaitingRoom.Service.Tests",
    "Payment.Service.Tests",
    "Gateway.Tests",
    "BuildingBlocks.Tests",
)
LOAD_TESTS = ("holds", "oversell", "availability", "queue", "browse", "rate-limit")


def read_trx(root: Path, stage: str):
    rows = []
    for path in sorted(root.rglob("*.trx")):
        if stage not in path.parts:
            continue
        try:
            summary = ElementTree.parse(path).find(".//{*}Counters")
            if summary is None:
                raise ValueError("Counters element is missing")
            suite = path.parent.name
            rows.append((suite, int(summary.get("total", "0")),
                         int(summary.get("passed", "0")),
                         int(summary.get("failed", "0")),
                         int(summary.get("notExecuted", "0"))))
        except (ElementTree.ParseError, ValueError) as exc:
            rows.append((f"{path.name} (unreadable: {exc})", 0, 0, 1, 0))
    return rows


def mutation_counts(path: Path):
    data = json.loads(path.read_text(encoding="utf-8"))
    statuses = Counter(
        mutant.get("status", "Unknown")
        for file_data in data.get("files", {}).values()
        for mutant in file_data.get("mutants", [])
    )
    killed = statuses["Killed"]
    timeout = statuses["Timeout"]
    survived = statuses["Survived"]
    uncovered = statuses["NoCoverage"]
    valid = killed + timeout + survived + uncovered
    score = 100 * (killed + timeout) / valid if valid else None
    return killed, timeout, survived, uncovered, score


def table(headers, rows):
    return ["| " + " | ".join(headers) + " |",
            "| " + " | ".join("---" for _ in headers) + " |",
            *("| " + " | ".join(map(str, row)) + " |" for row in rows)]


def build_report(root: Path):
    ci = root / "ci"
    mutation = root / "mutation"
    load = root / "load"
    run_url = (f"{os.getenv('GITHUB_SERVER_URL', 'https://github.com')}/"
               f"{os.getenv('GITHUB_REPOSITORY', 'ch3thiya/TicketHive-Backend')}/"
               f"actions/runs/{os.getenv('GITHUB_RUN_ID', 'local')}")
    results = {name: os.getenv(env, "unknown") for name, env in
               (("CI", "CI_RESULT"), ("Mutation", "MUTATION_RESULT"),
                ("Load", "LOAD_RESULT"))}
    issues = []
    lines = ["# Unified backend testing report", "",
             f"Run: [{os.getenv('GITHUB_RUN_ID', 'local')}]({run_url})  ",
             f"Commit: `{os.getenv('GITHUB_SHA', 'local')}`  ",
             "All sections below come from artifacts of this same Actions run.", "",
             *table(("Stage", "Job result"), ((name, value) for name, value in results.items())), ""]

    for stage in ("unit", "integration"):
        lines += [f"## {stage.title()} and architecture tests" if stage == "unit"
                  else "## Integration tests", ""]
        rows = read_trx(ci, stage)
        if not rows:
            issues.append(f"No {stage} TRX results were found")
            lines += [f"⚠️ No {stage} TRX results were uploaded.", ""]
            continue
        total = [sum(row[i] for row in rows) for i in range(1, 5)]
        lines += table(("Suite", "Total", "Passed", "Failed", "Not run"),
                       [*rows, ("**Total**", *total)]) + [""]
        if total[2]:
            issues.append(f"{total[2]} {stage} test cases failed")

    lines += ["## Code quality and coverage", ""]
    coverage = next(ci.rglob("SummaryGithub.md"), None)
    if coverage:
        lines += [coverage.read_text(encoding="utf-8").strip(), ""]
    else:
        issues.append("Coverage summary was not found")
        lines += ["⚠️ Coverage summary was not uploaded.", ""]
    python_coverage = next(ci.rglob("python-coverage.xml"), None)
    if python_coverage:
        try:
            root_element = ElementTree.parse(python_coverage).getroot()
            line_rate = 100 * float(root_element.attrib["line-rate"])
            branch_rate = 100 * float(root_element.attrib["branch-rate"])
            lines += [f"Python report-generator coverage: {line_rate:.1f}% lines,"
                      f" {branch_rate:.1f}% branches.", ""]
        except (ElementTree.ParseError, KeyError, ValueError) as exc:
            issues.append(f"Python coverage report unreadable: {exc}")
            lines += ["⚠️ Python coverage report could not be read.", ""]
    else:
        issues.append("Python coverage report was not found")
        lines += ["⚠️ Python coverage report was not uploaded.", ""]
    lines += ["The SonarCloud quality gate is enforced by the CI build job."
              " See that job and the SonarCloud dashboard for issue details.", ""]

    lines += ["## Mutation testing", ""]
    mutation_rows = []
    for suite in MUTATION_SUITES:
        artifact = mutation / f"mutation-{suite}"
        path = next(artifact.rglob("mutation-report.json"), None)
        if path is None:
            issues.append(f"Mutation report missing for {suite}")
            mutation_rows.append((suite, "missing", "—", "—", "—", "—"))
            continue
        try:
            killed, timeout, survived, uncovered, score = mutation_counts(path)
            mutation_rows.append((suite, f"{score:.2f}%" if score is not None else "n/a",
                                  killed, timeout, survived, uncovered))
            if score is None or score < 80:
                issues.append(f"Mutation score below gate or empty for {suite}")
        except (json.JSONDecodeError, ValueError, KeyError) as exc:
            issues.append(f"Mutation report unreadable for {suite}: {exc}")
            mutation_rows.append((suite, "unreadable", "—", "—", "—", "—"))
    lines += table(("Suite", "Score", "Killed", "Timeout", "Survived", "No coverage"),
                   mutation_rows) + ["", "The score is (killed + timed out) /"
                                     " (killed + timed out + survived + no coverage)."
                                     " See the `mutation-*` artifacts for mutant-level detail.", ""]

    lines += ["## Load and performance testing", ""]
    verdicts = {}
    for path in load.rglob("*-verdict.json"):
        try:
            data = json.loads(path.read_text(encoding="utf-8"))
            verdicts[data["test"]] = (data, path)
        except (json.JSONDecodeError, KeyError) as exc:
            issues.append(f"Unreadable load verdict {path.name}: {exc}")
    load_rows = []
    details = []
    for name in LOAD_TESTS:
        found = verdicts.get(name)
        if found is None:
            issues.append(f"Load verdict missing for {name}")
            load_rows.append((name, "—", "missing", "—"))
            continue
        data, path = found
        status = "PASS" if data.get("passed") is True else "FAIL"
        if status == "FAIL":
            issues.append(f"Load test failed: {name}")
        load_rows.append((name, data.get("profile", "?"), status,
                          "PASS" if data.get("thresholdsPassed") is True else "FAIL"))
        markdown = path.with_name(path.name.replace("-verdict.json", ".md"))
        if markdown.exists():
            details += [markdown.read_text(encoding="utf-8").strip(), ""]
        else:
            issues.append(f"Load Markdown report missing for {name}")
    lines += table(("Scenario", "Profile", "Verdict", "Thresholds"), load_rows)
    lines += ["", "### Scenario details", "", *details]

    # Report generation should not fail merely because an upstream quality
    # gate failed: that stage is already red, and the report still exists.
    evidence_complete = not issues
    for name, value in results.items():
        if value != "success":
            issues.append(f"{name} job result is {value}")
    lines += ["## Overall verdict", "",
              "**PASS** — all three jobs succeeded and required reports were found."
              if not issues else "**INCOMPLETE / FAIL** — " + "; ".join(issues) + ".", ""]
    return "\n".join(lines), evidence_complete


def main():
    report, passed = build_report(Path("report-input"))
    print(report)
    return 0 if passed else 1


if __name__ == "__main__":
    raise SystemExit(main())

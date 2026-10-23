import json
import tempfile
import unittest
from pathlib import Path
from sys import path
from unittest.mock import patch

path.insert(0, str(Path(__file__).resolve().parents[1]))

from unified_test_report import LOAD_TESTS, MUTATION_SUITES, build_report


class UnifiedTestReportTests(unittest.TestCase):
    def test_combines_all_stages_from_one_artifact_tree(self):
        with tempfile.TemporaryDirectory() as temporary:
            root = Path(temporary)
            for stage in ("unit", "integration"):
                trx = root / "ci" / "TestResults-CI" / stage / "Catalog.Service.Tests" / f"{stage}.trx"
                trx.parent.mkdir(parents=True)
                trx.write_text('<TestRun xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">'
                               '<ResultSummary><Counters total="2" passed="2" failed="0" '
                               'notExecuted="0" /></ResultSummary></TestRun>', encoding="utf-8")
            coverage = root / "ci" / "coverage-report" / "SummaryGithub.md"
            coverage.parent.mkdir(parents=True)
            coverage.write_text("Coverage summary example\n", encoding="utf-8")
            for suite in MUTATION_SUITES:
                report = root / "mutation" / f"mutation-{suite}" / "reports" / "mutation-report.json"
                report.parent.mkdir(parents=True)
                report.write_text(json.dumps({"files": {"example.cs": {"mutants": [
                    {"status": "Killed"}, {"status": "Killed"}, {"status": "Killed"},
                    {"status": "Survived"}, {"status": "Timeout"}]}}}), encoding="utf-8")
            for name in LOAD_TESTS:
                verdict = root / "load" / f"{name}-smoke-20261023-verdict.json"
                verdict.parent.mkdir(parents=True, exist_ok=True)
                verdict.write_text(json.dumps({"test": name, "profile": "smoke",
                                               "passed": True, "thresholdsPassed": True}),
                                   encoding="utf-8")
                verdict.with_name(verdict.name.replace("-verdict.json", ".md")).write_text(
                    f"### Load test: {name} — PASS\n", encoding="utf-8")

            with patch.dict("os.environ", {"CI_RESULT": "success",
                                          "MUTATION_RESULT": "success",
                                          "LOAD_RESULT": "success"}):
                report, passed = build_report(root)
            self.assertTrue(passed)
            self.assertIn("**PASS**", report)
            self.assertIn("| **Total** | 2 | 2 | 0 | 0 |", report)
            self.assertIn("80.00%", report)
            self.assertIn("### Load test: holds", report)

    def test_missing_artifacts_are_reported_as_incomplete(self):
        with tempfile.TemporaryDirectory() as temporary:
            report, passed = build_report(Path(temporary))
        self.assertFalse(passed)
        self.assertIn("INCOMPLETE / FAIL", report)
        self.assertIn("Mutation report missing", report)


if __name__ == "__main__":
    unittest.main()

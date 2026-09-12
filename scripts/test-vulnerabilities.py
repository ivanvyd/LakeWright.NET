#!/usr/bin/env python3
"""Negative fixtures for the same advisory evaluator used by CI."""

import copy
import importlib.util
import json
from pathlib import Path
import subprocess
import sys
import tempfile
import unittest
from unittest.mock import patch

spec = importlib.util.spec_from_file_location("advisories", Path(__file__).with_name("check-vulnerabilities.py"))
advisories = importlib.util.module_from_spec(spec)
spec.loader.exec_module(advisories)

CLEAN = {"version": 1, "parameters": "--vulnerable --include-transitive", "sources": ["https://example.invalid/index.json"], "projects": [{"path": "fixture.csproj"}]}


def report_with(severity):
    report = copy.deepcopy(CLEAN)
    report["projects"][0]["frameworks"] = [{"framework": "net10.0", "transitivePackages": [{
        "id": "Example.Package", "resolvedVersion": "1.0.0", "vulnerabilities": [{"severity": severity, "advisoryurl": "https://example.invalid/advisory"}],
    }]}]
    return report


class AdvisoryTests(unittest.TestCase):
    def test_clean_report(self):
        self.assertEqual([], advisories.evaluate(CLEAN))

    def test_every_severity(self):
        for severity in ("Low", "Moderate", "High", "Critical"):
            with self.subTest(severity=severity):
                self.assertEqual(severity != "Low", bool(advisories.evaluate(report_with(severity))))

    def test_unknown_severity(self):
        with self.assertRaises(advisories.InvalidReport):
            advisories.evaluate(report_with("Future"))

    def test_invalid_coverage_and_schema(self):
        for key, value in (("version", 2), ("version", True), ("sources", []), ("projects", []), ("parameters", "--vulnerable"), ("projects", None)):
            with self.subTest(key=key, value=value), self.assertRaises(advisories.InvalidReport):
                advisories.evaluate({**CLEAN, key: value})

    def test_feed_warning_or_restore_error_is_not_clean(self):
        for level in ("Warning", "Error", "Future"):
            with self.subTest(level=level), self.assertRaises(advisories.InvalidReport):
                advisories.evaluate({**CLEAN, "logs": [{"level": level, "message": "feed unavailable"}]})

    def test_exit_42_is_not_hidden_by_valid_stdout(self):
        with patch.object(advisories.subprocess, "run", return_value=subprocess.CompletedProcess([], 42, json.dumps(CLEAN), "")):
            with self.assertRaisesRegex(advisories.InvalidReport, "exit 42"):
                advisories.scan()

    def test_stderr_is_not_silently_discarded(self):
        with patch.object(advisories.subprocess, "run", return_value=subprocess.CompletedProcess([], 0, json.dumps(CLEAN), "feed unavailable")):
            with self.assertRaises(advisories.InvalidReport):
                advisories.scan()

    def test_cli_verdict_exit_codes(self):
        for raw, expected in (("", 2), ("not json", 2), ("null", 2), (json.dumps(CLEAN), 0), (json.dumps(report_with("Critical")), 1)):
            with self.subTest(raw=raw), tempfile.TemporaryDirectory() as directory:
                report = Path(directory) / "report.json"
                report.write_text(raw, encoding="utf-8")
                result = subprocess.run([sys.executable, str(Path(advisories.__file__)), "--report", str(report)], capture_output=True, check=False)
                self.assertEqual(expected, result.returncode, result.stderr.decode())


if __name__ == "__main__":
    unittest.main()

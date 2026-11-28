#!/usr/bin/env python3
"""Exercise the claim ledger check against small fixture repositories."""

from __future__ import annotations

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("check-claim-ledger.py")

PROPS = "<Project><PropertyGroup><VersionPrefix>3.0.0</VersionPrefix></PropertyGroup></Project>\n"
CHANGELOG = "# Changelog\n\n## [Unreleased]\n\n## [3.0.0] — 2026-11-07\n\n## [2.0.0] — 2026-09-02\n"


def row(number: int, capability: str, status: str, when: str) -> str:
    anchor = f"lw-c{number:03d}"
    return f'| <a id="{anchor}"></a>{anchor.upper()} | {capability} | {status} | {when} | evidence |\n'


def ledger(*rows: str) -> str:
    return (
        "# Compatibility matrix\n\n"
        "## Legend\n\n| Status | Meaning |\n|---|---|\n| **Verified** | Live. |\n\n"
        "## Recorded verification environments\n\n| | |\n|---|---|\n| .NET | 10.0.400 |\n\n"
        "## Databricks\n\n| ID | Capability | Status | Date | Evidence |\n|---|---|---|---|---|\n"
        + "".join(rows)
        + "\n```text\n| not | a table |\n```\n"
    )


CURRENT = (
    row(1, "Statement Execution", "**Verified**", "2026-11-07"),
    row(2, "Rendering hygiene", "**Verified locally**", "2026-09-02"),
    row(3, "Multi-chunk reads", "Unverified", ""),
)
README = "Queries are scoped ([LW-C001](docs/compatibility.md#lw-c001)).\n"


class ClaimLedgerTests(unittest.TestCase):
    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.root = Path(self.temp_dir.name)
        (self.root / "docs").mkdir()
        (self.root / "Directory.Build.props").write_text(PROPS, encoding="utf-8")
        (self.root / "CHANGELOG.md").write_text(CHANGELOG, encoding="utf-8")

    def tearDown(self) -> None:
        self.temp_dir.cleanup()

    def check(self, compatibility: str, readme: str = README) -> subprocess.CompletedProcess[str]:
        (self.root / "docs/compatibility.md").write_text(compatibility, encoding="utf-8")
        (self.root / "README.md").write_text(readme, encoding="utf-8")
        return subprocess.run(
            [sys.executable, str(SCRIPT), "--root", str(self.root)],
            capture_output=True,
            text=True,
            encoding="utf-8",
            check=False,
        )

    def test_valid_ledger_passes_without_warnings(self) -> None:
        result = self.check(ledger(*CURRENT))
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertNotIn("::warning", result.stdout)
        self.assertIn("3 IDs, 1 README citation(s) resolved; 0 live-verified row(s) predate 3.0.0", result.stdout)

    def test_duplicate_id_fails(self) -> None:
        result = self.check(ledger(*CURRENT, row(1, "Jobs API", "**Verified**", "2026-11-07")))
        self.assertEqual(1, result.returncode)
        self.assertIn("duplicate claim ID LW-C001", result.stdout)

    def test_readme_citation_of_unknown_id_fails(self) -> None:
        for readme, expected in (
            ("See [LW-C009](docs/compatibility.md#lw-c009).\n", "cites LW-C009"),
            ("See [LW-C001](docs/compatibility.md#lw-c002).\n", "link labelled LW-C001 points at #lw-c002"),
        ):
            with self.subTest(readme=readme):
                result = self.check(ledger(*CURRENT), readme)
                self.assertEqual(1, result.returncode)
                self.assertIn(expected, result.stdout)

    def test_missing_or_malformed_id_fails(self) -> None:
        for broken, expected in (
            ("|  | Jobs API | **Verified** | 2026-11-07 | evidence |\n", "claim row has no ID"),
            ("| LW-C004 | Jobs API | **Verified** | 2026-11-07 | evidence |\n", "malformed claim ID"),
            ('| <a id="lw-c005"></a>LW-C004 | Jobs API | **Verified** | 2026-11-07 | evidence |\n', "malformed claim ID"),
        ):
            with self.subTest(row=broken):
                result = self.check(ledger(*CURRENT, broken))
                self.assertEqual(1, result.returncode)
                self.assertIn(expected, result.stdout)
        result = self.check(ledger(*CURRENT).replace("| ID | Capability", "| Capability | ID"))
        self.assertEqual(1, result.returncode)
        self.assertIn("has no leading ID column", result.stdout)

    def test_stale_live_verified_row_warns_but_passes(self) -> None:
        result = self.check(ledger(*CURRENT, row(4, "Jobs API", "**Verified**", "2026-09-01")))
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        warnings = [line for line in result.stdout.splitlines() if line.startswith("::warning")]
        self.assertEqual(1, len(warnings), result.stdout)
        self.assertIn("LW-C004 was last live-verified on 2026-09-01, before 3.0.0", warnings[0])

    def test_unreleased_major_treats_every_live_row_as_stale(self) -> None:
        (self.root / "Directory.Build.props").write_text(PROPS.replace("3.0.0", "4.0.0"), encoding="utf-8")
        result = self.check(ledger(*CURRENT))
        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("LW-C001 was last live-verified on 2026-11-07, before 4.0.0", result.stdout)
        self.assertNotIn("LW-C002", result.stdout.split("Claim ledger:")[0])


if __name__ == "__main__":
    unittest.main()

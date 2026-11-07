#!/usr/bin/env python3
"""Exercise CI restore diagnostics and maintenance branch validation as shell data."""

import argparse
import os
from pathlib import Path
import subprocess
import textwrap
import unittest


ROOT = Path(__file__).resolve().parents[1]
parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument("--bash", default="bash")
args, test_args = parser.parse_known_args()


def step(workflow, name):
    source = (ROOT / ".github/workflows" / workflow).read_text(encoding="utf-8")
    marker = f"      - name: {name}\n"
    if marker not in source:
        raise AssertionError(f"Missing workflow step: {name}")
    block = source.split(marker, 1)[1].split("\n      - ", 1)[0]
    return block, textwrap.dedent(block.split("        run: |\n", 1)[1])


def run(script, **values):
    return subprocess.run([args.bash, "-c", script], cwd=ROOT, env=dict(os.environ, **values),
                          capture_output=True, text=True, check=False, timeout=15)


class ShellInputTests(unittest.TestCase):
    def test_restore_success_and_failure_preserve_diagnostic_data(self):
        _, script = step("ci.yml", "Restore")
        stub = 'dotnet() { [ "$*" = "restore --locked-mode" ] || return 99; return "$RESTORE_EXIT"; }\n'
        for branch in ("dependabot/nuget/example", "audit-$(printf${IFS}INJECTION_MARKER)", "audit-`printf${IFS}INJECTION_MARKER`"):
            with self.subTest(branch=branch):
                valid = subprocess.run(["git", "check-ref-format", "--branch", branch],
                                       capture_output=True, text=True, check=False)
                self.assertEqual(0, valid.returncode)
                rendered = script.replace("${{ github.head_ref || github.ref_name }}", branch)
                success = run(stub + rendered, RESTORE_EXIT="0")
                self.assertEqual(0, success.returncode, success.stderr)
                self.assertEqual("", success.stdout)
                failure = run(stub + rendered, RESTORE_EXIT="1")
                self.assertEqual(1, failure.returncode, failure.stderr)
                self.assertIn("::error title=Lock files are out of step::", failure.stdout)
                self.assertIn("Run the 'Refresh lock files' workflow from the default branch, "
                              "with the Dependabot branch as its input, then re-run these checks.", failure.stdout)
                self.assertNotIn("INJECTION_MARKER", failure.stdout)

    def test_maintenance_accepts_only_valid_dependabot_nuget_branches(self):
        block, script = step("refresh-lock-files.yml", "Validate the maintenance branch")
        self.assertIn("BRANCH: ${{ inputs.branch }}", block)
        for branch, accepted in (("dependabot/nuget/example", True), ("main", False),
                                 ("fix/example", False), ("dependabot/nuget/a..b", False),
                                 ("dependabot/nuget/a$(printf${IFS}INJECTION_MARKER)", True)):
            with self.subTest(branch=branch):
                result = run(script, BRANCH=branch)
                self.assertEqual(accepted, result.returncode == 0, result.stderr)
                self.assertNotIn("aINJECTION_MARKER", result.stdout)


if __name__ == "__main__":
    unittest.main(argv=[__file__, *test_args])

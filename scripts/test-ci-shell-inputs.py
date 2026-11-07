#!/usr/bin/env python3
"""Exercise CI restore diagnostics and maintenance branch validation as shell data."""

import argparse
import os
from pathlib import Path
import subprocess
import tempfile
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
                self.assertIn("Refresh lock files", failure.stdout)
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

    def test_maintenance_credentials_follow_verification_and_push_is_bounded(self):
        source = (ROOT / ".github/workflows/refresh-lock-files.yml").read_text(encoding="utf-8")
        self.assertLess(source.index("- name: Validate the maintenance branch"), source.index("- uses: actions/checkout@"))
        self.assertIn("persist-credentials: false", source)
        self.assertIn("ref: refs/heads/${{ inputs.branch }}", source)
        self.assertNotIn("persist-credentials: true", source)
        block, _ = step("refresh-lock-files.yml", "Commit the refreshed lock files")
        self.assertLess(source.index("dotnet build --no-restore -c Release"), source.index("GH_TOKEN:"))
        self.assertIn("GH_TOKEN: ${{ github.token }}", block)
        self.assertIn("BRANCH: ${{ inputs.branch }}", block)
        self.assertIn("git add -- '*packages.lock.json'", block)
        self.assertIn('git push origin "HEAD:refs/heads/$BRANCH"', block)
        self.assertNotIn("${{ inputs.branch }}", block.split("        run: |\n", 1)[1])

    def test_maintenance_push_preserves_branch_data_and_skips_unchanged_locks(self):
        _, script = step("refresh-lock-files.yml", "Commit the refreshed lock files")
        stub = '''
git() {
  case "$1" in
    status) printf '%s' "$LOCK_STATUS" ;;
    config|add|commit) return 0 ;;
    push) printf 'PUSH:%s\\n' "$3" ;;
    *) return 99 ;;
  esac
}
gh() { [ "$*" = 'auth setup-git' ]; }
'''
        branch = "dependabot/nuget/a$(printf${IFS}INJECTION_MARKER)"
        changed = run(stub + script, BRANCH=branch, LOCK_STATUS=" M packages.lock.json")
        self.assertEqual(0, changed.returncode, changed.stderr)
        self.assertIn(f"PUSH:HEAD:refs/heads/{branch}", changed.stdout)
        self.assertNotIn("aINJECTION_MARKER", changed.stdout)
        unchanged = run(stub + script, BRANCH=branch, LOCK_STATUS="")
        self.assertEqual(0, unchanged.returncode, unchanged.stderr)
        self.assertNotIn("PUSH:", unchanged.stdout)

    def test_refresh_commit_excludes_unrelated_staged_files(self):
        _, script = step("refresh-lock-files.yml", "Commit the refreshed lock files")
        script = script[:script.index("gh auth setup-git")]
        with tempfile.TemporaryDirectory() as directory:
            fixture = Path(directory)

            def git(*arguments):
                return subprocess.run(["git", *arguments], cwd=fixture, capture_output=True,
                                      text=True, check=True)

            git("init", "-q")
            git("config", "user.name", "Fixture")
            git("config", "user.email", "fixture@example.invalid")
            git("config", "commit.gpgsign", "false")
            lock = fixture / "packages.lock.json"
            other = fixture / "unrelated.txt"
            lock.write_text("{}\n", encoding="utf-8")
            other.write_text("before\n", encoding="utf-8")
            git("add", "packages.lock.json", "unrelated.txt")
            git("commit", "-qm", "fixture")
            lock.write_text('{"version": 1}\n', encoding="utf-8")
            other.write_text("staged elsewhere\n", encoding="utf-8")
            git("add", "unrelated.txt")
            result = subprocess.run([args.bash, "-c", script], cwd=fixture,
                                    env=dict(os.environ, BRANCH="dependabot/nuget/example"),
                                    capture_output=True, text=True, check=False, timeout=15)
            self.assertEqual(0, result.returncode, result.stderr)
            self.assertEqual("packages.lock.json", git("diff-tree", "--no-commit-id", "--name-only", "-r", "HEAD").stdout.strip())
            self.assertEqual("unrelated.txt", git("diff", "--cached", "--name-only").stdout.strip())


if __name__ == "__main__":
    unittest.main(argv=[__file__, *test_args])

#!/usr/bin/env python3
"""Exercise the maintenance trust boundary with real local Git repositories."""

import base64
import copy
import importlib.util
import json
from pathlib import Path
import re
import subprocess
import tempfile
import unittest
from unittest.mock import patch


ROOT = Path(__file__).resolve().parents[1]
ARTIFACTS = ROOT / "artifacts/scorecard-hardening-20261107"
SPEC = importlib.util.spec_from_file_location("refresh", ROOT / "scripts/refresh-lock-files.py")
refresh = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(refresh)
BRANCH = "dependabot/nuget/fixture"
LOCK = "src/Example/packages.lock.json"


def lock_bytes(version="1.0.0"):
    return json.dumps({"version": 2, "dependencies": {"net10.0": {"Example": {
        "type": "Direct", "requested": "[1.0.0, )", "resolved": version,
        "contentHash": base64.b64encode(bytes(64)).decode()}}}}).encode()


def workflow_jobs(source):
    return dict((match.group(1), match.group(2)) for match in re.finditer(
        r"^  ([a-z-]+):\n(.*?)(?=^  [a-z-]+:\n|\Z)", source.split("\njobs:\n", 1)[1], re.M | re.S))


def isolated_writer_policy(source):
    jobs = workflow_jobs(source)
    return (any("contents: write" in job for job in jobs.values())
            and all("dotnet " not in job for job in jobs.values() if "contents: write" in job))


def cache_disabled_policy(source):
    keys = re.findall(r"(?m)^[ \t]*cache-mode:", source)
    return bool(re.search(r"(?m)^cache-mode: none$", source)) and len(keys) == 1


class WorkflowPolicyTests(unittest.TestCase):
    def test_runtime_cache_access_is_disabled_without_job_overrides(self):
        source = (ROOT / ".github/workflows/refresh-lock-files.yml").read_text(encoding="utf-8")
        self.assertTrue(cache_disabled_policy(source))
        self.assertTrue(cache_disabled_policy(source + "\n# cache-mode is enforced by scoped cache tokens.\n"))
        self.assertFalse(cache_disabled_policy(source.replace("cache-mode: none\n", "", 1)))
        for mode in ("read", "write", "write-only"):
            with self.subTest(workflow_mode=mode):
                self.assertFalse(cache_disabled_policy(source.replace("cache-mode: none", "cache-mode: " + mode, 1)))
        for job in workflow_jobs(source):
            for mode in ("none", "read", "write", "write-only"):
                with self.subTest(job=job, override=mode):
                    overridden = source.replace(f"  {job}:\n", f"  {job}:\n    cache-mode: {mode}\n", 1)
                    self.assertFalse(cache_disabled_policy(overridden))

    def test_candidate_execution_has_no_write_job_and_trust_comes_from_resolver(self):
        source = (ROOT / ".github/workflows/refresh-lock-files.yml").read_text(encoding="utf-8")
        self.assertTrue(isolated_writer_policy(source))
        jobs = workflow_jobs(source)
        self.assertEqual({"resolve", "refresh", "write"}, jobs.keys())
        self.assertIn("github.ref == format('refs/heads/{0}', github.event.repository.default_branch)", jobs["resolve"])
        self.assertIn("needs: [resolve, refresh]", jobs["write"])
        self.assertIn("contents: read", jobs["refresh"])
        self.assertNotIn("outputs:", jobs["refresh"])
        self.assertNotIn("needs.refresh.outputs", source)
        self.assertEqual(3, source.count("ref: ${{ github.workflow_sha }}"))
        self.assertIn("ref: ${{ needs.resolve.outputs.source_sha }}", jobs["refresh"])
        self.assertEqual(4, source.count("persist-credentials: false"))
        self.assertNotIn("persist-credentials: true", source)
        self.assertNotIn("actions: write", source)
        for job in (jobs["resolve"], jobs["write"]):
            self.assertNotIn("global-json-file:", job)
            self.assertNotIn("path: candidate", job)
        writer = jobs["write"]
        self.assertIn("SOURCE_SHA: ${{ needs.resolve.outputs.source_sha }}", writer)
        self.assertIn("GH_TOKEN: ${{ github.token }}", writer)
        self.assertNotIn("${{ inputs.branch }}", writer.split("        run: |\n", 1)[1])
        self.assertIn("If lock files were pushed:\n", writer)
        self.assertIn("Approve pending PR workflows with 'Approve workflows to run'.", writer)
        self.assertIn("If expected checks are absent, close and reopen the PR with your own identity.", writer)
        self.assertIn("Rerunning older workflow runs does not test the new SHA.", writer)
        self.assertNotIn("creates no", writer)
        self.assertIn("actions/upload-artifact@cf430e030ddbb5b0abf93d22962f4752f3646cd9", source)
        self.assertIn("actions/download-artifact@9000827ccba6bdab643e8b6fd33ac0654aef8333", source)

    def test_committed_lock_files_fit_the_supported_schema(self):
        paths = refresh.git(ROOT, "ls-files", "-z", "*packages.lock.json").split(b"\0")
        for path in paths:
            if path:
                with self.subTest(path=path):
                    refresh.validate_lock((ROOT / path.decode()).read_bytes())


class ProtocolTests(unittest.TestCase):
    def setUp(self):
        ARTIFACTS.mkdir(parents=True, exist_ok=True)
        self.temporary = tempfile.TemporaryDirectory(prefix="lock-protocol-", dir=ARTIFACTS)
        self.addCleanup(self.temporary.cleanup)
        self.root = Path(self.temporary.name)
        self.source = self.root / "candidate"
        self.source.mkdir()
        template = self.root / "empty-template"
        template.mkdir()
        refresh.git(self.source, "init", "-q", "--template=" + str(template))
        refresh.git(self.source, "config", "user.name", "Fixture")
        refresh.git(self.source, "config", "user.email", "fixture@example.invalid")
        self.lock = self.source / LOCK
        self.lock.parent.mkdir(parents=True)
        self.lock.write_bytes(lock_bytes())
        (self.source / "other.txt").write_text("original\n", encoding="utf-8")
        # These are target-tree data. Loading any of them in the writer is a defect.
        self.marker = self.root / "candidate-code-executed"
        poison = f'#!/bin/sh\necho poisoned > "{self.marker.as_posix()}"\nexit 97\n'
        (self.source / ".githooks").mkdir()
        for name in ("pre-commit", "pre-push", "post-checkout"):
            (self.source / ".githooks" / name).write_text(poison, encoding="utf-8")
        (self.source / ".gitconfig").write_text("[include]\n path = .githooks/pre-push\n", encoding="utf-8")
        (self.source / ".gitattributes").write_text("* filter=poison\n", encoding="utf-8")
        refresh.git(self.source, "add", ".")
        refresh.git(self.source, "-c", "commit.gpgsign=false", "commit", "-qm", "fixture")
        refresh.git(self.source, "branch", BRANCH)
        self.base = refresh.git(self.source, "rev-parse", "HEAD").decode().strip()
        self.remote = self.root / "remote.git"
        refresh.git(self.root, "clone", "-q", "--bare", "--template=" + str(template), str(self.source), str(self.remote))
        (self.root / "payload").mkdir()
        self.payload = self.root / "payload/lock-refresh.json"
        self.writer = self.root / "writer.git"
        self.attempt = 0
        self.lock.write_bytes(lock_bytes("1.0.1"))
        refresh.pack(self.source, BRANCH, self.base, self.payload)

    def head(self):
        return refresh.git(self.remote, "rev-parse", "refs/heads/" + BRANCH).decode().strip()

    def write(self):
        self.attempt += 1
        self.writer = self.root / f"writer-{self.attempt}.git"
        refresh.write(str(self.remote), BRANCH, self.base, self.payload, self.writer)

    def payload_data(self):
        return json.loads(self.payload.read_bytes())

    def save(self, data):
        self.payload.write_text(json.dumps(data), encoding="utf-8")

    def assert_rejected(self):
        with self.assertRaises((ValueError, OSError, RecursionError, subprocess.SubprocessError)):
            self.write()
        self.assertEqual(self.base, self.head())
        self.assertFalse(self.marker.exists())

    def test_resolve_and_success_change_only_lock_data_without_target_checkout(self):
        self.assertEqual(self.base, refresh.resolve(str(self.remote), BRANCH))
        hooks = self.source / ".git/hooks"
        hooks.mkdir()
        for name in ("pre-commit", "pre-push", "post-checkout"):
            target = hooks / name
            target.write_text(f'#!/bin/sh\necho poison > "{self.marker.as_posix()}"\nexit 97\n', encoding="utf-8")
            target.chmod(0o755)
        self.write()
        head = self.head()
        self.assertNotEqual(self.base, head)
        self.assertEqual(self.base, refresh.git(self.remote, "rev-parse", head + "^").decode().strip())
        self.assertEqual(LOCK, refresh.git(self.remote, "diff-tree", "--no-commit-id", "--name-only", "-r", head).decode().strip())
        self.assertEqual(lock_bytes("1.0.1"), refresh.git(self.remote, "show", head + ":" + LOCK))
        self.assertEqual(b"true\n", refresh.git(self.writer, "rev-parse", "--is-bare-repository"))
        self.assertFalse(self.marker.exists())
        self.assertFalse((self.writer / "hooks").exists())

    def test_unchanged_does_not_commit_or_push(self):
        self.lock.write_bytes(lock_bytes())
        refresh.pack(self.source, BRANCH, self.base, self.payload)
        with patch.object(refresh, "git", wraps=refresh.git) as calls:
            self.write()
        self.assertEqual(self.base, self.head())
        self.assertFalse(any(call.args[1] in ("commit-tree", "push") for call in calls.call_args_list))

    def test_same_named_tag_cannot_select_source_or_receive_push(self):
        tree = refresh.git(self.source, "rev-parse", "HEAD^{tree}").decode().strip()
        tag_commit = refresh.git(self.source, "-c", "commit.gpgsign=false", "commit-tree", tree,
                                 "-p", self.base, data=b"different tag target\n").decode().strip()
        tag_ref = "refs/tags/" + BRANCH
        refresh.git(self.source, "update-ref", tag_ref, tag_commit)
        refresh.git(self.remote, "fetch", str(self.source), tag_ref + ":" + tag_ref)
        self.assertNotEqual(self.base, tag_commit)
        self.assertEqual(self.base, refresh.resolve(str(self.remote), BRANCH))
        self.write()
        self.assertEqual(tag_commit, refresh.git(self.remote, "rev-parse", tag_ref).decode().strip())
        self.assertNotEqual(self.base, self.head())

    def test_invalid_envelopes_are_rejected_without_mutation(self):
        valid = self.payload_data()
        cases = [[], {}, dict(valid, version=True), dict(valid, version=2),
                 dict(valid, branch="main"), dict(valid, base="0" * 40),
                 dict(valid, extra="unexpected"), dict(valid, files={}), dict(valid, files=[]),
                 dict(valid, files={LOCK: "not base64!"}), dict(valid, files={LOCK: 12})]
        for data in cases:
            with self.subTest(data=data):
                self.save(data)
                self.assert_rejected()

    def test_malicious_paths_and_untracked_lock_are_rejected(self):
        valid = self.payload_data()
        for path in ("../packages.lock.json", "/packages.lock.json", "x/../../packages.lock.json",
                     "src\\packages.lock.json", "src/.git/packages.lock.json", "other.txt",
                     "new/packages.lock.json", "src//packages.lock.json"):
            with self.subTest(path=path):
                data = copy.deepcopy(valid)
                data["files"] = {path: valid["files"][LOCK]}
                self.save(data)
                self.assert_rejected()

    def test_unexpected_artifact_file_is_rejected(self):
        (self.payload.parent / "script.sh").write_text("exit 97\n", encoding="utf-8")
        self.assert_rejected()

    def test_malformed_oversized_duplicate_and_invalid_lock_json(self):
        for data in (b"{", b"[" * 2000, b" " * (refresh.MAX_PAYLOAD + 1),
                     b'{"version":1,"version":1}', b'{"version":NaN}'):
            with self.subTest(size=len(data)):
                self.payload.write_bytes(data)
                self.assert_rejected()
        for data in (b"{}", b"[]", b"{", b'{"version":true,"dependencies":{}}',
                     b'{"version":2,"dependencies":[]}', b'{"version":2,"dependencies":{"net10.0":[]}}',
                     b'{"version":2,"dependencies":{"net10.0":{"X":{"type":"Direct"}}}}',
                     b'{"version":2,"dependencies":{"net10.0":{"X":{"type":"Project","resolved":"1"}}}}',
                     b" " * (refresh.MAX_LOCK + 1)):
            with self.subTest(lock=data[:90]):
                self.save({"version": 1, "base": self.base, "branch": BRANCH,
                           "files": {LOCK: base64.b64encode(data).decode()}})
                self.assert_rejected()

    def test_pack_rejects_unrelated_staged_untracked_or_missing_files(self):
        other = self.source / "other.txt"
        other.write_text("changed\n", encoding="utf-8")
        refresh.git(self.source, "add", "other.txt")
        with self.assertRaises(ValueError):
            refresh.pack(self.source, BRANCH, self.base, self.payload)
        other.write_text("original\n", encoding="utf-8")
        refresh.git(self.source, "add", "other.txt")
        extra = self.source / "new/packages.lock.json"
        extra.parent.mkdir()
        extra.write_bytes(lock_bytes())
        with self.assertRaises(ValueError):
            refresh.pack(self.source, BRANCH, self.base, self.payload)
        extra.unlink()
        self.lock.unlink()
        with self.assertRaises(ValueError):
            refresh.pack(self.source, BRANCH, self.base, self.payload)

    def test_symlink_in_source_tree_is_rejected_by_writer(self):
        # Create the Git symlink object directly; Windows need not grant symlink privileges.
        blob = refresh.git(self.source, "hash-object", "-w", "--stdin", data=b"../../other.txt").decode().strip()
        refresh.git(self.source, "update-index", "--cacheinfo", "120000", blob, LOCK)
        refresh.git(self.source, "-c", "commit.gpgsign=false", "commit", "-qm", "symlink fixture")
        self.base = refresh.git(self.source, "rev-parse", "HEAD").decode().strip()
        refresh.git(self.remote, "fetch", str(self.source), "HEAD")
        refresh.git(self.remote, "update-ref", "refs/heads/" + BRANCH, self.base)
        data = self.payload_data()
        data["base"] = self.base
        self.save(data)
        self.assert_rejected()

    def advance(self):
        (self.source / "other.txt").write_text("concurrent change\n", encoding="utf-8")
        refresh.git(self.source, "add", "other.txt")
        refresh.git(self.source, "-c", "commit.gpgsign=false", "commit", "-qm", "concurrent fixture")
        refresh.git(self.remote, "fetch", str(self.source), "HEAD")
        advanced = refresh.git(self.source, "rev-parse", "HEAD").decode().strip()
        refresh.git(self.remote, "update-ref", "refs/heads/" + BRANCH, advanced)
        return advanced

    def test_advanced_branch_before_writer_fetch_is_rejected(self):
        advanced = self.advance()
        with self.assertRaises(ValueError):
            self.write()
        self.assertEqual(advanced, self.head())

    def test_normal_push_rejects_race_after_writer_fetch(self):
        real_git = refresh.git
        advanced = []

        def racing_git(directory, *arguments, **kwargs):
            if arguments[0] == "push":
                advanced.append(self.advance())
            return real_git(directory, *arguments, **kwargs)

        with patch.object(refresh, "git", side_effect=racing_git):
            with self.assertRaises(subprocess.CalledProcessError) as failure:
                self.write()
        self.assertIn(b"rejected", failure.exception.stderr)
        self.assertEqual(advanced[0], self.head())
        self.assertFalse(self.marker.exists())

    def test_invalid_branch_and_source_fail_closed(self):
        for branch in ("main", "dependabot/npm/example", "dependabot/nuget/a..b"):
            with self.subTest(branch=branch), self.assertRaises((ValueError, subprocess.CalledProcessError)):
                refresh.resolve(str(self.remote), branch)
        with self.assertRaises(ValueError):
            refresh.pack(self.source, BRANCH, "0" * 40, self.payload)
        with self.assertRaises(ValueError):
            refresh.write(str(self.remote), BRANCH, "HEAD", self.payload, self.writer)


if __name__ == "__main__":
    unittest.main()

#!/usr/bin/env python3
"""Regression tests for the confidentiality scanner."""

from __future__ import annotations

import base64
import os
from pathlib import Path
import subprocess
import struct
import sys
import tempfile
import time
import unittest
import zipfile
import zlib


SCRIPT = Path(__file__).with_name("check-confidential-identifiers.py")
RELEASE_TAG_SCRIPT = Path(__file__).with_name("validate-release-tag.py")
WORKFLOWS = SCRIPT.parent.parent / ".github" / "workflows"


class ConfidentialIdentifierScannerTests(unittest.TestCase):
    def test_release_tag_validation(self) -> None:
        valid = (
            "v0.1.0",
            "v1.2.3-preview.1",
            "v1.2.3+build-01",
            "v1.2.3-preview.1+build-01",
        )
        invalid = (
            "1.2.3",
            "v01.2.3",
            "v1.2",
            "v1.2.3-01",
            "v1.2.3-",
        )
        for value in valid:
            result = subprocess.run(
                [sys.executable, str(RELEASE_TAG_SCRIPT), value],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(0, result.returncode, value)
        for value in invalid:
            result = subprocess.run(
                [sys.executable, str(RELEASE_TAG_SCRIPT), value],
                capture_output=True,
                text=True,
                check=False,
            )
            self.assertEqual(1, result.returncode, value)

    def setUp(self) -> None:
        self.temp_dir = tempfile.TemporaryDirectory()
        self.repo = Path(self.temp_dir.name) / "repo"
        self.repo.mkdir()
        self._git("init", "--initial-branch=main")
        self._git("config", "user.email", "scanner-tests@example.invalid")
        self._git("config", "user.name", "Scanner Tests")
        self._git("config", "commit.gpgsign", "false")
        (self.repo / "README.md").write_text("public fixture\n", encoding="utf-8")
        self._commit("base")

    def tearDown(self) -> None:
        self.temp_dir.cleanup()

    @property
    def term(self) -> str:
        return "PRIVATE" + "-SENTINEL"

    def _git(self, *args: str) -> subprocess.CompletedProcess[str]:
        return subprocess.run(
            ["git", *args],
            cwd=self.repo,
            check=True,
            capture_output=True,
            text=True,
        )

    def _commit(self, message: str) -> None:
        self._git("add", "--all")
        self._git("commit", "--no-gpg-sign", "-m", message)

    def _run(
        self,
        *,
        terms: list[str] | None = None,
        targets: tuple[Path, ...] = (),
        git_range: str | None = None,
        allow_unreviewed_images: bool = False,
    ) -> subprocess.CompletedProcess[str]:
        env = os.environ.copy()
        env.pop("OSS_CONFIDENTIAL_TERMS_B64", None)
        env.pop("CONFIDENTIAL_GIT_RANGE", None)
        env.pop("CONFIDENTIAL_ALLOW_UNREVIEWED_IMAGES", None)
        if terms is not None:
            payload = "\n".join(terms) + "\n"
            env["OSS_CONFIDENTIAL_TERMS_B64"] = base64.b64encode(
                payload.encode("utf-8")
            ).decode("ascii")
        if git_range is not None:
            env["CONFIDENTIAL_GIT_RANGE"] = git_range
        if allow_unreviewed_images:
            env["CONFIDENTIAL_ALLOW_UNREVIEWED_IMAGES"] = "1"
        return subprocess.run(
            [sys.executable, str(SCRIPT), *(str(target) for target in targets)],
            cwd=self.repo,
            env=env,
            check=False,
            capture_output=True,
            text=True,
        )

    def assert_term_is_redacted(self, result: subprocess.CompletedProcess[str]) -> None:
        self.assertNotIn(
            self.term.casefold(), (result.stdout + result.stderr).casefold()
        )

    def test_missing_denylist_fails_closed(self) -> None:
        result = self._run()

        self.assertNotEqual(0, result.returncode)
        self.assertIn("denylist", result.stdout.casefold())

    def test_empty_denylist_fails_closed(self) -> None:
        result = self._run(terms=["", "  "])

        self.assertNotEqual(0, result.returncode)
        self.assertIn("usable", result.stdout.casefold())

    def test_scans_unicode_and_embedded_binary_content(self) -> None:
        unicode_term = "Privé-Sentinel"
        encodings = ("utf-8", "utf-16-le", "utf-16-be")
        for encoding in encodings:
            with self.subTest(encoding=encoding):
                path = self.repo / f"fixture-{encoding}.bin"
                path.write_bytes(b"prefix" + unicode_term.encode(encoding) + b"suffix")
        (self.repo / "embedded.bin").write_bytes(
            b"prefix" + self.term.encode("utf-8") + b"suffix"
        )
        self._commit("add binary fixtures")

        result = self._run(terms=[unicode_term.lower(), self.term.lower()])

        self.assertEqual(1, result.returncode)
        self.assertIn("tracked file", result.stdout.casefold())
        self.assertNotIn(unicode_term.casefold(), result.stdout.casefold())
        self.assert_term_is_redacted(result)

    def test_scans_every_commit_message_in_the_requested_range(self) -> None:
        (self.repo / "first.txt").write_text("first\n", encoding="utf-8")
        self._commit(f"mention {self.term}")
        (self.repo / "second.txt").write_text("second\n", encoding="utf-8")
        self._commit("clean tip")

        result = self._run(terms=[self.term], git_range="HEAD~2..HEAD")

        self.assertEqual(1, result.returncode)
        self.assertIn("commit message", result.stdout.casefold())
        self.assert_term_is_redacted(result)

    def test_scans_unicode_commit_messages(self) -> None:
        unicode_term = "Privé-Sentinel"
        (self.repo / "first.txt").write_text("first\n", encoding="utf-8")
        self._commit(f"embedded-prefix{unicode_term}suffix")
        (self.repo / "second.txt").write_text("second\n", encoding="utf-8")
        self._commit("clean tip")

        result = self._run(terms=[unicode_term], git_range="HEAD~2..HEAD")

        self.assertEqual(1, result.returncode)
        self.assertIn("commit message", result.stdout.casefold())
        self.assertNotIn(unicode_term.casefold(), result.stdout.casefold())

    def test_scans_content_removed_before_the_tip(self) -> None:
        path = self.repo / "transient.txt"
        path.write_text(self.term, encoding="utf-8")
        self._commit("add transient content")
        path.unlink()
        self._commit("remove transient content")

        result = self._run(terms=[self.term], git_range="HEAD~2..HEAD")

        self.assertEqual(1, result.returncode)
        self.assertIn("commit content", result.stdout.casefold())
        self.assert_term_is_redacted(result)

    def test_rejects_oversized_historical_blob_before_materializing(self) -> None:
        path = self.repo / "oversized.bin"
        with path.open("wb") as stream:
            stream.truncate(128 * 1024 * 1024 + 1)
        self._commit("add oversized transient content")
        path.unlink()
        self._commit("remove oversized transient content")

        result = self._run(terms=[self.term], git_range="HEAD~2..HEAD")

        self.assertEqual(2, result.returncode)
        self.assertIn("per-file limit", result.stdout.casefold())

    def test_scans_archive_entry_names(self) -> None:
        artifact_dir = Path(self.temp_dir.name) / "artifacts"
        artifact_dir.mkdir()
        archive = artifact_dir / "fixture.nupkg"
        with zipfile.ZipFile(archive, "w") as package:
            package.writestr(f"docs/{self.term}.xml", "safe")

        result = self._run(terms=[self.term.lower()], targets=(artifact_dir,))

        self.assertEqual(1, result.returncode)
        self.assertIn("package entry path", result.stdout.casefold())
        self.assert_term_is_redacted(result)

    def test_scans_archive_binary_content(self) -> None:
        artifact_dir = Path(self.temp_dir.name) / "artifacts"
        artifact_dir.mkdir()
        archive = artifact_dir / "fixture.nupkg"
        unicode_term = "Privé-Sentinel"
        with zipfile.ZipFile(archive, "w") as package:
            package.writestr(
                "lib/net10.0/fixture.dll",
                b"prefix" + unicode_term.encode("utf-16-le") + b"suffix",
            )

        result = self._run(terms=[unicode_term.lower()], targets=(artifact_dir,))

        self.assertEqual(1, result.returncode)
        self.assertIn("package content", result.stdout.casefold())
        self.assertNotIn(unicode_term.casefold(), result.stdout.casefold())

    def test_scans_outer_archive_filename(self) -> None:
        artifact_dir = Path(self.temp_dir.name) / "artifacts"
        artifact_dir.mkdir()
        archive = artifact_dir / f"{self.term}.nupkg"
        with zipfile.ZipFile(archive, "w") as package:
            package.writestr("safe.txt", "safe")

        result = self._run(terms=[self.term.lower()], targets=(artifact_dir,))

        self.assertEqual(1, result.returncode)
        self.assertIn("package path", result.stdout.casefold())
        self.assert_term_is_redacted(result)

    def test_scans_ordinary_generated_artifacts(self) -> None:
        artifact_dir = Path(self.temp_dir.name) / "artifacts"
        artifact_dir.mkdir()
        (artifact_dir / "sbom.json").write_text(
            '{"component":"' + self.term + '"}\n', encoding="utf-8"
        )

        result = self._run(terms=[self.term], targets=(artifact_dir,))

        self.assertEqual(1, result.returncode)
        self.assertIn("artifact", result.stdout.casefold())
        self.assert_term_is_redacted(result)

    def test_scans_png_text_metadata_without_matching_compressed_pixels(self) -> None:
        png = self.repo / "fixture.png"
        metadata = b"Description\0" + self.term.encode("ascii")
        chunks = [
            (b"tEXt", metadata),
            (b"IDAT", zlib.compress(b"untrusted compressed pixels")),
            (b"IEND", b""),
        ]
        payload = bytearray(b"\x89PNG\r\n\x1a\n")
        for chunk_type, chunk in chunks:
            payload.extend(struct.pack(">I", len(chunk)))
            payload.extend(chunk_type)
            payload.extend(chunk)
            payload.extend(struct.pack(">I", zlib.crc32(chunk_type + chunk)))
        png.write_bytes(payload)
        self._commit("add png metadata fixture")

        result = self._run(terms=[self.term.lower()])

        self.assertEqual(1, result.returncode)
        self.assertIn("tracked file", result.stdout.casefold())
        self.assert_term_is_redacted(result)

    def test_does_not_scan_png_pixel_payload_as_text(self) -> None:
        png = self.repo / "pixels.png"
        chunks = [(b"IDAT", self.term.encode("ascii")), (b"IEND", b"")]
        payload = bytearray(b"\x89PNG\r\n\x1a\n")
        for chunk_type, chunk in chunks:
            payload.extend(struct.pack(">I", len(chunk)))
            payload.extend(chunk_type)
            payload.extend(chunk)
            payload.extend(struct.pack(">I", zlib.crc32(chunk_type + chunk)))
        png.write_bytes(payload)
        self._commit("add pixel fixture")

        result = self._run(terms=[self.term], allow_unreviewed_images=True)

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)

    def test_unreviewed_raster_image_fails_closed(self) -> None:
        png = self.repo / "unreviewed.png"
        chunks = [(b"IEND", b"")]
        payload = bytearray(b"\x89PNG\r\n\x1a\n")
        for chunk_type, chunk in chunks:
            payload.extend(struct.pack(">I", len(chunk)))
            payload.extend(chunk_type)
            payload.extend(chunk)
            payload.extend(struct.pack(">I", zlib.crc32(chunk_type + chunk)))
        png.write_bytes(payload)
        self._commit("add unreviewed image")

        result = self._run(terms=[self.term])

        self.assertEqual(1, result.returncode)
        self.assertIn("image review required", result.stdout.casefold())

    def test_remote_discussion_attachment_requires_owner_review(self) -> None:
        metadata = Path(self.temp_dir.name) / "comment.json"
        metadata.write_text(
            '{"body":"https://github.com/' + 'user-attachments/assets/example"}',
            encoding="utf-8",
        )

        blocked = self._run(terms=[self.term], targets=(metadata,))
        approved = self._run(
            terms=[self.term],
            targets=(metadata,),
            allow_unreviewed_images=True,
        )

        self.assertEqual(1, blocked.returncode)
        self.assertIn("image review required", blocked.stdout.casefold())
        self.assertEqual(0, approved.returncode, approved.stdout + approved.stderr)

    def test_clean_repository_and_artifacts_pass(self) -> None:
        artifact_dir = Path(self.temp_dir.name) / "artifacts"
        artifact_dir.mkdir()
        (artifact_dir / "sbom.json").write_text("{}\n", encoding="utf-8")

        result = self._run(terms=[self.term], targets=(artifact_dir,))

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertIn("scan passed", result.stdout.casefold())

    def test_many_terms_do_not_multiply_binary_scan_time(self) -> None:
        artifact = Path(self.temp_dir.name) / "large.bin"
        artifact.write_bytes(b"x" * (8 * 1024 * 1024))
        terms = [f"private-sentinel-{index:03d}" for index in range(100)]

        started = time.perf_counter()
        result = self._run(terms=terms, targets=(artifact,))
        elapsed = time.perf_counter() - started

        self.assertEqual(0, result.returncode, result.stdout + result.stderr)
        self.assertLess(elapsed, 5.0)

    def test_workflows_cover_public_metadata_and_preflight_execution(self) -> None:
        confidentiality = (WORKFLOWS / "confidentiality.yml").read_text(
            encoding="utf-8"
        )
        ci = (WORKFLOWS / "ci.yml").read_text(encoding="utf-8")
        release = (WORKFLOWS / "release.yml").read_text(encoding="utf-8")

        self.assertIn("  issues:\n", confidentiality)
        self.assertIn(
            "types: [opened, edited, reopened, labeled, unlabeled]", confidentiality
        )
        self.assertIn("issues/$ISSUE_NUMBER/comments", confidentiality)
        self.assertIn("Metadata budget exceeded", confidentiality)
        self.assertIn("confidentiality-control-reviewed", confidentiality)
        self.assertIn("github-advanced-security[bot]", confidentiality)
        self.assertIn("pull-requests: write", confidentiality)
        self.assertIn("needs: confidentiality-preflight", ci)
        self.assertIn("pull_request:\n    types:", ci)
        self.assertIn("github.event.before", ci)
        self.assertIn("  repository_dispatch:\n", release)
        self.assertNotIn("  workflow_dispatch:\n", release)
        self.assertIn("if: github.actor == github.repository_owner", release)
        self.assertIn("trusted/scripts/validate-release-tag.py", release)
        self.assertLess(
            release.index("Refuse an unsigned tag"),
            release.index("Scan verified release source and history"),
        )
        self.assertLess(
            release.index(
                "Scan verified release source and history before executing it"
            ),
            release.index("actions/setup-dotnet"),
        )


if __name__ == "__main__":
    unittest.main()

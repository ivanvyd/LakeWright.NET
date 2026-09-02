#!/usr/bin/env python3
"""Fail-closed scan for private identifiers in source, metadata, and artifacts."""

from __future__ import annotations

import base64
from dataclasses import dataclass
import hashlib
import os
from pathlib import Path
import re
import subprocess
import struct
import sys
from typing import Iterable
import zipfile
import zlib


MAX_FILE_BYTES = 128 * 1024 * 1024
MAX_ARCHIVE_BYTES = 1024 * 1024 * 1024
MAX_SCAN_WORK_BYTES = 1024 * 1024 * 1024
ARCHIVE_SUFFIXES = {".nupkg", ".snupkg"}
RASTER_SUFFIXES = {".gif", ".jpeg", ".jpg", ".png", ".webp"}
IMAGE_ALLOWLIST = Path(__file__).with_name("approved-public-images.sha256")


class ScanError(RuntimeError):
    """The scan could not establish a trustworthy result."""


@dataclass(frozen=True, order=True)
class Finding:
    surface: str
    location: str
    path_is_private: bool = False


@dataclass
class ScanBudget:
    remaining: int = MAX_SCAN_WORK_BYTES

    def consume(self, size: int) -> None:
        self.remaining -= size
        if self.remaining < 0:
            raise ScanError("The cumulative scan work limit was exceeded.")


class TermMatcher:
    def __init__(self, terms: tuple[str, ...]) -> None:
        folded = sorted({term.casefold() for term in terms}, key=len, reverse=True)
        self._pattern = re.compile("|".join(re.escape(term) for term in folded))

    def contains_text(self, text: str) -> bool:
        return self._pattern.search(text.casefold()) is not None

    def contains_bytes(self, payload: bytes, budget: ScanBudget) -> bool:
        for encoding in ("utf-8", "utf-16-le", "utf-16-be"):
            budget.consume(len(payload))
            if self.contains_text(payload.decode(encoding, errors="ignore")):
                return True
        return False


def load_terms() -> tuple[str, ...]:
    encoded = os.environ.get("OSS_CONFIDENTIAL_TERMS_B64", "")
    if not encoded:
        raise ScanError("No private confidentiality denylist is available.")

    try:
        decoded = base64.b64decode(encoded, validate=True).decode("utf-8")
    except (ValueError, UnicodeDecodeError) as error:
        raise ScanError(
            "OSS_CONFIDENTIAL_TERMS_B64 is not valid base64-encoded UTF-8."
        ) from error

    terms = tuple(
        dict.fromkeys(line.strip() for line in decoded.splitlines() if line.strip())
    )
    if not terms:
        raise ScanError(
            "The private confidentiality denylist contains no usable terms."
        )
    return terms


def load_approved_image_hashes() -> frozenset[str]:
    try:
        lines = IMAGE_ALLOWLIST.read_text(encoding="utf-8").splitlines()
    except OSError as error:
        raise ScanError(
            "The approved public-image allowlist is unavailable."
        ) from error

    hashes: set[str] = set()
    for line in lines:
        stripped = line.strip()
        if not stripped or stripped.startswith("#"):
            continue
        digest = stripped.split(maxsplit=1)[0].casefold()
        if not re.fullmatch(r"[0-9a-f]{64}", digest):
            raise ScanError("The approved public-image allowlist is malformed.")
        hashes.add(digest)
    return frozenset(hashes)


def image_is_approved(payload: bytes, approved_hashes: frozenset[str]) -> bool:
    if os.environ.get("CONFIDENTIAL_ALLOW_UNREVIEWED_IMAGES") == "1":
        return True
    return hashlib.sha256(payload).hexdigest() in approved_hashes


def path_contains_term(path: str, matcher: TermMatcher) -> bool:
    return matcher.contains_text(path)


def read_file(path: Path) -> bytes:
    size = path.stat().st_size
    if size > MAX_FILE_BYTES:
        raise ScanError("A scan target exceeds the per-file safety limit.")
    return path.read_bytes()


def png_text_payload(payload: bytes) -> bytes:
    if not payload.startswith(b"\x89PNG\r\n\x1a\n"):
        raise ScanError("A .png scan target does not have a valid PNG signature.")
    offset = 8
    text_chunks: list[bytes] = []
    while offset + 12 <= len(payload):
        length = struct.unpack(">I", payload[offset : offset + 4])[0]
        chunk_type = payload[offset + 4 : offset + 8]
        data_start = offset + 8
        data_end = data_start + length
        if data_end + 4 > len(payload):
            raise ScanError("A .png scan target contains a truncated chunk.")
        chunk = payload[data_start:data_end]
        if chunk_type == b"tEXt":
            text_chunks.append(chunk)
        elif chunk_type == b"zTXt":
            keyword, separator, compressed = chunk.partition(b"\0")
            if not separator or not compressed or compressed[0] != 0:
                raise ScanError(
                    "A .png scan target contains invalid compressed text metadata."
                )
            text_chunks.extend((keyword, decompress_limited(compressed[1:])))
        elif chunk_type == b"iTXt":
            keyword, separator, rest = chunk.partition(b"\0")
            if not separator or len(rest) < 2:
                raise ScanError(
                    "A .png scan target contains invalid international text metadata."
                )
            compressed_flag, compression_method, rest = rest[0], rest[1], rest[2:]
            language, separator, rest = rest.partition(b"\0")
            if not separator:
                raise ScanError(
                    "A .png scan target contains invalid international text metadata."
                )
            translated, separator, text = rest.partition(b"\0")
            if (
                not separator
                or compression_method != 0
                or compressed_flag not in {0, 1}
            ):
                raise ScanError(
                    "A .png scan target contains unsupported text metadata."
                )
            if compressed_flag == 1:
                text = decompress_limited(text)
            text_chunks.extend((keyword, language, translated, text))
        offset = data_end + 4
        if chunk_type == b"IEND":
            break
    return b"\n".join(text_chunks)


def decompress_limited(payload: bytes) -> bytes:
    try:
        decompressor = zlib.decompressobj()
        result = decompressor.decompress(payload, MAX_FILE_BYTES + 1)
        if len(result) > MAX_FILE_BYTES or decompressor.unconsumed_tail:
            raise ScanError("Compressed metadata exceeds the scan limit.")
        result += decompressor.flush()
    except zlib.error as error:
        raise ScanError("Compressed metadata is invalid.") from error
    if len(result) > MAX_FILE_BYTES:
        raise ScanError("Compressed metadata exceeds the scan limit.")
    return result


def file_contains_term(
    path: Path, payload: bytes, matcher: TermMatcher, budget: ScanBudget
) -> bool:
    if path.suffix.casefold() == ".png":
        return matcher.contains_bytes(png_text_payload(payload), budget)
    return matcher.contains_bytes(payload, budget)


def safe_path(path: Path | str) -> str:
    return str(path).replace("\\", "/")


def git_output(*args: str) -> bytes:
    process = subprocess.run(
        ["git", *args],
        check=False,
        stdout=subprocess.PIPE,
        stderr=subprocess.PIPE,
    )
    if process.returncode != 0:
        raise ScanError(f"git {' '.join(args[:2])} failed; the scan cannot continue.")
    return process.stdout


def scan_revision_range(
    revision_range: str,
    matcher: TermMatcher,
    budget: ScanBudget,
    approved_images: frozenset[str],
) -> set[Finding]:
    if revision_range.startswith("-") or not re.fullmatch(
        r"[A-Za-z0-9._/~^\-]+(?:\.\.[A-Za-z0-9._/~^\-]+)?", revision_range
    ):
        raise ScanError("CONFIDENTIAL_GIT_RANGE is not a valid revision range.")

    findings: set[Finding] = set()
    commits = git_output("rev-list", "--reverse", revision_range).splitlines()
    for encoded_commit in commits:
        commit = encoded_commit.decode("ascii")
        message = git_output("show", "-s", "--format=%B", commit)
        budget.consume(len(message))
        if matcher.contains_text(message.decode("utf-8", errors="replace")):
            findings.add(Finding("commit message", "requested revision range"))

        changed_paths = git_output(
            "diff-tree",
            "--root",
            "-m",
            "--no-commit-id",
            "--name-only",
            "--diff-filter=ACMRT",
            "-r",
            "-z",
            commit,
        ).split(b"\0")
        for encoded_path in dict.fromkeys(path for path in changed_paths if path):
            relative = os.fsdecode(encoded_path)
            private_path = path_contains_term(relative, matcher)
            try:
                payload = git_output("show", f"{commit}:{relative}")
            except ScanError:
                continue
            if Path(
                relative
            ).suffix.casefold() in RASTER_SUFFIXES and not image_is_approved(
                payload, approved_images
            ):
                findings.add(Finding("unreviewed image", relative, private_path))
            if private_path or file_contains_term(
                Path(relative), payload, matcher, budget
            ):
                findings.add(Finding("commit content", relative, private_path))
    return findings


def scan_repository(
    matcher: TermMatcher, budget: ScanBudget, approved_images: frozenset[str]
) -> set[Finding]:
    findings: set[Finding] = set()
    tracked = git_output("ls-files", "-z").split(b"\0")
    for encoded_path in tracked:
        if not encoded_path:
            continue
        relative = os.fsdecode(encoded_path)
        path = Path(relative)
        private_path = path_contains_term(relative, matcher)
        if path.is_symlink():
            payload = os.readlink(path).encode("utf-8", errors="surrogateescape")
            has_private_content = matcher.contains_bytes(payload, budget)
        else:
            payload = read_file(path)
            has_private_content = file_contains_term(path, payload, matcher, budget)
            if path.suffix.casefold() in RASTER_SUFFIXES and not image_is_approved(
                payload, approved_images
            ):
                findings.add(Finding("unreviewed image", relative, private_path))
        if private_path or has_private_content:
            findings.add(Finding("tracked file", relative, private_path))

    revision_range = os.environ.get("CONFIDENTIAL_GIT_RANGE")
    if revision_range:
        findings.update(
            scan_revision_range(revision_range, matcher, budget, approved_images)
        )
    else:
        message = git_output("show", "-s", "--format=%B", "HEAD")
        budget.consume(len(message))
        if matcher.contains_text(message.decode("utf-8", errors="replace")):
            findings.add(Finding("commit message", "HEAD"))
    return findings


def iter_files(target: Path) -> Iterable[Path]:
    if target.is_symlink() or target.is_file():
        yield target
        return
    if not target.is_dir():
        raise ScanError("A requested scan target does not exist.")

    for root, directories, files in os.walk(target, followlinks=False):
        directories.sort()
        files.sort()
        root_path = Path(root)
        for directory in directories:
            path = root_path / directory
            if path.is_symlink():
                yield path
        for filename in files:
            yield root_path / filename


def scan_archive(
    archive: Path,
    matcher: TermMatcher,
    budget: ScanBudget,
    approved_images: frozenset[str],
    display_path: str,
    path_is_private: bool,
) -> set[Finding]:
    findings: set[Finding] = set()
    try:
        with zipfile.ZipFile(archive) as package:
            entries = package.infolist()
            total_size = sum(entry.file_size for entry in entries)
            if total_size > MAX_ARCHIVE_BYTES:
                raise ScanError("A package archive exceeds the total scan limit.")
            for entry in entries:
                entry_private = path_contains_term(entry.filename, matcher)
                if entry_private:
                    findings.add(
                        Finding("package entry path", display_path, path_is_private)
                    )
                if entry.is_dir():
                    continue
                if entry.file_size > MAX_FILE_BYTES:
                    raise ScanError("A package entry exceeds the per-file scan limit.")
                with package.open(entry) as stream:
                    payload = stream.read(MAX_FILE_BYTES + 1)
                if len(payload) > MAX_FILE_BYTES:
                    raise ScanError("A package entry exceeded the per-file scan limit.")
                entry_path = Path(entry.filename)
                if (
                    entry_path.suffix.casefold() in RASTER_SUFFIXES
                    and not image_is_approved(payload, approved_images)
                ):
                    findings.add(
                        Finding("unreviewed image", display_path, path_is_private)
                    )
                if file_contains_term(entry_path, payload, matcher, budget):
                    findings.add(
                        Finding("package content", display_path, path_is_private)
                    )
    except (OSError, zipfile.BadZipFile) as error:
        raise ScanError("Could not inspect a package archive.") from error
    return findings


def scan_targets(
    targets: Iterable[str],
    matcher: TermMatcher,
    budget: ScanBudget,
    approved_images: frozenset[str],
) -> set[Finding]:
    findings: set[Finding] = set()
    for raw_target in targets:
        target = Path(raw_target)
        for path in iter_files(target):
            location = safe_path(path)
            private_path = path_contains_term(location, matcher)
            if path.is_symlink():
                payload = os.readlink(path).encode("utf-8", errors="surrogateescape")
                if private_path or matcher.contains_bytes(payload, budget):
                    findings.add(Finding("artifact", location, private_path))
            elif path.suffix.casefold() in ARCHIVE_SUFFIXES:
                if private_path:
                    findings.add(Finding("package path", location, True))
                findings.update(
                    scan_archive(
                        path,
                        matcher,
                        budget,
                        approved_images,
                        location,
                        private_path,
                    )
                )
            else:
                payload = read_file(path)
                if path.suffix.casefold() in RASTER_SUFFIXES and not image_is_approved(
                    payload, approved_images
                ):
                    findings.add(Finding("unreviewed image", location, private_path))
                if private_path or file_contains_term(path, payload, matcher, budget):
                    findings.add(Finding("artifact", location, private_path))
    return findings


def annotation_path(finding: Finding) -> str | None:
    if finding.path_is_private:
        return None
    return (
        finding.location.replace("%", "%25").replace("\r", "%0D").replace("\n", "%0A")
    )


def report(findings: set[Finding]) -> None:
    for finding in sorted(findings):
        path = annotation_path(finding)
        if finding.surface == "tracked file" and path is not None:
            print(
                f"::error file={path},title=Confidential identifier::"
                "A private denylist term appears in this tracked file."
            )
        elif finding.surface.startswith("commit"):
            detail = "message" if finding.surface == "commit message" else "content"
            print(
                "::error title=Confidential identifier::"
                f"A private denylist term appears in scanned commit {detail}."
            )
        elif finding.surface.startswith("package"):
            suffix = (
                f" Archive: {path}." if path is not None else " Archive path redacted."
            )
            print(
                "::error title=Confidential identifier in package::"
                f"A private denylist term appears in {finding.surface}.{suffix}"
            )
        elif finding.surface == "unreviewed image":
            suffix = f" File: {path}." if path is not None else " File path redacted."
            print(
                "::error title=Public image review required::"
                "A raster image has not received repository-owner visual review."
                f"{suffix}"
            )
        else:
            suffix = f" File: {path}." if path is not None else " File path redacted."
            print(
                "::error title=Confidential identifier in artifact::"
                f"A private denylist term appears in generated output.{suffix}"
            )


def main(argv: list[str]) -> int:
    try:
        terms = load_terms()
        approved_images = load_approved_image_hashes()
        matcher = TermMatcher(terms)
        budget = ScanBudget()
        findings = scan_repository(matcher, budget, approved_images)
        findings.update(scan_targets(argv, matcher, budget, approved_images))
    except ScanError as error:
        print(f"::error title=Confidentiality scan unavailable::{error}")
        return 2

    if findings:
        report(findings)
        return 1
    print("Confidential identifier scan passed.")
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

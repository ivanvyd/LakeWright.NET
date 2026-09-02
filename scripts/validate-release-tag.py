#!/usr/bin/env python3
"""Validate the signed tag name accepted by the release workflow."""

from __future__ import annotations

import re
import sys


RELEASE_TAG = re.compile(
    r"^v(?:0|[1-9][0-9]*)\."
    r"(?:0|[1-9][0-9]*)\."
    r"(?:0|[1-9][0-9]*)"
    r"(?:-(?P<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?"
    r"(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$"
)


def is_valid_release_tag(value: str) -> bool:
    match = RELEASE_TAG.fullmatch(value)
    if match is None:
        return False
    prerelease = match.group("prerelease")
    if prerelease is None:
        return True
    return all(
        not (identifier.isdigit() and len(identifier) > 1 and identifier[0] == "0")
        for identifier in prerelease.split(".")
    )


def main(argv: list[str]) -> int:
    if len(argv) != 1 or not is_valid_release_tag(argv[0]):
        print(
            "::error::Invalid release tag. Expected v<major>.<minor>.<patch> "
            "with optional SemVer prerelease or build metadata."
        )
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main(sys.argv[1:]))

#!/usr/bin/env python3
"""Fail when a Dockerfile's .NET SDK image disagrees with the SDK version in global.json.

global.json pins the SDK and disables roll-forward, so an image carrying any other SDK cannot build
the repository. A `10.0-noble` digest that Dependabot moved to SDK 10.0.401 broke the Signalboard
image that way, and no check noticed. Each SDK base image must use the exact version as its tag,
for example `sdk:10.0.400-noble@sha256:...`.
"""

from __future__ import annotations

import argparse
import json
from pathlib import Path
import re
import sys


SDK_IMAGE = re.compile(r"^\s*FROM\s+\S*/dotnet/sdk:(?P<tag>[^@\s]+)", re.IGNORECASE)
SKIPPED = {".git", "bin", "obj", "node_modules"}


def dockerfiles(root: Path):
    for path in sorted(root.rglob("Dockerfile*")):
        if path.is_file() and not SKIPPED.intersection(path.relative_to(root).parts):
            yield path


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Fail when a Dockerfile's .NET SDK image disagrees with global.json.")
    parser.add_argument("--root", type=Path, default=Path(__file__).resolve().parents[1])
    root = parser.parse_args().root.resolve()
    version = json.loads((root / "global.json").read_text(encoding="utf-8"))["sdk"]["version"]

    pins, errors = 0, []
    for path in dockerfiles(root):
        for number, line in enumerate(path.read_text(encoding="utf-8").splitlines(), start=1):
            match = SDK_IMAGE.match(line)
            if not match:
                continue
            pins += 1
            tag = match.group("tag")
            if tag != version and not tag.startswith(f"{version}-"):
                where = path.relative_to(root).as_posix()
                errors.append(f"{where}:{number}: SDK image tag {tag!r} does not name global.json's {version}")

    for error in errors:
        print(f"::error::{error}")
    if errors:
        return 1
    print(f"SDK pins: {pins} Dockerfile SDK image(s) match global.json {version}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

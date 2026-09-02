#!/usr/bin/env python3
"""Print packed primary package names in deterministic dependency order.

The release workflow publishes every packed package.  Internal package dependencies must be
available before their dependents, while external NuGet dependencies do not participate in this
ordering.  The input directory and version are explicit so CI can exercise the same contract on
its candidate packages.
"""

from __future__ import annotations

import sys
import xml.etree.ElementTree as ElementTree
import zipfile
from collections.abc import Iterable
from pathlib import Path


def read_nuspec(package: Path) -> tuple[str, set[str]]:
    with zipfile.ZipFile(package) as archive:
        nuspecs = [name for name in archive.namelist() if name.endswith(".nuspec")]
        if len(nuspecs) != 1:
            raise ValueError(f"{package.name} must contain exactly one nuspec.")
        root = ElementTree.fromstring(archive.read(nuspecs[0]))

    metadata = next((element for element in root if element.tag.endswith("metadata")), None)
    if metadata is None:
        raise ValueError(f"{package.name} has no nuspec metadata.")

    package_id = next(
        (element.text for element in metadata if element.tag.endswith("id") and element.text),
        None,
    )
    if package_id is None:
        raise ValueError(f"{package.name} has no package id.")

    dependencies = {
        element.attrib["id"]
        for element in metadata.iter()
        if element.tag.endswith("dependency") and "id" in element.attrib
    }
    return package_id, dependencies


def ordered_packages(packages: Iterable[Path]) -> list[Path]:
    package_metadata = {package: read_nuspec(package) for package in packages}
    by_id: dict[str, Path] = {}
    dependencies: dict[str, set[str]] = {}

    for package, (package_id, package_dependencies) in package_metadata.items():
        key = package_id.casefold()
        if key in by_id:
            raise ValueError(f"Duplicate packed package id: {package_id}.")
        by_id[key] = package
        dependencies[key] = {dependency.casefold() for dependency in package_dependencies}

    internal_dependencies = {
        package_id: dependencies[package_id] & by_id.keys()
        for package_id in by_id
    }
    result: list[Path] = []

    while internal_dependencies:
        ready = sorted(package_id for package_id, depends_on in internal_dependencies.items() if not depends_on)
        if not ready:
            unresolved = ", ".join(sorted(internal_dependencies))
            raise ValueError(f"Internal package dependency cycle: {unresolved}.")
        result.extend(by_id[package_id] for package_id in ready)
        for package_id in ready:
            del internal_dependencies[package_id]
        for depends_on in internal_dependencies.values():
            depends_on.difference_update(ready)

    return result


def main() -> int:
    if len(sys.argv) != 3:
        print("Usage: package-publish-order.py <artifacts-directory> <version>", file=sys.stderr)
        return 2

    artifacts = Path(sys.argv[1])
    version = sys.argv[2]
    suffix = f".{version}.nupkg"
    packages = sorted(
        package
        for package in artifacts.glob(f"*{suffix}")
        if not package.name.endswith(f".{version}.snupkg")
    )
    if not packages:
        print(f"No primary packages for version {version} in {artifacts}.", file=sys.stderr)
        return 1

    try:
        for package in ordered_packages(packages):
            print(package.name)
    except (ElementTree.ParseError, ValueError, zipfile.BadZipFile) as error:
        print(f"Cannot order packed packages: {error}", file=sys.stderr)
        return 1
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

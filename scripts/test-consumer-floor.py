#!/usr/bin/env python3
"""Exercise candidate rejection through the actual built or installed verification tool."""

import argparse
from pathlib import Path
import subprocess
import tempfile
import unittest
import zipfile


def package(directory, name, version="2.0.0", dependencies=(), filename=None):
    dependency_xml = "".join(f'<dependency id="{dependency}" version="[{version}]" />' for dependency in dependencies)
    with zipfile.ZipFile(directory / (filename or f"{name}.{version}.nupkg"), "w") as archive:
        archive.writestr(f"{name}.nuspec", f'<package><metadata><id>{name}</id><version>{version}</version><dependencies>{dependency_xml}</dependencies></metadata></package>')


class CandidateRejectionTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="lakewright candidate test ")
        self.directory = Path(self.scratch.name)
        self.addCleanup(self.scratch.cleanup)

    def reject(self, diagnostic, version="2.0.0"):
        result = subprocess.run([*COMMAND, "verify-floor", str(self.directory), version], capture_output=True, text=True, check=False, timeout=30)
        self.assertNotEqual(0, result.returncode)
        self.assertIn(diagnostic, result.stderr)
        self.assertNotIn("Consumer floor passed", result.stdout)

    def test_empty_source_cannot_use_public_or_cached_version(self):
        self.reject("missing LakeWright.Embedding 2.0.0")

    def test_wrong_version(self):
        package(self.directory, "LakeWright.Embedding", "1.0.0")
        self.reject("missing LakeWright.Embedding 2.0.0")

    def test_missing_direct_candidate(self):
        package(self.directory, "LakeWright.Embedding")
        self.reject("missing LakeWright.Databricks 2.0.0")

    def test_missing_transitive_candidate(self):
        package(self.directory, "LakeWright.Embedding", dependencies=["LakeWright.Core"])
        package(self.directory, "LakeWright.Databricks")
        self.reject("missing LakeWright.Core 2.0.0")

    def test_duplicate_identity(self):
        package(self.directory, "LakeWright.Embedding")
        package(self.directory, "LakeWright.Embedding", filename="duplicate.nupkg")
        self.reject("Duplicate candidate package")

    def test_no_manifest(self):
        with zipfile.ZipFile(self.directory / "invalid.nupkg", "w") as archive:
            archive.writestr("unexpected.txt", "not a NuGet package")
        self.reject("omitted its nuspec")

    def test_package_id_cannot_escape_the_candidate_snapshot(self):
        package(self.directory, "LakeWright.Unsafe/../../outside", filename="unsafe.nupkg")
        self.reject("invalid package id")

    def test_version_ranges_and_xml_are_rejected(self):
        for version in ("[2.0.0,)", "2.*", '2.0.0\" />', ""):
            with self.subTest(version=version):
                self.reject("exact three-part", version)


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tool", type=Path, help="Built tool DLL or installed lakewright executable.")
    args = parser.parse_args()
    COMMAND = (["dotnet"] if args.tool.suffix == ".dll" else []) + [str(args.tool.resolve())]
    unittest.main(argv=[__file__])

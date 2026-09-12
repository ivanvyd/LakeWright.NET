#!/usr/bin/env python3
"""Prove verify-floor rejects a complete candidate graph with an API-incompatible Core package."""

import argparse
import io
from pathlib import Path
import shutil
import subprocess
import tarfile
import tempfile
import unittest


ROOT = Path(__file__).resolve().parents[1]
ARCHIVE_PATHS = [
    "Directory.Build.props",
    "Directory.Packages.props",
    "README.md",
    "global.json",
    "src/LakeWright.Core",
]
PARSE_MEMBER = "    public static TenantId Parse(string value) => new(Guid.Parse(value));\n"


class IncompatibleCandidateTests(unittest.TestCase):
    def setUp(self):
        self.scratch = tempfile.TemporaryDirectory(prefix="lakewright incompatible consumer ")
        self.addCleanup(self.scratch.cleanup)
        self.root = Path(self.scratch.name)
        self.core_root = self.root / "core"
        self.candidates = self.root / "candidates"
        self.candidates.mkdir()

    def test_actual_verify_floor_rejects_missing_tenant_id_parse_member(self):
        self.extract_head_core()
        tenant_id = self.core_root / "src/LakeWright.Core/Tenancy/TenantId.cs"
        source = tenant_id.read_text(encoding="utf-8")
        self.assertIn(PARSE_MEMBER, source)
        tenant_id.write_text(source.replace(PARSE_MEMBER, "", 1), encoding="utf-8")

        for package in CANDIDATES.glob("*.nupkg"):
            if not package.name.startswith("LakeWright.Core."):
                shutil.copy2(package, self.candidates / package.name)

        packed = self.pack_incompatible_core()
        shutil.copy2(packed, self.candidates / packed.name)

        result = subprocess.run(
            [*COMMAND, "verify-floor", str(self.candidates), VERSION],
            capture_output=True,
            text=True,
            check=False,
            timeout=180,
        )
        retain("verify-floor.log", result)
        output = result.stdout + result.stderr

        self.assertNotEqual(0, result.returncode, output)
        self.assertRegex(output, r"CS0117.*TenantId.*Parse", output)
        self.assertNotIn("Candidate source is missing", output)
        self.assertNotIn("NU1101", output)
        self.assertNotIn("Unable to resolve", output)
        self.assertNotIn("Consumer floor passed", output)

    def extract_head_core(self):
        archive = subprocess.run(
            ["git", "archive", "--format=tar", "HEAD", *ARCHIVE_PATHS],
            cwd=ROOT,
            capture_output=True,
            check=True,
        )
        with tarfile.open(fileobj=io.BytesIO(archive.stdout), mode="r:") as contents:
            contents.extractall(self.core_root, filter="data")

    def pack_incompatible_core(self):
        restore = subprocess.run(
            ["dotnet", "restore", "src/LakeWright.Core/LakeWright.Core.csproj", "--locked-mode"],
            cwd=self.core_root,
            capture_output=True,
            text=True,
            check=False,
            timeout=180,
        )
        retain("restore.log", restore)
        self.assertEqual(0, restore.returncode, restore.stdout + restore.stderr)

        output = self.root / "packed"
        packed = subprocess.run(
            [
                "dotnet", "pack", "src/LakeWright.Core/LakeWright.Core.csproj", "--no-restore", "-c", "Release",
                f"-p:PackageVersion={VERSION}",
                f"-p:Version={VERSION}",
                "-p:EnablePackageValidation=false",
                "-o", str(output),
            ],
            cwd=self.core_root,
            capture_output=True,
            text=True,
            check=False,
            timeout=180,
        )
        retain("pack.log", packed)
        self.assertEqual(0, packed.returncode, packed.stdout + packed.stderr)
        packages = list(output.glob(f"LakeWright.Core.{VERSION}.nupkg"))
        self.assertEqual(1, len(packages))
        return packages[0]


def retain(name, result):
    if EVIDENCE is None:
        return
    EVIDENCE.mkdir(parents=True, exist_ok=True)
    (EVIDENCE / name).write_text(
        f"exit_code={result.returncode}\n\nstdout:\n{result.stdout}\n\nstderr:\n{result.stderr}",
        encoding="utf-8",
    )


if __name__ == "__main__":
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("tool", type=Path, help="Built tool DLL or installed lakewright executable.")
    parser.add_argument("candidates", type=Path, help="Complete valid candidate package directory.")
    parser.add_argument("version", help="Exact candidate package version.")
    parser.add_argument("--evidence", type=Path, help="Optional private directory for restore, pack, and verify-floor logs.")
    args = parser.parse_args()
    if not args.candidates.is_dir():
        parser.error("candidates must be a directory")
    CANDIDATES = args.candidates.resolve()
    VERSION = args.version
    EVIDENCE = args.evidence.resolve() if args.evidence else None
    COMMAND = (["dotnet"] if args.tool.suffix == ".dll" else []) + [str(args.tool.resolve())]
    unittest.main(argv=[__file__])

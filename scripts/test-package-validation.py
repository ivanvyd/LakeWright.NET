#!/usr/bin/env python3
"""Prove the current package baseline rejects a removed real API in an isolated HEAD fixture."""

import argparse
import io
from pathlib import Path
import subprocess
import tarfile
import tempfile

ROOT = Path(__file__).resolve().parents[1]


def run(directory, *arguments):
    result = subprocess.run(["dotnet", *arguments], cwd=directory, capture_output=True, text=True, check=False, timeout=300)
    return result.returncode, result.stdout + result.stderr


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--evidence", type=Path, required=True)
    args = parser.parse_args()
    args.evidence.mkdir(parents=True, exist_ok=True)
    archive = subprocess.run(["git", "archive", "HEAD", "src/LakeWright.Core", "Directory.Packages.props", "global.json", "README.md", ".editorconfig"], cwd=ROOT, capture_output=True, check=True)
    with tempfile.TemporaryDirectory(prefix="lakewright-api-fixture-") as directory:
        fixture = Path(directory)
        with tarfile.open(fileobj=io.BytesIO(archive.stdout)) as source:
            source.extractall(fixture, filter="data")
        (fixture / "Directory.Build.props").write_bytes((ROOT / "Directory.Build.props").read_bytes())
        project = "src/LakeWright.Core/LakeWright.Core.csproj"
        code, log = run(fixture, "restore", project, "--locked-mode")
        (args.evidence / "compatibility-fixture-restore.log").write_text(log, encoding="utf-8")
        if code:
            raise RuntimeError("The baseline fixture did not restore; no compatibility verdict is established.")
        command = ("pack", project, "-c", "Release", "--no-restore", "-p:Version=2.0.1-ci.0", "-o", "candidate")
        code, log = run(fixture, *command)
        (args.evidence / "compatibility-compatible.log").write_text(log, encoding="utf-8")
        if code:
            raise RuntimeError("The unmodified public API did not pack; inspect the evidence.")
        tenant = fixture / "src/LakeWright.Core/Tenancy/TenantId.cs"
        text = tenant.read_text(encoding="utf-8")
        original = "public static TenantId New()"
        if text.count(original) != 1:
            raise RuntimeError("The public member fixture changed; select a real removable API before continuing.")
        tenant.write_text(text.replace(original, "public static TenantId CreateForCompatibilityProbe()"), encoding="utf-8")
        code, log = run(fixture, *command)
        (args.evidence / "compatibility-incompatible.log").write_text(log, encoding="utf-8")
        if code == 0 or "CP0002" not in log or "TenantId.New()" not in log:
            raise RuntimeError("Removing TenantId.New did not produce the expected API-compatibility failure.")
    print("Compatible HEAD fixture packs; removing TenantId.New fails CP0002 against the current stable baseline.")


if __name__ == "__main__":
    main()

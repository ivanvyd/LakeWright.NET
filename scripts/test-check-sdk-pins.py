#!/usr/bin/env python3
"""Exercise the SDK pin check against small fixture repositories."""

from __future__ import annotations

from pathlib import Path
import subprocess
import sys
import tempfile
import unittest


SCRIPT = Path(__file__).with_name("check-sdk-pins.py")
GLOBAL_JSON = '{"sdk": {"version": "10.0.400", "rollForward": "disable"}}\n'
DIGEST = "@sha256:" + "0" * 64


def run(dockerfile: str | None) -> subprocess.CompletedProcess[str]:
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        (root / "global.json").write_text(GLOBAL_JSON, encoding="utf-8")
        if dockerfile is not None:
            (root / "sample").mkdir()
            (root / "sample" / "Dockerfile").write_text(dockerfile, encoding="utf-8")
        return subprocess.run([sys.executable, str(SCRIPT), "--root", str(root)],
                              capture_output=True, text=True, check=False)


class SdkPinTests(unittest.TestCase):
    def test_exact_version_tag_passes(self):
        result = run(f"FROM mcr.microsoft.com/dotnet/sdk:10.0.400-noble{DIGEST} AS build\n")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("1 Dockerfile SDK image(s)", result.stdout)

    def test_floating_channel_tag_fails(self):
        result = run(f"FROM mcr.microsoft.com/dotnet/sdk:10.0-noble{DIGEST} AS build\n")
        self.assertEqual(result.returncode, 1)
        self.assertIn("sample/Dockerfile:1", result.stdout)

    def test_other_patch_fails(self):
        result = run(f"FROM mcr.microsoft.com/dotnet/sdk:10.0.401-noble{DIGEST} AS build\n")
        self.assertEqual(result.returncode, 1)

    def test_runtime_images_are_ignored(self):
        result = run(f"FROM mcr.microsoft.com/dotnet/aspnet:10.0-noble-chiseled{DIGEST}\n")
        self.assertEqual(result.returncode, 0, result.stdout)
        self.assertIn("0 Dockerfile SDK image(s)", result.stdout)


if __name__ == "__main__":
    unittest.main()

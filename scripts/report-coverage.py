#!/usr/bin/env python3
"""Summarise Cobertura coverage into the CI job summary.

Reported per project rather than as one solution-wide figure, and never gated. A threshold
invites tests written to move a number; the useful signal here is which project is thin. The
first measured run put LakeWright.Databricks at 6.6% against three modules above 89%, because
almost all of its coverage lives in Category=Live tests CI excludes — a single percentage would
have hidden exactly that.
"""

import glob
import sys
import xml.etree.ElementTree as ET


def main() -> int:
    files = sorted(glob.glob("TestResults/**/coverage.cobertura.xml", recursive=True))
    if not files:
        print("No coverage file produced. Is coverlet.collector still referenced?")
        return 0

    if len(files) > 1:
        # One test project today. A second would produce a second file, and reporting one of
        # them as the number is how a partial figure gets read as a whole one.
        print(f"> {len(files)} coverage files; only `{files[-1]}` is summarised below.\n")

    root = ET.parse(files[-1]).getroot()
    pct = lambda rate: f"{float(rate) * 100:.1f}%"

    print("## Coverage\n")
    print(
        f"{pct(root.get('line-rate'))} of lines "
        f"({root.get('lines-covered')}/{root.get('lines-valid')}), "
        f"{pct(root.get('branch-rate'))} of branches. Live tests excluded.\n"
    )
    print("| Project | Lines |")
    print("|---|---|")
    for package in sorted(root.find("packages"), key=lambda p: float(p.get("line-rate"))):
        print(f"| {package.get('name')} | {pct(package.get('line-rate'))} |")
    return 0


if __name__ == "__main__":
    sys.exit(main())

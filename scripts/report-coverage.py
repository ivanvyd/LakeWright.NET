#!/usr/bin/env python3
"""Summarise Cobertura coverage into the CI job summary.

Reported per project rather than as one solution-wide figure, and never gated. A threshold
invites tests written to move a number; the useful signal here is which project is thin. The
first measured run put LakeWright.Databricks at 6.6% against three modules above 89%, because
almost all of its coverage lives in Category=Live tests CI excludes — a single percentage would
have hidden exactly that.
"""

import glob
import hashlib
import sys
import xml.etree.ElementTree as ET


def distinct_reports() -> list[str]:
    """Coverage files, minus the data collector's staging duplicates.

    The collector writes each report twice: once under the results directory and once under its
    own `<runner>_<timestamp>/In/<runner>/` staging path. The two are byte-identical, so counting
    them as two files made the run announce partial coverage it did not have. Deduplicating by
    content also means a genuine second test project still registers as a second report.
    """
    seen: dict[str, str] = {}
    for path in sorted(glob.glob("TestResults/**/coverage.cobertura.xml", recursive=True)):
        with open(path, "rb") as handle:
            digest = hashlib.sha256(handle.read()).hexdigest()
        seen.setdefault(digest, path)
    return list(seen.values())


def main() -> int:
    files = distinct_reports()
    if not files:
        print("No coverage file produced. Is coverlet.collector still referenced?")
        return 0

    if len(files) > 1:
        # A second test project would produce a genuinely different report, and reporting one of
        # them as the number is how a partial figure gets read as a whole one.
        print(f"> {len(files)} distinct coverage reports; only `{files[0]}` is summarised below.\n")

    root = ET.parse(files[0]).getroot()
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

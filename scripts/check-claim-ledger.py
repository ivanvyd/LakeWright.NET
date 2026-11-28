#!/usr/bin/env python3
"""Keep the README's capability citations tied to rows in docs/compatibility.md.

Every table in the compatibility matrix, apart from the legend and the environment record, is a
claim ledger: its first column holds a stable `LW-Cnnn` ID behind a matching `#lw-cnnn` anchor.
This fails on a claim row without a well-formed ID, on a duplicate ID, and on a README citation
that names no row. A citation to a missing row reads as evidence and is not.

It also warns, without failing, about each live-verified row dated before the first release of the
current major version. The date stays true as history; the warning says it is not evidence for the
line now being shipped. Major version comes from VersionPrefix in Directory.Build.props, and the
first release date of that major from its CHANGELOG.md heading. Before that release exists, every
live-verified row predates it.
"""

from __future__ import annotations

import argparse
from dataclasses import dataclass
from datetime import date
from pathlib import Path
import re
import sys


ROOT = Path(__file__).resolve().parents[1]
LEDGER = "docs/compatibility.md"
# Reference tables that describe the ledger rather than make claims.
NON_CLAIM_SECTIONS = {"Legend", "Recorded verification environments"}
ID_CELL = re.compile(r'<a id="(lw-c\d{3})"></a>(LW-C\d{3})')
CITATION = re.compile(r"(?i)\blw-[a-z0-9]+")
CITATION_LINK = re.compile(r"\[(LW-C\d{3})\]\(docs/compatibility\.md#([^)]*)\)")
ISO_DATE = re.compile(r"\b(\d{4}-\d{2}-\d{2})\b")
CELL_SPLIT = re.compile(r"(?<!\\)\|")


@dataclass
class Row:
    claim_id: str
    line: int
    status: str
    dates: list[date]


def cells(line: str) -> list[str]:
    return [cell.strip() for cell in CELL_SPLIT.split(line.strip().strip("|"))]


def parse_ledger(text: str, errors: list[str]) -> list[Row]:
    rows: list[Row] = []
    section = ""
    fenced = False
    header: list[str] | None = None
    separator_pending = False
    claim_table = False
    for number, line in enumerate(text.splitlines(), start=1):
        stripped = line.strip()
        if stripped.startswith("```"):
            fenced = not fenced
            continue
        if fenced:
            continue
        if not stripped.startswith("|"):
            header = None
            if stripped.startswith("#"):
                section = stripped.lstrip("#").strip()
            continue
        if header is None:
            header = cells(stripped)
            separator_pending = True
            claim_table = section not in NON_CLAIM_SECTIONS
            if claim_table and header[0] != "ID":
                errors.append(f"{LEDGER}:{number}: table under '{section}' has no leading ID column")
                claim_table = False
            continue
        if separator_pending or not claim_table:
            separator_pending = False
            continue
        row = cells(stripped)
        match = ID_CELL.fullmatch(row[0])
        if not row[0]:
            errors.append(f"{LEDGER}:{number}: claim row has no ID")
            continue
        if not match or match.group(1) != match.group(2).lower():
            errors.append(f"{LEDGER}:{number}: malformed claim ID cell {row[0]!r}; "
                          'expected <a id="lw-cnnn"></a>LW-Cnnn')
            continue
        columns = dict(zip(header, row))
        dated = columns.get("Date") or " ".join(row[1:])
        rows.append(Row(match.group(2), number, columns.get("Status", ""),
                        [date.fromisoformat(value) for value in ISO_DATE.findall(dated)]))
    return rows


def major_release(root: Path, errors: list[str]) -> tuple[str, date | None]:
    props = (root / "Directory.Build.props").read_text(encoding="utf-8")
    version = re.search(r"<VersionPrefix>(\d+)\.\d+\.\d+</VersionPrefix>", props)
    if not version:
        errors.append("Directory.Build.props: no stable VersionPrefix")
        return "", None
    major = f"{version.group(1)}.0.0"
    changelog = root / "CHANGELOG.md"
    text = changelog.read_text(encoding="utf-8") if changelog.is_file() else ""
    released = re.search(rf"^## \[{re.escape(major)}\] [—–-] (\d{{4}}-\d{{2}}-\d{{2}})", text, re.M)
    return major, date.fromisoformat(released.group(1)) if released else None


def main() -> int:
    parser = argparse.ArgumentParser(
        description="Keep the README's capability citations tied to rows in docs/compatibility.md.")
    parser.add_argument("--root", type=Path, default=ROOT, help="repository root (default: this checkout)")
    root = parser.parse_args().root

    errors: list[str] = []
    rows = parse_ledger((root / LEDGER).read_text(encoding="utf-8"), errors)
    seen: dict[str, int] = {}
    for row in rows:
        if row.claim_id in seen:
            errors.append(f"{LEDGER}:{row.line}: duplicate claim ID {row.claim_id}, first used on line {seen[row.claim_id]}")
        else:
            seen[row.claim_id] = row.line

    readme = (root / "README.md").read_text(encoding="utf-8")
    citations = sorted({citation.upper() for citation in CITATION.findall(readme)})
    for citation in citations:
        if citation not in seen:
            errors.append(f"README.md: cites {citation}, which is not a row in {LEDGER}")
    for label, anchor in CITATION_LINK.findall(readme):
        if anchor != label.lower():
            errors.append(f"README.md: link labelled {label} points at #{anchor}")

    major, released = major_release(root, errors)
    if errors:
        for error in errors:
            print(f"ERROR: {error}")
        print(f"\n{len(errors)} claim ledger problem(s). IDs are never renumbered; fix the row or the citation.")
        return 1

    stale = 0
    for row in rows:
        if not row.status.startswith("**Verified") or row.status.startswith("**Verified locally"):
            continue
        if not row.dates:
            stale += 1
            print(f"::warning file={LEDGER},line={row.line}::{row.claim_id} is live-verified with no date to check against {major}")
        elif released is None or max(row.dates) < released:
            stale += 1
            since = f"its {released.isoformat()} release" if released else "its release, which CHANGELOG.md does not date yet"
            print(f"::warning file={LEDGER},line={row.line}::{row.claim_id} was last live-verified on "
                  f"{max(row.dates).isoformat()}, before {major} ({since})")
    print(f"Claim ledger: {len(rows)} IDs, {len(citations)} README citation(s) resolved; "
          f"{stale} live-verified row(s) predate {major}.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

#!/usr/bin/env python3
"""Keep public install/display references tied to the last verified stable release."""

import json
from pathlib import Path
import re
import sys

ROOT = Path(__file__).resolve().parents[1]
manifest = json.loads((ROOT / "scripts/released-version.json").read_text(encoding="utf-8"))
version = manifest["version"]
if not re.fullmatch(r"\d+\.\d+\.\d+", version):
    sys.exit("Released version must be stable; candidates do not update public install references.")
release = f"https://github.com/ivanvyd/LakeWright.NET/releases/tag/v{version}"
if manifest["releaseUrl"] != release or not (ROOT / f"docs/release-evidence/v{version}.md").is_file():
    sys.exit("Released-version metadata and release evidence disagree.")

checks = {
    "README.md": [f"Current stable release: [v{version}]({release})"],
    "ROADMAP.md": [f"Current stable release: [v{version}]({release})"],
    "samples/Signalboard/Components/Pages/Home.razor": [f'href="{release}"', f"--version {version}", f"<span>v{version}</span>"],
    "samples/Signalboard/Components/Layout/MainLayout.razor": [f"/packages/LakeWright.AspNetCore/{version}/"],
}
for path, expected in checks.items():
    content = (ROOT / path).read_text(encoding="utf-8")
    if any(token not in content for token in expected):
        sys.exit(f"Public release reference drift in {path}; update only after verifying a real stable release.")
    if path.endswith(".razor"):
        references = re.findall(r"(?:releases/tag/v|--version |nuget.org/packages/LakeWright\.[^/]+/)(\d+\.\d+\.\d+(?:-[\w.]+)?)", content)
        if any(reference != version for reference in references):
            sys.exit(f"Mixed public release versions in {path}.")
print(f"Public landing/install/docs references match verified stable v{version}. Candidate VersionPrefix is independent.")

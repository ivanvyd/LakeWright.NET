#!/usr/bin/env python3
"""Build and run the published net8 consumer in a fresh directory without repository props."""

import argparse
import json
import os
from pathlib import Path
import shutil
import subprocess
import tempfile


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--artifacts", type=Path, required=True)
    args = parser.parse_args()
    artifacts = args.artifacts.resolve()
    artifacts.mkdir(parents=True, exist_ok=True)
    source = Path(__file__).resolve().parents[1] / "examples" / "package-quickstart"
    with tempfile.TemporaryDirectory(prefix="consumer-", dir=artifacts) as scratch:
        consumer = Path(scratch)
        environment = dict(os.environ, NUGET_PACKAGES=str(consumer / "packages"))
        for pattern in ("*.cs", "*.csproj", "*.props", "*.targets", "global.json", "NuGet.Config"):
            for item in source.glob(pattern):
                shutil.copy2(item, consumer / item.name)
        commands = [
            ["dotnet", "restore", "--configfile", "NuGet.Config", "--no-http-cache"],
            ["dotnet", "build", "-c", "Release", "--no-restore"],
            ["dotnet", "run", "-c", "Release", "--no-build", "--no-restore", "--", "--offline"],
        ]
        for index, command in enumerate(commands):
            result = subprocess.run(command, cwd=consumer, env=environment, capture_output=True, text=True, timeout=180, check=False)
            (artifacts / f"{index + 1}.log").write_text(result.stdout + result.stderr, encoding="utf-8")
            if result.returncode:
                raise RuntimeError(f"{' '.join(command)} failed:\n{result.stdout}{result.stderr}")
            if index == 2:
                if "OFFLINE_OK: published 2.1.2" not in result.stdout or "transport requests remain 1" not in result.stdout:
                    raise RuntimeError("The consumer did not prove allow and deny behavior.")
                print(result.stdout, end="")
        assets = json.loads((consumer / "obj" / "project.assets.json").read_text(encoding="utf-8"))
        packages = {name for name in assets["libraries"] if name.startswith("LakeWright.")}
        if packages != {"LakeWright.Core/2.1.2", "LakeWright.Databricks/2.1.2"}:
            raise RuntimeError(f"Unexpected LakeWright package identities: {sorted(packages)}")
        references = assets["project"]["restore"]["frameworks"]["net8.0"].get("projectReferences", {})
        if references:
            raise RuntimeError("The standalone consumer acquired project references.")
        sources = sorted(assets["project"]["restore"]["sources"])
        folders = list(assets["packageFolders"])
        if sources != ["https://api.nuget.org/v3/index.json"]:
            raise RuntimeError(f"Unexpected package sources: {sources}")
        if len(folders) != 1 or Path(folders[0]).resolve() != consumer / "packages":
            raise RuntimeError(f"Restore escaped the fresh package cache: {folders}")
        (artifacts / "packages.json").write_text(json.dumps(sorted(packages), indent=2) + "\n", encoding="utf-8")
        (artifacts / "restore-evidence.json").write_text(json.dumps({
            "targetFramework": "net8.0", "packageSources": sources, "packageFolders": folders,
            "lakeWrightPackages": sorted(packages), "projectReferences": references,
        }, indent=2) + "\n", encoding="utf-8")
        print("PUBLISHED_CONSUMER_OK: net8.0; exact 2.1.2 packages; fresh package cache; nuget.org only; no project references.")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Fail closed when NuGet advisory data is unavailable or exceeds the release policy."""

import argparse
import json
from pathlib import Path
import subprocess
import sys


class InvalidReport(ValueError):
    pass


def object_value(value):
    if not isinstance(value, dict):
        raise InvalidReport("Expected an object in the NuGet report.")
    for entry in array_value(value.get("logs", [])):
        if not isinstance(entry, dict) or entry.get("level") not in ("Information", "Info", "Debug", "Verbose"):
            raise InvalidReport("NuGet reported a warning or error; advisory coverage is not established.")
    return value


def array_value(value):
    if not isinstance(value, list):
        raise InvalidReport("Expected an array in the NuGet report.")
    return value


def evaluate(report):
    report = object_value(report)
    if type(report.get("version")) is not int or report["version"] != 1:
        raise InvalidReport("Unsupported NuGet report version.")
    parameters = report.get("parameters")
    if not isinstance(parameters, str) or not {"--vulnerable", "--include-transitive"}.issubset(parameters.split()):
        raise InvalidReport("Report must include direct and transitive vulnerabilities.")
    sources = array_value(report.get("sources"))
    projects = array_value(report.get("projects"))
    if not sources or not all(isinstance(source, str) and source for source in sources) or not projects:
        raise InvalidReport("NuGet report omitted its sources or projects.")
    findings = []
    for project in projects:
        project = object_value(project)
        if not isinstance(project.get("path"), str) or not project["path"]:
            raise InvalidReport("NuGet report omitted a project path.")
        for framework in array_value(project.get("frameworks", [])):
            framework = object_value(framework)
            if not isinstance(framework.get("framework"), str) or not framework["framework"]:
                raise InvalidReport("NuGet report omitted a target framework.")
            for kind in ("topLevelPackages", "transitivePackages"):
                for package in array_value(framework.get(kind, [])):
                    package = object_value(package)
                    if not all(isinstance(package.get(key), str) and package[key] for key in ("id", "resolvedVersion")):
                        raise InvalidReport("NuGet report omitted package identity.")
                    vulnerabilities = array_value(package.get("vulnerabilities"))
                    if not vulnerabilities:
                        raise InvalidReport("Vulnerable package omitted advisory details.")
                    for advisory in vulnerabilities:
                        advisory = object_value(advisory)
                        severity = advisory.get("severity")
                        if severity not in ("Low", "Moderate", "High", "Critical"):
                            raise InvalidReport("Unknown advisory severity.")
                        if not isinstance(advisory.get("advisoryurl"), str) or not advisory["advisoryurl"]:
                            raise InvalidReport("Advisory omitted its source.")
                        if severity != "Low":
                            findings.append((package["id"], package["resolvedVersion"], severity))
    return findings


def scan():
    result = subprocess.run(
        ["dotnet", "list", "package", "--vulnerable", "--include-transitive", "--format", "json", "--output-version", "1"],
        capture_output=True, text=True, check=False, timeout=300,
    )
    if result.returncode != 0:
        raise InvalidReport(f"NuGet scan failed (exit {result.returncode}); no clean verdict is available.")
    if result.stderr.strip():
        raise InvalidReport("NuGet emitted stderr; inspect the scan locally before trusting its coverage.")
    return result.stdout


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    source = parser.add_mutually_exclusive_group()
    source.add_argument("--report", type=Path, help="Evaluate an existing NuGet JSON report instead of running dotnet.")
    source.add_argument("--output", type=Path, help="Save a successfully obtained scan for audit evidence.")
    args = parser.parse_args()
    try:
        raw = args.report.read_text(encoding="utf-8-sig") if args.report else scan()
        findings = evaluate(json.loads(raw))
        if args.output:
            args.output.write_text(raw, encoding="utf-8")
    except (InvalidReport, json.JSONDecodeError, OSError, subprocess.TimeoutExpired) as error:
        # Do not echo subprocess output or source URLs; feeds can carry credentials.
        print(f"Advisory gate unavailable: {error if isinstance(error, InvalidReport) else type(error).__name__}", file=sys.stderr)
        return 2
    for package, version, severity in sorted(set(findings)):
        print(f"{package} {version}: {severity}")
    if findings:
        return 1
    print("NuGet scan succeeded: no Moderate, High, or Critical advisories.")
    return 0


if __name__ == "__main__":
    sys.exit(main())

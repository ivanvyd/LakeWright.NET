#!/usr/bin/env python3
"""Transfer NuGet lock data between isolated maintenance jobs without checking out candidate code."""

import argparse
import base64
import json
import os
from pathlib import Path
import re
import stat
import subprocess


MAX_PAYLOAD = 16 * 1024 * 1024
MAX_LOCK = 1024 * 1024
MAX_FILES = 128


def git(directory, *arguments, data=None, env=None):
    return subprocess.run(["git", *arguments], cwd=directory, input=data,
                          stdout=subprocess.PIPE, stderr=subprocess.PIPE, check=True,
                          env=env, timeout=120).stdout


def branch_ref(branch):
    if not branch.startswith("dependabot/nuget/"):
        raise ValueError("Only a dependabot/nuget/* branch can be refreshed.")
    ref = "refs/heads/" + branch
    git(None, "check-ref-format", ref)
    return ref


def source_sha(value):
    if not re.fullmatch(r"[0-9a-f]{40}", value):
        raise ValueError("Expected an immutable source SHA.")
    return value


def lock_path(value):
    if (not isinstance(value, str) or len(value) > 512
            or not re.fullmatch(r"[A-Za-z0-9._/-]+", value)
            or any(part in ("", ".", "..") or part.lower() == ".git" for part in value.split("/"))
            or value.split("/")[-1] != "packages.lock.json"):
        raise ValueError("Expected a regular packages.lock.json path.")
    return value


def unique_object(pairs):
    result = {}
    for key, value in pairs:
        if key in result:
            raise ValueError("Duplicate JSON property.")
        result[key] = value
    return result


def parse_json(data):
    return json.loads(data, object_pairs_hook=unique_object, parse_constant=reject_constant)


def reject_constant(value):
    raise ValueError("Non-finite JSON number.")


def string_map(value):
    return isinstance(value, dict) and all(isinstance(k, str) and k and isinstance(v, str) and v
                                           for k, v in value.items())


def validate_lock(data):
    if len(data) > MAX_LOCK:
        raise ValueError("Lock file exceeds the size budget.")
    lock = parse_json(data)
    if (not isinstance(lock, dict) or set(lock) != {"version", "dependencies"}
            or type(lock["version"]) is not int or lock["version"] not in (1, 2)
            or not isinstance(lock["dependencies"], dict)):
        raise ValueError("Invalid NuGet lock file header.")
    for framework, packages in lock["dependencies"].items():
        if not framework or not isinstance(packages, dict):
            raise ValueError("Invalid NuGet target framework.")
        for name, package in packages.items():
            if (not name or not isinstance(package, dict)
                    or not set(package) <= {"type", "requested", "resolved", "contentHash", "dependencies"}
                    or package.get("type") not in ("Direct", "Transitive", "CentralTransitive", "Project")
                    or ("dependencies" in package and not string_map(package["dependencies"]))):
                raise ValueError("Invalid NuGet dependency entry.")
            if package["type"] == "Project":
                if not set(package) <= {"type", "dependencies"}:
                    raise ValueError("Invalid NuGet project dependency.")
                continue
            for field in ("resolved", "contentHash"):
                if not isinstance(package.get(field), str) or not package[field]:
                    raise ValueError("Missing NuGet package identity or hash.")
            if len(base64.b64decode(package["contentHash"], validate=True)) != 64:
                raise ValueError("Expected a NuGet SHA-512 content hash.")
            if ("requested" in package and (not isinstance(package["requested"], str) or not package["requested"])):
                raise ValueError("Invalid NuGet version request.")
            if package["type"] in ("Direct", "CentralTransitive") and "requested" not in package:
                raise ValueError("Missing NuGet version request.")


def lock_entries(repository, base):
    entries = {}
    for entry in git(repository, "ls-tree", "-r", "-z", base).split(b"\0"):
        if not entry:
            continue
        metadata, raw_path = entry.split(b"\t", 1)
        path = raw_path.decode("utf-8")
        if path.split("/")[-1] != "packages.lock.json":
            continue
        lock_path(path)
        mode, kind, _ = metadata.decode("ascii").split()
        if kind != "blob" or mode not in ("100644", "100755"):
            raise ValueError("Tracked lock files must be regular files.")
        entries[path] = mode
    if not entries or len(entries) > MAX_FILES:
        raise ValueError("Tracked lock file count exceeds the budget or is empty.")
    return entries


def resolve(remote, branch):
    ref = branch_ref(branch)
    lines = git(None, "ls-remote", "--exit-code", "--refs", remote, ref).decode("ascii").splitlines()
    if len(lines) != 1:
        raise ValueError("Expected exactly one maintenance branch.")
    sha, found_ref = lines[0].split("\t")
    if found_ref != ref:
        raise ValueError("Maintenance branch did not match.")
    return source_sha(sha)


def pack(repository, branch, base, output):
    branch_ref(branch)
    source_sha(base)
    if git(repository, "rev-parse", "HEAD").decode().strip() != base:
        raise ValueError("Candidate HEAD differs from the trusted source SHA.")
    entries = lock_entries(repository, base)
    # Ignored build outputs stay local; every visible change must be an existing lock file.
    status = git(repository, "status", "--porcelain=v1", "-z", "--untracked-files=all")
    for record in status.split(b"\0"):
        if not record:
            continue
        if record[:2] not in (b" M", b"M ", b"MM") or record[3:].decode("utf-8") not in entries:
            raise ValueError("Candidate contains an unexpected changed, staged or untracked path.")
    files = {}
    for path in entries:
        target = repository / path
        for part in (target, *target.parents):
            if part == repository:
                break
            if part.is_symlink():
                raise ValueError("Candidate lock paths cannot contain symlinks.")
        if not stat.S_ISREG(target.lstat().st_mode) or target.stat().st_size > MAX_LOCK:
            raise ValueError("Candidate lock file is not a bounded regular file.")
        data = target.read_bytes()
        validate_lock(data)
        files[path] = base64.b64encode(data).decode("ascii")
    payload = json.dumps({"version": 1, "branch": branch, "base": base, "files": files}).encode("utf-8")
    if len(payload) > MAX_PAYLOAD:
        raise ValueError("Payload exceeds the size budget.")
    output.write_bytes(payload)


def read_payload(path, branch, base):
    if set(path.parent.iterdir()) != {path}:
        raise ValueError("Artifact must contain only the lock payload.")
    if path.is_symlink() or not path.is_file() or path.stat().st_size > MAX_PAYLOAD:
        raise ValueError("Expected a bounded regular payload file.")
    payload = parse_json(path.read_bytes())
    if (not isinstance(payload, dict) or set(payload) != {"version", "branch", "base", "files"}
            or type(payload["version"]) is not int or payload["version"] != 1
            or payload["branch"] != branch or payload["base"] != base
            or not isinstance(payload["files"], dict) or not 0 < len(payload["files"]) <= MAX_FILES):
        raise ValueError("Payload schema, branch or base differs from trusted resolution.")
    files = {}
    for path, encoded in payload["files"].items():
        lock_path(path)
        if not isinstance(encoded, str) or len(encoded) > 4 * ((MAX_LOCK + 2) // 3):
            raise ValueError("Invalid encoded lock file.")
        data = base64.b64decode(encoded, validate=True)
        validate_lock(data)
        files[path] = data
    return files


def write(remote, branch, base, payload, repository):
    ref = branch_ref(branch)
    source_sha(base)
    files = read_payload(payload, branch, base)
    # No candidate worktree, Git configuration, attributes, hooks or binaries are loaded.
    template = repository.parent / (repository.name + "-empty-template")
    template.mkdir()
    git(None, "init", "--bare", "--template=" + str(template), str(repository))
    git(repository, "fetch", "--no-tags", "--no-recurse-submodules", remote, ref)
    if git(repository, "rev-parse", "FETCH_HEAD").decode().strip() != base:
        raise ValueError("Maintenance branch advanced after trusted resolution; run again.")
    entries = lock_entries(repository, base)
    if files.keys() != entries.keys():
        raise ValueError("Payload must contain exactly the tracked regular lock files.")
    git(repository, "read-tree", base)
    for path, data in files.items():
        mode = entries[path]
        blob = git(repository, "hash-object", "-w", "--stdin", data=data).decode().strip()
        git(repository, "update-index", "--cacheinfo", mode, blob, path)
    tree = git(repository, "write-tree").decode().strip()
    if tree == git(repository, "rev-parse", base + "^{tree}").decode().strip():
        print("Lock files already match the manifests. Nothing to commit.")
        return
    environment = dict(os.environ, GIT_AUTHOR_NAME="github-actions[bot]",
                       GIT_AUTHOR_EMAIL="41898282+github-actions[bot]@users.noreply.github.com",
                       GIT_COMMITTER_NAME="github-actions[bot]",
                       GIT_COMMITTER_EMAIL="41898282+github-actions[bot]@users.noreply.github.com")
    message = ("Refresh the lock files for this bump\n\n"
               "Regenerated with dotnet restore --force-evaluate, then verified with\n"
               "--locked-mode and a Release build in an isolated read-only job.\n")
    commit = git(repository, "commit-tree", tree, "-p", base, data=message.encode(), env=environment).decode().strip()
    # A normal push rejects concurrent advancement; never force a maintenance update.
    git(repository, "push", remote, commit + ":" + ref)
    print("Pushed refreshed lock files.")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("command", choices=("resolve", "pack", "write"))
    parser.add_argument("--branch", required=True)
    parser.add_argument("--remote")
    parser.add_argument("--base")
    parser.add_argument("--repository", type=Path)
    parser.add_argument("--payload", type=Path)
    args = parser.parse_args()
    try:
        if args.command == "resolve":
            print(resolve(args.remote, args.branch))
        elif args.command == "pack":
            pack(args.repository.resolve(), args.branch, args.base, args.payload)
        else:
            write(args.remote, args.branch, args.base, args.payload, args.repository.resolve())
    except (ValueError, OSError, RecursionError, subprocess.SubprocessError) as error:
        parser.exit(1, f"Lock refresh rejected: {error}\n")


if __name__ == "__main__":
    main()

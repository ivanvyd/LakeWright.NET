# Releasing

A release is a signed tag. Everything else — building, packing, attesting, publishing to nuget.org
and creating the GitHub release — happens in `.github/workflows/release.yml` when a maintainer
sends the repository's default-branch-only release event for that tag.

## Why the tag is signed

A release tag asserts "this exact commit is what we published". Unsigned, that assertion rests on
nobody having pushed a tag they should not have, and tag-push rights are usually wider than release
rights. Signed, it is checkable by anyone, afterwards, without trusting the pipeline that produced
it.

The workflow refuses an unsigned tag before it builds anything, and refuses a *lightweight* tag
outright: `git tag v1.0.0` creates a pointer with no object to sign, so there is nothing to verify.
Release tags must be created with `git tag -s`.

Verification is asked of GitHub rather than performed on the runner. `git verify-tag` would need the
public key present during the run, and a key the runner supplies is a key that anyone controlling
the runner supplies. GitHub checks the signature against the keys registered to the account, which
is the property worth proving.

## Setting up signing, once

SSH signing is the shorter path on Windows and needs no extra software; GPG works equally well if
you already use it.

### SSH

Use a key dedicated to signing rather than reusing an authentication key — they are registered
separately on GitHub and a signing key that is also an access key widens what one leak costs.

```bash
ssh-keygen -t ed25519 -C "signing key for LakeWright.NET releases" -f ~/.ssh/id_signing

git config --global gpg.format ssh
git config --global user.signingkey ~/.ssh/id_signing.pub
git config --global tag.gpgSign true
```

Then add the **public** half at <https://github.com/settings/keys> — choose **New SSH key** and set
the key type to **Signing Key**, not Authentication Key. GitHub verifies signatures only against
keys registered in that role.

### GPG

```bash
gpg --full-generate-key            # ed25519 is fine; set an expiry you will actually renew
gpg --list-secret-keys --keyid-format=long

git config --global user.signingkey <KEY_ID>
git config --global tag.gpgSign true
```

Add the public key at <https://github.com/settings/keys> under **GPG keys**.

## Cutting a release

1. **Write the CHANGELOG section first.** Rename `## [Unreleased]` to the version being released and
   open a fresh `## [Unreleased]` above it. The workflow extracts the section matching the tag
   *exactly* and fails when there is not one — notes for the wrong version are worse than a failed
   release. Land that through a pull request.

2. **Tag it, signed and annotated.** Only the repository owner can send the release event; other
   writers cannot grant a release job its publication permissions.

   ```bash
   git switch main && git pull
   git tag -s v1.0.0 -m "v1.0.0"
   git push origin v1.0.0
   gh api repos/ivanvyd/LakeWright.NET/dispatches \
     --method POST \
     -f event_type=release \
     -F 'client_payload[tag]=v1.0.0'
   ```

   The version follows SemVer. A tag with a hyphen is published as a GitHub prerelease and to
   nuget.org with `--prerelease`; a tag without a hyphen is published as a stable release.
   Build metadata is stripped before the prerelease check, so `1.0.0+exp-sha.5114f85` is read
   as stable. [ADR 0010](../decisions/0010-publish-prerelease-packages.md) was the rationale
   for refusing stable tags; [ADR 0019](../decisions/0019-stable-1-0-0.md) is the rationale
   for no longer refusing them.

3. **Watch the repository-dispatch run.** GitHub loads this event's workflow and confidentiality
   scanner only from the default branch. In order it verifies and pins the tag object and commit,
   checks out and scans that commit, derives the version, builds, tests, packs, generates a CycloneDX
   SBOM, and extracts the release notes in a read-only job. A separate publication job downloads
   only that immutable same-run artifact, attests build provenance, rechecks that the tag did not
   move, publishes the internal package dependencies before their consumers to nuget.org, and waits
   until every package appears in NuGet's public flat-container index. Only then does it create the
   GitHub release. A timeout is a failed release:
   re-run the same immutable publication after NuGet becomes available; do not create a release that
   claims a version consumers cannot restore. Tagged build hooks never execute with release or OIDC
   permissions.

## If a release goes wrong

Before the nuget.org push, delete the tag, fix the release, and tag again. After the push it is not
reversible: nuget.org allows a package to be *unlisted* but never deleted, and a version number,
once used, is used forever. A GitHub release is created only after the public restore check passes,
so an indexing timeout leaves no release to delete; re-run the same immutable publication instead.

```bash
git push --delete origin v0.1.2-preview.1
git tag -d v0.1.2-preview.1
gh release delete v0.1.2-preview.1
```

## What is signed, and what is not

| | |
|---|---|
| Release tags | Signed by the maintainer's key, verified by GitHub, enforced by the workflow |
| Packages | Build-provenance attestation (Sigstore, keyless) tying each `.nupkg` to the workflow run and commit that produced it |
| Package contents | **Not** author-signed. NuGet author signing needs a code-signing certificate, which costs money |

The provenance attestation is what an adopter should check to confirm a package came from this
repository. It is verifiable with `gh attestation verify <file> --repo ivanvyd/LakeWright.NET`.

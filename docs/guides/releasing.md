# Releasing

A release is a signed tag. Everything else — building, packing, attesting, publishing to nuget.org
and creating the GitHub release — happens in `.github/workflows/release.yml` when that tag arrives.

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

2. **Tag it, signed and annotated:**

   ```bash
   git switch main && git pull
   git tag -s v0.1.2-preview.1 -m "v0.1.2-preview.1"
   git push origin v0.1.2-preview.1
   ```

   The version needs a prerelease label ([ADR 0010](../decisions/0010-publish-prerelease-packages.md)).
   A stable tag is refused, and so is a version whose only hyphen is inside build metadata.

3. **Watch the run.** In order it verifies the signature, refuses a stable version, builds, tests,
   packs, generates a CycloneDX SBOM, attests build provenance, extracts the release notes, creates
   the GitHub release, and publishes to nuget.org last — because that is the only step that cannot
   be undone.

## If a release goes wrong

Everything up to the nuget.org push is reversible: delete the tag and the GitHub release, fix, and
tag again. After the push it is not. nuget.org allows a package to be *unlisted* but never deleted,
and a version number, once used, is used forever.

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

# ADR 0010: Publish prerelease packages to nuget.org

Status: accepted
Date: 2026-08-05

Supersedes the "Any NuGet package" non-goal in [ROADMAP.md](../../ROADMAP.md).

## Context

The v0.1 non-goal read: "Any NuGet package. Packages ship when they have independent value and a
stable surface, not because folders exist." The reasoning was sound and the position was already
half-abandoned in practice.

`release.yml` runs `dotnet pack`, attests provenance, generates a CycloneDX SBOM, and attaches the
result to the GitHub release. The v0.1.0 tag published four `.nupkg` files that way on 2026-08-01.
They are built, signed, public and downloadable. The only step never wired up was `dotnet nuget
push`.

So what the non-goal actually withheld was not the package — it was the listing. And the listing is
the half that matters: nuget.org is where .NET developers look for libraries, and there is no
substitute surface. A reader who wants to try this has to clone the repository or download a release
asset and reference it by path, which nobody does for an evaluation.

The counter-argument the non-goal was protecting against is real. A published package acquires
users, and users acquire expectations about an API that is still moving. Pre-1.0 SemVer licenses
breaking minors, but a licence in a specification is not the same as a warning a person will see.

## Decision

**Publish to nuget.org, with an explicit prerelease suffix.** `0.1.1-preview.1` rather than
`0.1.1`. `dotnet add package` will not resolve it without `--prerelease`, so acquiring one is a
deliberate act, and the version string carries the instability warning at the point of use rather
than in a document the installer never reads.

**Every packable project publishes**, including `LakeWright.AI`, which landed after the v0.1.0 tag
and has therefore never been packed by a release.

The suffix comes off when there are adopters whose code an API change would break. Until then there
is nothing to stabilise for, and a 1.0 declared without users is a promise to nobody.

## Consequences

**Package identifiers are permanent.** nuget.org allows unlisting but not deletion. Every name
published here is claimed forever, which is why the naming question is settled deliberately rather
than by whatever the project folder happens to be called.

**The prefix gets reserved as a side effect**, which is worth having: `LakeWright.*` cannot be
squatted by anyone else once the first package is up.

**Prerelease is a weaker warning than it looks.** Tooling shows prerelease versions readily and some
teams take them without reading the suffix. The label reduces accidental adoption; it does not
prevent it, and it is not a licence to break things carelessly. Breaking changes still get a
CHANGELOG entry and a migration note.

**Download counts become a signal, and a bad one.** It will be tempting to read them as demand. They
measure discovery, and a package nobody has been told about reads zero whether the idea is good or
not. The addressable-market question in [ROADMAP.md](../../ROADMAP.md) stays open, and this does not
answer it.

**No publishing credential is stored anywhere.** Amended 2026-08-06: this record first said a
`NUGET_API_KEY` secret would exist, mitigated by scope and rotation. It does not. Publishing uses
nuget.org trusted publishing — GitHub mints an OIDC token, nuget.org validates it against a policy
naming this owner, repository and workflow file, and returns a key valid for one hour. There is no
long-lived secret to leak, and rotation stops being a thing anyone has to remember.

What replaces the key as the thing to protect is the **workflow file name**, which the policy is
bound to, and the `id-token: write` permission on the release job. Anyone who can change
`release.yml` on the default branch can publish; that is the same population that could already push
a tag, so the trust boundary has not widened.

**The policy can lapse quietly.** nuget.org deactivates a newly created policy if nothing publishes
within seven days. The publish step warns rather than failing when no key comes back, so a lapsed
policy shows up as a release that succeeded and published nothing — which is why the warning names
the fix.

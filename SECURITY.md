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

The reference deployment stores no long-lived Databricks credentials.

- Application to Databricks on Azure uses a managed identity, exchanging an Entra ID token that
  Azure Databricks accepts directly as a bearer token.
- CI to Databricks uses GitHub Actions OIDC through a Databricks federation policy.
- Personal access tokens are not used in any documented path.

If you find documentation or sample code that contradicts this, treat it as a bug and report it.

## Supported versions

Pre-1.0. Only the latest release receives fixes. Once 1.0 ships, this table will list the supported
minor versions and their end-of-support dates.

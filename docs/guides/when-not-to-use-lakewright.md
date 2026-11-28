# When not to use LakeWright

LakeWright.NET is for one situation. A .NET team builds an application hosted outside Databricks.
Its users are external customers who do not have Databricks identities, and it needs its own
application logic on top of tenant-scoped data. If your situation is any other row in the table
below, a Databricks product does the job with less to own.

The quotes come from the Databricks documentation as retrieved on 2026-11-28. The pages and the
full quotes are in [the evidence record](../release-evidence/databricks-platform-2026-11-28.md).
Check the linked page before you decide: these products change quickly.

| Your situation | Use instead | Why, from the Databricks documentation |
|---|---|---|
| Every user can hold an identity in your Databricks account, for example staff, contractors or partners you can provision | [Databricks Apps](https://docs.databricks.com/aws/en/dev-tools/databricks-apps/) | Users "must be part of the same Databricks account." Apps runs Python and Node.js on Databricks serverless compute, with no hosting or authentication for you to build. |
| External customers only need to view and filter dashboards | [AI/BI embedding for external users](https://docs.databricks.com/aws/en/dashboards/share/embedding/external-embed) | It shares dashboards "with viewers outside of your organization, such as partners and customers, without provisioning Databricks accounts for those users." |
| You need a transactional Postgres database next to your lakehouse data | [Lakebase](https://docs.databricks.com/aws/en/oltp/) | "Lakebase is a fully managed Postgres database integrated into the Databricks platform." It is generally available on AWS, Azure and Google Cloud. |
| Your team works in Python, JavaScript, Go or Java | [The Databricks SDK for that language](https://docs.databricks.com/aws/en/dev-tools/sdks) | Databricks publishes and supports SDKs for those languages. LakeWright adds nothing a team in those languages needs. |
| A .NET team serves external customers who have no Databricks identities, with custom application logic | **LakeWright.NET** | Apps cannot serve them: "You can't make Databricks apps public. Anonymous access and bypassing single sign-on (SSO) are not supported." Databricks lists no .NET SDK. |

## The overlap with AI/BI embedding

LakeWright's embedding module, `LakeWright.Embedding`, uses AI/BI embedding for external users. It
does not replace it. If dashboards are all your customers need, call the documented token exchange
from any server and skip this library. LakeWright earns its place when the same application also runs
tenant-scoped queries, jobs or Genie conversations, and needs one tenant model across all of them.

## What LakeWright adds, and what it costs

Over the products above, it adds tenant resolution from your own membership database, a query layer
that will not compile string-built SQL, durable operations, an append-only audit trail and a
cross-tenant isolation test suite. Each claim links to its verification status in
[the compatibility matrix](../compatibility.md).

The cost is that you host the application, run its Postgres database and keep the library current.
Single-maintainer governance is described in [GOVERNANCE.md](../../GOVERNANCE.md).

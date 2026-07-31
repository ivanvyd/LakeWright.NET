# Lakewright.NET

The build kit for multi-tenant .NET SaaS on Databricks.

An opinionated reference architecture and a small set of reusable components for teams that sell
analytics to customers who are not themselves Databricks customers.

> **Status: planning. There is no runnable code yet.**
> This repository currently contains the research, the architecture decisions and the delivery plan.
> The first milestone is defined in [ROADMAP.md](ROADMAP.md). Watch the repo rather than cloning it.

## What problem this solves

Unity Catalog row filters resolve the caller with `session_user()`. When an ASP.NET Core backend
connects with one service principal for every tenant, that function returns the same value on every
request, so the filter predicate is identical for all of them. The result is a system that either
shows every tenant nothing or shows every tenant everything.

Databricks documents the trade-off directly, for Databricks Apps:

> All actions initiated by the app use the service principal's permissions... it doesn't support
> user-level access control. All users who interact with the app share the same permissions defined
> for the service principal, which prevents the app from enforcing fine-grained policies based on
> individual user identity.

App identity means you filter. User identity means Unity Catalog filters. There is no third option,
and there is no general on-behalf-of flow for a service hosted outside Databricks.

You find this in an audit rather than in testing, because every tenant's request returns a
plausible-looking result. Lakewright.NET handles it in the query layer, along with the pieces such a
product needs anyway: durable asynchronous operations, per-tenant cost attribution, and the
Databricks side deployed as code.

## What this is not

- Not a Databricks SDK. [`Microsoft.Azure.Databricks.Client`](https://www.nuget.org/packages/Microsoft.Azure.Databricks.Client)
  already covers Unity Catalog, Statement Execution and Jobs under MIT. This project depends on it.
- Not a dashboard embedding library. Databricks AI/BI external embedding ships today, with row-level
  security and no per-viewer fee. Use it.
- Not a generic ASP.NET Core SaaS starter. If you are not on Databricks, nothing here is for you.
- Not an admin portal, a workspace provisioner, or a notebook tutorial.

## Why the application runs outside Databricks

Databricks Apps documentation states: "You can't make Databricks apps public. Anonymous access and
bypassing single sign-on (SSO) are not supported." Every end user must exist as an identity in your
Databricks account, there are no custom domains, and the runtime ships Python and Node with no .NET.

Databricks Apps is the right host for internal tooling. It cannot host a customer-facing product.
[ADR 0001](docs/decisions/0001-host-the-application-outside-databricks.md) records the evidence and
settles it.

## Planned repository layout

Projects appear when there is code to put in them, so this grows over the milestones in
[ROADMAP.md](ROADMAP.md). What exists today:

```
src/
  Lakewright.Core/            tenancy contracts
  Lakewright.Databricks/      tenant-scoped Databricks SQL access
  Lakewright.Multitenancy/    tenant model, resolution, EF Core
tests/
  Lakewright.TenantIsolation.Tests/   the suite the rest of it rests on
docs/planning/                research, spikes and the plan
docs/decisions/               architecture decision records
```

Still to come: the ASP.NET Core integration, observability, the Declarative Automation Bundle, and
the Signalboard sample.

## Documentation

| Document | What it answers |
|---|---|
| [Product thesis](docs/planning/01-product-thesis.md) | What this is for, and the strongest argument against building it |
| [Architecture](docs/planning/03-architecture.md) | The three planes, what lives where, and why |
| [Tenant model](docs/planning/04-tenant-model.md) | The isolation decision matrix and the recommended default |
| [Decisions](docs/decisions) | One record per load-bearing choice |

## Support boundary

This is a community project maintained in personal time. It is provided for your exploration only,
it carries no service-level agreement, and it is provided as-is without warranties or guarantees of
any kind. Do not open Databricks support tickets about it.

The compatibility matrix records which Databricks features have been verified against a live
workspace and which have not. Anything not listed as verified should be treated as unverified.

## Trademarks

Databricks is a trademark of Databricks, Inc. This project is not affiliated with, endorsed by, or
sponsored by Databricks, Inc.

.NET is a trademark of Microsoft Corporation. This project is not affiliated with, endorsed by, or
sponsored by Microsoft Corporation.

## License

[Apache-2.0](LICENSE).

# Auto-development adapter: Lakewright.NET

## Project shape

Open-source .NET 10 accelerator. ASP.NET Core plus Blazor application tier, PostgreSQL for
transactional state, Databricks for analytics. Solo maintainer, public-facing eventually.

Read before changing anything structural:

- `docs/planning/03-architecture.md` — what lives where
- `docs/planning/04-tenant-model.md` — the isolation model and why
- `docs/decisions/` — one ADR per load-bearing choice, all binding

## Commands

```bash
dotnet build -c Release                                  # warnings are errors
dotnet test -c Release --filter "Category!=Live"         # default suite, no external services
dotnet test -c Release --filter "Category=TenantIsolation"
dotnet format --verify-no-changes
docker compose up -d                                     # Postgres, mock Databricks, OIDC
```

Live tests need a workspace and are opt-in:

```bash
dotnet test -c Release --filter "Category=Live"
```

Databricks assets:

```bash
cd databricks
databricks bundle validate -t dev
databricks bundle deploy -t dev
databricks bundle destroy -t dev
```

## Deploy model

Container image to Azure Container Apps. Not wired up until there is an application to deploy.
Until then, "verify" means running locally and running the test suite, not hitting a live URL.

## Rules that override default behaviour

**Never interpolate into SQL.** Statements are built through the parameterised path, including
catalog and schema identifiers. If you find yourself building SQL with string concatenation, the
design is wrong, not the rule.

**Never construct a Databricks query without a resolved tenant context.** This is the property the
project exists to provide. Any new data-reaching endpoint needs a case in the cross-tenant isolation
suite in the same change.

**Do not write an exhaustive switch over Databricks platform states.** They are documented as
open-ended. Map them at the boundary into our closed internal enum with an `Unknown` arm. This is a
deliberate exception to the usual exhaustive-switch rule, which still applies to our own types.

**Strip the `Authorization` header** when fetching presigned result links, or the Databricks token
goes to blob storage.

**No new dependency without justification in the PR.** Scope growth into a generic SaaS framework is
the project's main failure mode. See `GOVERNANCE.md`.

**Architecture or public API change requires an ADR** in the same PR, in the existing
`Status/Date/Context/Decision/Consequences` format.

## Verification standards

A claim of "done" needs output from this session. If a spike had a kill condition and the condition
fired, say so and stop; do not work around it silently.

Unverified paths are labelled unverified in the docs. The compatibility matrix records what was
tested, against what, and when.

## Environment

Databricks workspace for live verification: `lakewright-dev`, eastus2, premium SKU,
`https://adb-workspace.azuredatabricks.net`. Azure subscription
`<subscription-id>` (Visual Studio Enterprise), resource group
`rg-lakewright-dev`.

Free Edition is the contributor baseline and may lack service principals; treat that as unverified
until the week-one spike settles it.

Compute costs real credits. Warehouses get auto-stop, and live tests clean up what they create.

Never commit `.databrickscfg`, tokens, or `appsettings.Local.json`.

## Style

Match `production-databricks-patterns` and the existing docs: terse, concrete, comments that state
the failure a setting prevents rather than restating the code. No marketing register in docs. Run
`/deslop` on code and `/stop-slop` on outward-facing prose before finishing.

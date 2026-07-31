# Deploying the Databricks side

The Databricks resources are a Declarative Automation Bundle in [`databricks/`](../../databricks).
Everything below was run against a real workspace on 2026-08-01, CLI v1.10.0.

## Prerequisite: the catalog must already exist

The bundle manages what lives *inside* a catalog, not the catalog itself:

```sql
CREATE CATALOG IF NOT EXISTS lakewright_dev;
```

Two reasons, and the first one will bite you before the second.

**On a metastore with Default Storage enabled, creating a catalog through the API or through SQL
fails**, whether the bundle does it or you do:

```
Metastore storage root URL does not exist. Default Storage is enabled in your account.
You can use the UI to create a new catalog using Default Storage, or please provide a
storage location for the catalog (for example
'CREATE CATALOG myCatalog MANAGED LOCATION '<location-path>'').
```

Measured, not read: `bundle deploy` and a `CREATE CATALOG` statement both returned it. Either create
the catalog through the workspace UI, which uses Default Storage, or supply an explicit
`MANAGED LOCATION`.

**And creating a catalog needs metastore-admin rights.** A bundle that an application team deploys
on every merge should not carry that privilege. In most organisations the catalog is created once by
whoever owns the metastore, which is the same split this bundle assumes.

## Commands

```bash
cd databricks
databricks bundle validate -t dev     # parse, resolve, check against the workspace
databricks bundle deploy   -t dev
databricks bundle summary  -t dev     # what exists and where
databricks bundle destroy  -t dev     # dev only; never wire this to prod
```

Authentication is the same Entra token the application uses:

```bash
export DATABRICKS_HOST=https://adb-<id>.<n>.azuredatabricks.net
export DATABRICKS_TOKEN=$(az account get-access-token \
  --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d --query accessToken -o tsv)
```

## What the targets do

`dev` uses `mode: development`, which prefixes every resource so your copy cannot collide with
anyone else's, and pauses schedules. Observed after `deploy -t dev`:

| Declared | Deployed as |
|---|---|
| `lakewright-analytics` | `[dev contributor] lakewright-analytics` |
| `reference` | `dev_contributor_reference` |

The paused schedule is the behaviour that stops a personal copy of an hourly job running all
weekend.

`prod` uses `mode: production`, which is stricter on purpose: it rejects a user-specific root path,
so a bundle that deploys fine from your laptop can fail in CI. It runs as a named service principal
rather than whoever triggered the pipeline, and grants to groups.

Two things `validate -t prod` will tell you, both worth heeding:

- **`/Workspace/Shared` is writable by every workspace user.** A production bundle root anyone can
  write to is a production bundle anyone can replace. The bundle uses
  `/Workspace/Applications/${bundle.name}/${bundle.target}`.
- **"permissions should include the current deployment identity"** appears when a human validates
  the prod target, because the permissions list names the service principal and a group, not you.
  Expected; it does not appear when the service principal deploys.

## What is deliberately not in the bundle

**Tenant schemas.** They are created by the application when a tenant is provisioned. A bundle
describes a fixed set of resources; tenants arrive at runtime. See ADR 0002.

**The catalog.** See above.

## In CI

Fork pull requests validate the bundle against its JSON schema with no credentials, because a
workflow that hands secrets to fork code is an exfiltration path. Authenticated
`bundle validate` runs only for branches in this repository. Deployment is not wired to CI yet.

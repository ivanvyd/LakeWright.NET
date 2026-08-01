# Spike 04: managed identity to Databricks, with no stored secret

Run 2026-07-31 against `lakewright-dev`. **ADR 0006's premise holds.** The kill condition did not
fire, so the secretless claim stays in the README without qualification.

## What was proved

An Azure workload with a user-assigned managed identity authenticates to the Databricks REST API
using only an Entra token from IMDS. No Databricks personal access token, no OAuth client secret, no
federation policy, nothing in configuration to leak.

Spike 01 proved the token exchange for a *user* principal, which left open whether Databricks
accepts a non-human Entra identity the same way. It does.

## Method

1. Created a user-assigned managed identity in the workspace's resource group.
2. Registered its **client id** as a Databricks service principal through the workspace SCIM API,
   with the `workspace-access` entitlement.
3. Ran a container with that identity attached, which acquired a token for resource
   `2ff814a6-3304-4ab8-85cb-cd0e6f879c1d` from IMDS and called
   `/api/2.0/preview/scim/v2/Me`.

Result:

```
TOKEN_ACQUIRED_FROM_IMDS
{"displayName":"id-lakewright-probe", ...
 "userName":"<managed identity application id>",
 "entitlements":[{"value":"workspace-access"}]}
DATABRICKS_HTTP=200
```

Databricks resolved the caller as the managed identity, not as the human who deployed it. That is
the property the tenancy design depends on: the application runs as a principal of its own.

## The detail worth keeping

**The managed identity needs no Azure RBAC role at all.** The login initially failed with "No access
was configured for the managed identity, hence no subscriptions were found" and succeeded once it
stopped asking for subscription access. The identity holds zero Azure permissions; its only
privilege is workspace membership in Databricks.

That is a better least-privilege story than it first appears, and it is worth stating in the
deployment guide, because the instinct when this fails is to grant the identity a subscription role,
which would be granting Azure access to fix a Databricks problem.

## What this does not prove

- Only `SCIM /Me` was called. Whether this principal can run statements depends on Unity Catalog
  grants, which are a separate concern.
- The token was acquired through the Azure CLI's IMDS path. An ASP.NET Core application would use
  `DefaultAzureCredential` or `ManagedIdentityCredential`, which use the same endpoint but are not
  the same code.
- AWS and GCP remain unverified. Their documented equivalent is OAuth token federation, which is a
  different mechanism, not this one with a different cloud name.

## Reproducing

Everything created here was deleted afterwards: the container, the managed identity, and the
Databricks service principal. `rg-lakewright-dev` holds only the workspace. Recreating costs a few
minutes and roughly nothing; the container ran once and terminated.

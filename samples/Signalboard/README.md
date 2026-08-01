# Signalboard

A two-tenant sample that demonstrates what Lakewright.NET actually enforces. Two organizations,
three people, and a handful of curl commands that show a cross-tenant read failing the way it
should.

## Run it

```bash
docker compose up -d
dotnet run
```

Then open <http://localhost:8080>. The landing page prints the seeded identifiers and the exact
commands to paste.

You need Docker and the .NET 10 SDK. You do not need a Databricks workspace.

## What it demonstrates

**A cross-tenant read returns 404, not 403.** Bob is an Admin at Globex. Asking for an Acme
operation gets him a 404, because a 403 would confirm the operation exists. Membership is resolved
before authorization runs, so the request stops at tenant resolution.

**Roles are a floor inside a tenant, not the isolation boundary.** Vera is a Viewer at Acme. She
gets 403 on a write, because she is a member and the tenant resolved. Two different denials, two
different status codes, for two different reasons.

**Isolation does not depend on authentication.** This sample authenticates from a request header,
so you can claim to be anyone. You still cannot read another organization's data, because
membership comes from the database rather than from anything the caller sent. Swapping in real
OIDC changes nothing about the isolation behaviour, which is the point.

## What it is not

`DemoAuthenticationHandler` believes whatever `X-Demo-User` you send. Never ship that. It exists so
the sample runs with a single container instead of an identity provider — see the remarks on the
class for why it is an honest demonstration in spite of that.

## With a workspace

Without Databricks configured, no worker starts and operations stay `Pending`. Nothing here fakes a
run. To see one reach `Succeeded`, supply a workspace and a job:

```bash
export Databricks__WorkspaceUrl="https://adb-....azuredatabricks.net"
export Databricks__WarehouseId="..."
export Databricks__Token="$(az account get-access-token \
  --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d --query accessToken -o tsv)"
export OperationWorker__JobId="123456789"
dotnet run
```

The token here comes from the Azure CLI because a sample cannot assume a managed identity. A
deployed application holds no secret at all — see
[ADR 0006](../../docs/decisions/0006-secretless-authentication.md).

## Clean up

```bash
docker compose down -v
```

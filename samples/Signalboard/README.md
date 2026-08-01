# Signalboard

A two-tenant sample that demonstrates what LakeWright.NET actually enforces. Two organizations,
three people, a dashboard, and an API you can drive from a terminal.

## Run it

```bash
docker compose up -d
dotnet run
```

Open <http://localhost:8080> and sign in as one of the three seeded people. You need Docker and the
.NET 10 SDK. You do not need a Databricks workspace.

## What it demonstrates

**A cross-tenant read returns 404, not 403.** Bob is an Admin at Globex. Asking for an Acme
operation gets him a 404, because a 403 would confirm the operation exists. Membership is resolved
before authorization runs, so the request stops at tenant resolution.

**Roles are a floor inside a tenant, not the isolation boundary.** Vera is a Viewer at Acme. She
gets 403 on a write, because she is a member and the tenant resolved. Two different denials, two
different status codes, for two different reasons.

**Isolation does not depend on authentication.** Signing in picks a name from a list and believes
it. You still cannot read another organization's data, because membership comes from the database
rather than from the cookie. Swapping in real OIDC changes nothing about the isolation behaviour,
which is the point.

**The dashboard is held to the same rules as the API.** It renders on the server and could have
queried the tables directly. It goes through the tenant resolver and the operation store instead —
a page with its own unguarded query beside the guarded one is the bug this project exists to
demonstrate. `DashboardIsolationTests` fails if that changes.

## The same thing from a terminal

The API is the one the dashboard uses. Authenticate with a header instead of a cookie:

```bash
ACME=0198f000-0000-7000-8000-00000000ac11

curl -i -X POST http://localhost:8080/organizations/$ACME/operations \
  -H "X-Demo-User: demo|alice" \
  -H "Idempotency-Key: nightly-2026-08-01" \
  -H "Content-Type: application/json" \
  -d '{"kind":"analysis"}'
```

Send that twice and you get the same operation back, not a second Databricks run. Reuse the key with
`"kind":"export"` and you get 422. Ask for the operation as `demo|bob` and you get 404; as
`demo|vera` you can read it but not start one.

The OpenAPI document is at `/openapi/v1.json`.

## What is not real here

`DemoAuthenticationHandler` believes whatever `X-Demo-User` you send, and the sign-in page has no
password. Never ship either. They exist so the sample runs with one container instead of an identity
provider — see the remarks on the class for why it is an honest demonstration in spite of that.

## With a workspace

Without Databricks configured, no worker starts and operations stay `Pending`. Nothing here fakes a
run. To see one reach `Succeeded`, supply a workspace and a job per operation kind:

```bash
export Databricks__WorkspaceUrl="https://adb-....azuredatabricks.net"
export Databricks__WarehouseId="..."
export Databricks__Token="$(az account get-access-token \
  --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d --query accessToken -o tsv)"
export OperationWorker__Jobs__analysis="123456789"
dotnet run
```

The token comes from the Azure CLI because a sample cannot assume a managed identity, and it cannot
refresh — expect Databricks calls to start failing when it expires. A deployed application registers
`DefaultAzureCredential`, holds no secret at all, and refreshes ([ADR 0006](../../docs/decisions/0006-secretless-authentication.md)).

## Clean up

```bash
docker compose down -v
```

`down -v` matters. The schema is created on first run, so a volume left over from an older version
of the sample keeps its old columns and the app will fail against it.

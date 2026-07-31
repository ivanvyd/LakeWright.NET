# Spike 01: Statement Execution through Microsoft.Azure.Databricks.Client

Run 2026-07-31 against `lakewright-dev` (eastus2, premium), warehouse `Serverless Starter Warehouse`
(2X-Small serverless), library version 2.9.3.

**Kill condition: not triggered.** The library supports parameterised statements, `EXTERNAL_LINKS`
and `ARROW_STREAM`. We depend on it, per ADR 0004.

## What was verified

| Claim | Result |
|---|---|
| Entra ID token works as a Databricks bearer token | **Confirmed.** `az account get-access-token --resource 2ff814a6-3304-4ab8-85cb-cd0e6f879c1d` authenticated against SCIM `/Me`. No Databricks secret. This is ADR 0006's premise, tested with a user principal. |
| Typed parameters bind rather than interpolate | **Confirmed.** A parameter valued `acme'; DROP TABLE x; --` returned as a literal string. `INT` arithmetic on `:n + 1` produced 42 from 41, so the type is honoured. |
| `EXTERNAL_LINKS` + `ARROW_STREAM` | **Confirmed.** 200,000 rows, 1 chunk, 3,264,352 bytes fetched from Azure blob storage. |
| Presigned link must not carry the Databricks token | **Confirmed, and it fails loudly.** Fetching with an `Authorization` header returns **HTTP 400**. Azure blob rejects a request carrying both a SAS and an `Authorization` header, so the mistake cannot silently leak the token. |

## The finding that shapes the wrapper

**The library has a split failure model, and the dangerous half does not throw.**

| Failure | Behaviour |
|---|---|
| Bad warehouse ID | Throws `ClientApiException` with the JSON error body. |
| Statement fails (missing table, syntax error) | **Returns normally.** `Status.State = FAILED`, `Status.Error` populated, and `Manifest` and `Result` are both **null**. |

Observed:

```
Status.State : FAILED
Status.Error : BAD_REQUEST / [TABLE_OR_VIEW_NOT_FOUND] The table or view
               `definitely_not_a_table_lakewright` cannot be found.
Manifest     : <null>
Result       : <null>
```

The natural way to call this is `var r = await Execute(stmt, ct); return r.Result.DataArray;`, which
on a failed statement throws `NullReferenceException` at a line that has nothing to do with the
cause. A caller who instead null-checks `Result` returns an empty result set for a query that
failed, which is worse: an empty analytics panel looks like "no data for this tenant".

**Consequence for the design.** The query layer never returns the library's response type. It
inspects `Status.State`, translates `FAILED` into a typed failure carrying
`StatementExecutionErrorCode`, and unifies the thrown and returned failure modes into one model, so
callers cannot skip the check by forgetting to.

## Useful surface

`SqlStatement` carries `Catalog` and `Schema` as first-class properties, which is what the
schema-per-tenant model in ADR 0002 needs: the tenant's schema is set on the statement rather than
concatenated into SQL.

`StatementExecutionErrorCode` is a real enum (`BAD_REQUEST`, `TEMPORARILY_UNAVAILABLE`,
`DEADLINE_EXCEEDED`, `RESOURCE_EXHAUSTED`, `SERVICE_UNDER_MAINTENANCE`, and others), which gives the
retry classification a typed input instead of string matching.

`StatementExecutionState` is `PENDING, RUNNING, SUCCEEDED, FAILED, CANCELED, CLOSED`.

## Caveats

`SqlStatementParameter.Type` is a plain `string`, not the `SqlStatementParameterTypes` type, so the
compiler does not check it. Our wrapper exposes typed parameter construction so callers do not hand
in a free string.

Tested with a user principal. The service-principal and managed-identity variants of the same token
exchange are a separate spike and remain unverified.

`GetResultChunk` was not exercised; the test result fit in one chunk. Multi-chunk reads, and the
documented read-once behaviour, are untested here.

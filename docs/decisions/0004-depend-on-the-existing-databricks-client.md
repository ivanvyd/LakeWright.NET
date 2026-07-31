# ADR 0004: Depend on Microsoft.Azure.Databricks.Client rather than write a client

Status: accepted
Date: 2026-07-31

## Context

Databricks publishes official SDKs for Python, JavaScript, Go and Java. There is no .NET SDK and no
announced plan for one. The obvious reading is that a .NET accelerator should ship a typed client.

Research found `Microsoft.Azure.Databricks.Client` already covers most of the surface: MIT licensed,
2.2 million downloads, targeting .NET 8, 9 and 10, with 18 typed Unity Catalog clients plus
Statement Execution and Jobs.

## Decision

Depend on it. Wrap it behind our own interfaces so individual capabilities can be replaced. Build
only what is missing, which is model serving: `/api/2.0/serving-endpoints` for CRUD and
`/serving-endpoints/{name}/invocations` for scoring.

Do not fork it.

## Consequences

The largest single chunk of work in the integration layer disappears, and with it the strongest
reason to distrust the project. An accelerator whose first act is to reimplement a working MIT
library is signalling that it prefers writing code to reading it.

We inherit its gaps and work around them rather than through them: Jobs pagination is capped at 25,
`for_each_task` is unsupported, and the `environments` parameter is missing. Each is wrapped with a
comment naming the upstream issue, and each is a candidate upstream contribution.

The wrapper interfaces are ours, so if Databricks ships an official .NET SDK the dependency can be
swapped without touching calling code. That is the actual reason for the indirection, and it is the
only reason.

"The missing Databricks SDK for .NET" is removed from the project's positioning. It was the most
appealing claim available and it is false.

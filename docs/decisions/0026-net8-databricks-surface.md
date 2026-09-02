# ADR 0026: Keep Databricks registration outside the web package

Status: accepted
Date: 2026-09-02

## Context

The Databricks SDK ships a net8.0 asset, but `AddLakeWrightDatabricks` lived in the ASP.NET Core
package. A worker or stock net8 application therefore could not adopt the guarded SQL executor
without taking the web and persistence stack. The previous package consumer checked only dashboard
embedding, leaving that claimed boundary unexercised.

## Decision

`LakeWright.Databricks` multi-targets net8.0 and net10.0. Its net8.0 dependency group pins the
compatible Azure.Core line and Extensions 8 packages; net10.0 retains the current Azure SDK graph.
The public `AddLakeWrightDatabricks` extension is owned by that package. ASP.NET Core retains a
non-extension obsolete static forwarder so an already compiled caller continues to bind.

The stock net8 consumer registers its own resolver, resolves `IStatementExecutor`, and sends a
shared-schema `TenantScopedStatement` to a loopback workspace. The loopback verifies the catalog,
schema, and framework-supplied tenant parameter. It proves the package graph and the isolation
request shape without an account, secret, or billable workspace.

## Consequences

- A worker or console application can use tenant-scoped SQL on net8.0 without ASP.NET Core, EF
  Core, Npgsql, or PostgreSQL packages.
- New applications import `LakeWright.Databricks` for Databricks registration; the old static
  forwarding entry point is a migration aid, not a new dependency direction.
- The consumer floor is intentionally narrow. Live-workspace behavior remains covered by the
  opt-in live tests and is not represented as a CI claim.

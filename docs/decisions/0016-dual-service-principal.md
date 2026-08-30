# 16. Two service principals, not one

## Status

Accepted. 2026-08-30.

## Context

A workspace tenant has two distinct kinds of traffic against the Databricks
REST API:

1. **Viewer-facing** — the host application mints a per-viewer embed token
   that grants one specific user access to one specific dashboard for ten
   minutes. This token carries an `external_viewer_id` and `external_value`
   so the workspace audit log shows *who* looked at the dashboard, not
   just *which service principal* asked.
2. **Backend** — the host application lists dashboards in the workspace,
   discovers which one to embed for a tenant, and (in future) drives
   refreshes and Genie conversations on the user's behalf. None of this
   has a "viewer" identity to attach.

These two roles have fundamentally different trust requirements:

- The embed role is *the* boundary between an external viewer and a
  workspace asset. It must be the **only** role that can mint embed
  tokens, and it must not see anything the viewer is not entitled to
  see. If a token-broker compromise escalates to "list every dashboard
  in the workspace", the blast radius is every tenant the product
  serves.
- The backend role is *the* boundary between one tenant's logic and
  every tenant in the workspace. It must be able to list, but it does
  not need the ability to mint per-viewer tokens at all.

A single service principal asked to do both gives the wider role to the
narrower boundary. The cost of splitting them is one extra
`DashboardOps` section in the configuration and one extra
`AddLakeWrightDashboardOps` call at startup; the cost of *not* splitting
them is a security posture the gap analysis flagged as §3.2.

## Decision

LakeWright ships **two** Databricks service principals:

- `DashboardEmbedding` — the *embed* principal. Registered by
  `AddLakeWrightDashboardEmbedding`. Mints per-viewer tokens. Holds the
  narrow `databricks:dashboards:embed` permission only. Cannot list
  dashboards.
- `DashboardOps` — the *ops* principal. Registered by
  `AddLakeWrightDashboardOps`. Lists dashboards, and (in future) drives
  refresh and Genie. Holds the wider permissions the backend needs,
  *not* the embed permission.

The two registrations are independent `IHttpClientFactory` clients
with different `BaseAddress` and credentials. A product that only
embeds dashboards never calls `AddLakeWrightDashboardOps` and never
carries an ops secret in its configuration. A product that needs the
catalog registers both, and the two clients are wired into their own
DI services (`IDashboardTokenBroker` for the embed side,
`IOpsTokenBroker` and `IDashboardCatalog` for the ops side).

## Consequences

- The catalog and the embed broker never share a connection or a
  credential. An attacker who compromises `IOpsTokenBroker`'s options
  cannot use it to mint viewer tokens.
- Configuration validation is per-section. A half-filled `DashboardOps`
  block fails at startup (ADR 0009) rather than on the first catalog
  call.
- A host application that does not need the catalog does not pay the
  complexity tax of carrying an unused secret.
- The embed and ops paths can scale their tokens independently. The
  embed path benefits from caching (ADR 0018 — 3.1 in the gap
  analysis); the ops path currently mints a fresh token per call, with
  caching deferred to avoid a cross-PR dependency.
- A future change that adds *another* ops role (refresh, Genie)
  belongs as a third `Add…` method on the same pattern, not as a
  permission bump on the embed principal.

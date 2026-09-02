# ADR 0023: Select one Databricks credential source

Status: accepted
Date: 2026-09-02

## Context

The Databricks client previously required a consumer to register an Azure `TokenCredential`.
That is the right default for managed identity and developer sign-in, but it leaves a service
principal integration to each application and creates duplicate token-acquisition code.

## Decision

`AddLakeWrightDatabricks` accepts exactly one source of workspace authority:

- a registered Azure `TokenCredential`; or
- both `Databricks:ClientId` and `Databricks:ClientSecret`.

The registration rejects missing, partial, and ambiguous configuration at startup. Both paths
implement `IDatabricksCredential`, which keeps the client registration independent of token
acquisition. The service-principal implementation posts client credentials to the workspace token
endpoint and keeps the returned token in a process-local cache keyed by client ID. The cache
collapses concurrent requests, retries a failed exchange, and evicts each token 30 seconds before
the expiry returned by the workspace.

## Consequences

- Managed identity remains the preferred production choice where it is available.
- A service principal secret stays in application configuration or a secret store; it is never
  copied into requests, logs, or the public API surface.
- Credential rotation creates a different cache key when it changes the client ID. A process must
  still be restarted after secret-only rotation so it reads the new configuration.
- The cache is process-local. It avoids a stampede without distributing bearer tokens across hosts.

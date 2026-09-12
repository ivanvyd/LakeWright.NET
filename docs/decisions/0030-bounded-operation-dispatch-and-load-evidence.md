# ADR 0030: Bound local operation dispatch and make load evidence falsifiable

## Status

Accepted, 2026-09-12.

## Context

The operation worker used one hosted loop that awaited a Databricks run through its entire polling lifetime. A single long run could therefore stop a one-replica adopter from starting any other tenant's work. The load harness also omitted thrown requests from its error denominator and had no throughput or sample-size gate.

## Decision

Each worker process runs at most `OperationWorker:MaxConcurrentOperations` operations concurrently. The database claim remains the authority for cross-replica and per-tenant admission. Dispatch alternates a reconciliation-first attempt with a pending-first attempt so new traffic cannot indefinitely bypass old abandoned work. A process also refuses to poll the same operation twice, and renews its database claim immediately before each upstream poll. A crashed process stops renewing, so another replica can reconcile it after the grace period. This proves local deduplication; it is not fencing or an independent heartbeat. Operators must set grace longer than the bounded upstream-call duration plus the poll interval, because one call that exceeds grace can still be reclaimed by another replica.

The default is four. The controlled PostgreSQL fixture held every upstream poll: one configured slot reached one held poll, while four slots reached four, and the two-slot regression proved Beta started before held Acme completed. This establishes the dispatch bound, not production capacity. Four remains a conservative default because the existing per-tenant cap is four and the database guard still limits a single tenant across replicas.

The harness records scheduled, issued, completed, failed, dropped, censored, in-flight-at-deadline, and queued-at-deadline arrivals. A result is valid only when those categories reconcile exactly; censored work cannot pass. It gates both completion/scheduled and actual-RPS/requested-RPS, as well as endpoint sample count. The in-process profile is a regression profile, not an ingress-capacity result; network capacity requires a separately run deployment profile.

## Consequences

The default is a safe starting bound, not a measured production capacity recommendation. Operators must measure queue wait, database connections, Databricks polling, and throttles at one, four, and their chosen higher bound before increasing it. Real network measurements must state replica count, resource limits, ingress, and database configuration.

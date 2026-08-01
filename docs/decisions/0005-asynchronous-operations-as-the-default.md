# ADR 0005: Asynchronous operations as the default path

Status: accepted
Date: 2026-07-31

## Context

`wait_timeout` on the Statement Execution API accepts a maximum of 50 seconds. Anything slower is
asynchronous whether the design acknowledges it or not. Lakeflow job runs take minutes to hours.

Treating asynchrony as an escalation path leads to a synchronous happy path plus a second, less
tested asynchronous path that is reached under exactly the conditions where correctness matters most.

## Decision

Every Databricks operation that can exceed a request timeout is modelled as a durable operation
record from the start. The API returns `202 Accepted` with an operation URL.

A `BackgroundService` claims work with `SELECT ... FOR UPDATE SKIP LOCKED`, submits with an
`idempotency_token`, records the external run identifier, then polls with exponential backoff and
jitter.

Background work uses Postgres rather than Hangfire, Quartz, MassTransit or Temporal. The operation
record is a domain entity we need regardless, `SKIP LOCKED` has been in Postgres since 9.5, and a
worker that crashes releases its lock on transaction rollback. No extra infrastructure, no licensing
exposure.

## Consequences

The crash-critical window is between submitting to Databricks and recording the run identifier. A
worker that dies there orphans the operation, and a naive retry submits a second run. It is the case
the integration suite covers, because no happy-path test can see it.

Reconciliation closes it by **re-submitting the orphan with its original `idempotency_token`**.
Databricks returns the run that token already started rather than starting a second one, so one call
both discovers the lost run identifier and is safe if no run was ever created.

This decision originally said reconciliation would match orphaned operations to runs *by tag*. That
is not possible: the Jobs API does not expose the idempotency token on a run, so there is nothing to
search on. Re-submission is simpler and it deletes a method rather than adding one. Verified against
a live workspace in `LiveDatabricksTests`, because the whole design rests on Databricks actually
deduplicating, and that had only ever been proved against a fake written to behave that way.

`idempotency_token` is capped at 64 characters and has no documented deduplication window, and it
errors if the matching run was deleted. Reconciliation is therefore required, not optional.

Job run states are explicitly open-ended in the Databricks documentation. An exhaustive `switch` over
them is a future crash. Platform states map at the boundary into a closed internal enum, and the
`default` arm logs a warning and treats the run as still running, bounded by the run timeout.

Note what that does not give you: there is no distinct `Unknown` value, so "we recognise this and it
is running" and "we do not recognise this and are guessing" are the same value to a caller or a
metric. The warning log is the only signal. That is a deliberate trade for now — an extra enum case
would ripple through every consumer for an event that has not happened yet — but it is the thing to
change first if an unrecognised state ever shows up in production. This is a deliberate exception to the
usual rule of exhaustive switches with a `never` check: that rule is correct for our own discriminated
unions and wrong for a vendor's extensible enumeration.

Customers never see a raw Databricks state. Product-facing states are a closed set we control.

We write the claim loop ourselves, which is roughly a hundred lines and a schema. The alternative was
a dependency that would still need the operation record.

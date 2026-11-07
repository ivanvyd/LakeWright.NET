# ADR 0033: Serialize tenant admission and distinguish queued deletion work

Status: accepted (Unreleased; the breaking admission precondition requires the next major release)
Date: 2026-11-07
Amends: [ADR 0005](0005-asynchronous-operations-as-the-default.md),
[ADR 0030](0030-bounded-operation-dispatch-and-load-evidence.md)

## Context

An operation-row lock prevents duplicate ownership but cannot reserve a tenant's last slot.
On PostgreSQL 17, two connections can each claim one of the same tenant's two pending rows at
cap one while the first transaction is uncommitted. The second connection skips the locked
operation and its statement snapshot sees no committed in-flight work. The prior sequential
cap test and cap-100 concurrency test did not cover that case.

Deletion also strands a tenant's unclaimed queue. `BeginDeletionAsync` stops new claims, but
the old purge check treats every incomplete row as in flight. A never-submitted pending row
therefore prevents purge even after the deletion grace period.

Both cases were reproduced with the isolated PostgreSQL fixture before changing their runtime
implementations. `OperationClaimTests` holds the first claim at a command interceptor and uses
a separate caller transaction to force the pre-fix race. Its owned-transaction variant checks
the new transaction's lifetime too. `TenantLifecycleTests` asserts the post-grace queued-only
purge separately from grace-period preservation.

## Decision

Claim admission first locks an eligible organization's row with `FOR UPDATE SKIP LOCKED`,
ordered by existing in-flight count and oldest queued work. A separate atomic operation update
then counts again with a fresh ReadCommitted snapshot while holding that organization's lock.
This avoids using a stale count from the locking statement. Other tenants remain claimable.
Completion can release capacity concurrently; reconciliation of already-claimed work does
not consume a new slot. Organization lifecycle writes contend with the same row lock.

Without a caller transaction, the method opens and commits its own ReadCommitted transaction.
An existing EF transaction is accepted only at ReadCommitted or ReadUncommitted, which PostgreSQL
implements with ReadCommitted semantics. The PostgreSQL/Npgsql test verifies the latter is
reported as ReadUncommitted and preserves the cap, uncommitted visibility, and rollback behavior.
The method never commits, rolls back, or changes the isolation of a caller-owned transaction.
The caller must commit a returned claim before submitting remote work.

This is an observable compatibility change: caller transactions with transaction-wide snapshots
(RepeatableRead or Serializable) are now rejected before admission. Invoke claims outside that
transaction, or use ReadCommitted. Ambient/enlisted transactions are not an alternative supported
admission path; use the explicit EF transaction seam or call outside them.

The new admission precondition changes previously accepted caller behavior and must ship in the
next major release. It is not eligible for a 2.x minor or patch release. The source VersionPrefix
is unchanged in this batch; release preparation must select the major version and retain the
migration guidance above. SDK API validation remains enabled against stable 2.1.2 even across a
major-version change. Any intentional API exception needs a reviewed narrow suppression or a
new verified baseline; a version change alone does not turn validation off.

`BeginDeletionAsync` continues to preserve all queued work. After the caller's grace period,
purge may discard an incomplete row only when its state is Pending, `ClaimedAt` is null, and
`ExternalId` is null. Those rows disappear through the existing organization cascade after a
successful schema drop. Every other incomplete row blocks the drop. A claimed Pending row with
no external ID is uncertain remote work, not proof that submission never happened.

## Consequences

The PostgreSQL regression suite covers the forced cap-one overlap, transaction ownership and
rollback, cancellation of owned admission, fair scheduling, duplicate ownership, cross-tenant
progress, inactive refusal, queued grace preservation, conservative incomplete-state retention,
schema-drop failure and retry, and other-tenant preservation. It establishes fixture behavior,
not deployment throughput or live Databricks cancellation.

Only the existing worker submission contract makes an unclaimed Pending row safe to discard:
submission must follow a committed claim and must promptly record its external identifier.
Callers that bypass that contract must reconcile their external work before purging.

An inactive tenant's orphan is still not automatically resubmitted or reconciled by the worker.
A live worker may finish polling its existing run. If the worker stopped, operators must inspect
the remote run and record a safely observed terminal outcome before purge, or deliberately
restore service and resume reconciliation. An external ID alone does not prove termination;
neither a missing ID nor an accepted cancellation request proves that remote work stopped.
This batch does not add remote cancellation or an operator orchestration service.

The schema drop remains the irreversible boundary outside the local delete transaction. A failure
before a successful drop preserves the organization and queue for retry. A crash after a successful
drop can still require an idempotent retry to finish database bookkeeping.

These changes are Unreleased source behavior. The package quickstart remains pinned to published
2.1.2 and makes no claim to exercise these worker or lifecycle fixes.

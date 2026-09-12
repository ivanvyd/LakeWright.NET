# ADR 0032: Persist accepted Genie ownership before polling under one response deadline

Status: accepted

Date: 2026-09-12

## Context

Genie accepts a conversation and message identifier before the answer is ready. Previously,
`AskAsync` polled to completion and recorded ownership only afterward. A polling failure, local
timeout, or caller cancellation after acceptance left the real conversation unrecorded, so its
rightful owner could neither list nor resume it. Retrying could create another upstream
conversation.

The old response timeout also began after creation and did not cover credential acquisition, POST,
response-body reads, or a terminal answer that arrived after the local deadline.

## Decision

`AskAsync` treats a valid `conversation_id` and `message_id` response as acceptance. It records the
owner immediately, before polling and before observing a caller cancellation that arrives at that
boundary. An ownership-store failure is surfaced as `ConversationOwnershipPersistenceException`
with the accepted conversation and message IDs. They allow a trusted host to reconcile persistence;
they do not bypass ownership checks. The library does not report this failure as a successful answer.

`ResponseTimeout` is a positive, total deadline over credential acquisition, creation or
continuation POST, response-body reads, poll requests, and poll backoff. The deadline uses the
configured `TimeProvider`; all waits are capped by the remaining duration. Before acceptance, a
local deadline raises `GenieResponseTimeoutException` because no identity was received. Remote
acceptance is unknown in that case: the workspace may have created a conversation whose response
was not received in time. Retrying creation may create another conversation. After
acceptance, it returns `GenieOutcome.TimedOut` with the known conversation and message identifiers.
Caller cancellation remains cancellation, not a timeout. Stopping local waiting does not claim to
cancel remote Genie work.

## Consequences

- Conversations whose accepted identifiers were received and successfully recorded remain
  discoverable and resumable after a poll failure, local timeout, or caller cancellation.
- The default in-memory ownership store and the explicitly acknowledged shared-space mode retain
  their existing behavior. Multi-replica hosts still need the Redis ownership adapter.
- The ownership write deliberately completes once acceptance is known, even if the caller cancels
  at that moment. It has its own bounded `ResponseTimeout` deadline so an unavailable store
  produces an explicit failure rather than an invisible accepted id or an unbounded request; a host
  needing stronger storage-duration guarantees supplies a durable ownership store.

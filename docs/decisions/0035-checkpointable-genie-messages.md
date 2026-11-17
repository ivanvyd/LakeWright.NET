# 0035. Separate accepted Genie submission from authorized reads

Status: accepted

Date: 2026-11-17

## Context

`AskAsync` and `ContinueAsync` return accepted IDs after polling. The latter always submits another
question. Ownership persistence from ADR 0032 does not let a durable caller checkpoint a successful
message acceptance or resume that message without another POST. A fake-HTTP fixture confirms that
continuing after a poll failure makes a second submission.

## Decision

Expose `StartConversationAsync`, `SendMessageAsync`, `GetMessageAsync` and `WaitForResponseAsync`
on `IGenieConversations`. Submission returns a `GenieAcceptedMessage` before polling. Starting
persists ownership first. Reading returns a `GenieMessageSnapshot` with the platform status and
terminal flag. Waiting uses only GET requests. All existing owner checks, tenant mappings and
feature gates apply before credentials or HTTP. Conflicting returned identity fields are rejected.

Default interface implementations throw `NotSupportedException` so existing custom adapters remain
compatible. The existing typed HTTP client registration and convenience-method total deadlines are
preserved. Each separate operation gets one response deadline. No automatic POST retry is added.

Library-owned HTTP client factories disable default request logging. A packaged .NET 8 fixture
reproduced credential and signed-query disclosure with the supported HTTP dependency floor in all
nine other credential-bearing factories after Genie was corrected. The existing export client
already disabled those loggers. The fixture captures formatted messages, structured values and
scopes. Client names, handlers and host configuration callbacks remain unchanged. Hosts own custom
loggers, telemetry and the upstream SDK transport.
Earlier custom loggers for the same named clients are removed too. Hosts must configure any
deliberate replacement after library registration.

## Consequences

- Applications can checkpoint message IDs and resume after a failure or restart with durable ownership.
- Applications own persistence, retention, authorization inputs and recovery from ambiguous acceptance.
- Cancellation stops local work. Known ownership persistence remains independently bounded.
- Metadata does not imply SQL result materialization. This package still returns answer text and generated SQL.
- Claim-based SQL filtering is an available platform control, but it does not authorize conversation history.
  LakeWright keeps its existing data model and token extension points instead of adding an identity framework.

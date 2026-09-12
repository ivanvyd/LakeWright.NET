# Complete statement results and local deadlines

Date: 2026-09-12

Status: Accepted

## Context

The Statement Execution API returns an initial result chunk, not necessarily every row.
The previous session mapping discarded continuation, truncation, and link-expiry metadata.
The exporter also expected an object envelope where external JSON_ARRAY blobs contain an array
of row arrays. Local fixtures reproduced partial inline success and a failed external download.
The configured polling budget did not cover submission or a slow terminal response.

The [Statement Execution API reference](https://docs.databricks.com/api/statement-execution/v1/statement-execution)
defines chunk indices, row offsets/counts, truncation, and expiring external links. This decision
supersedes the payload, expiry, memory, and polling statements in ADR 0016. Local fixtures follow
the current published wire contract; live workspace verification remains opt-in and was not run.

## Decision

Keep the existing public outcome constructors and the existing executor/export interfaces.
Add external chunk descriptors and total chunk count to LargeResult. Its legacy Links collection
is the initial link page; it never promises every link. The descriptor's string representation
excludes its credential-bearing link.

The public executor and billing reader complete inline chunk chains before returning rows.
Chunk indices and row offsets must be consecutive, and the accumulated row and chunk counts
must match the manifest. Reject server truncation with RESULT_TRUNCATED and IsTruncated=true;
reject missing or contradictory metadata with RESULT_INCOMPLETE. A 25 MiB accounting limit for
retained inline rows includes conservative container/cell overhead and string characters.
Exceeding it returns RESULT_LIMIT_EXCEEDED and directs the caller to streaming export. This
accounting bound is not a measured peak-process-memory claim: the SDK also holds a response chunk.

Exports consume the root JSON array incrementally and validate string/null cells against the
column schema. They resolve subsequent pages by statement ID and numeric chunk index, never by
following or logging the opaque credential-bearing continuation URL. A link-only continuation
means the next consecutive index. Both result paths stop after at most 100,000 chunks.

Export downloads use a separate HTTP client without workspace authorization. The registered
client disables default URI loggers. A supplied client with default Authorization is rejected;
hosts must keep their custom handlers and diagnostics credential-free as well. Transport/status
diagnostics exclude blob URLs and upstream response bodies. Expired links are renewed before
download; a 401/403 before reading any rows permits one renewal/retry. Renewed metadata must
describe the same chunk. Failures after rows have been emitted are never retried from the start.

The JSON reader permits at most 1 MiB of new input between yielded rows, with bounded serializer
read-ahead. A large individual row can fail this limit; a large collection of small rows streams.
Malformed payloads and count mismatches fail enumeration. Consumers must stage artifacts and
only mark an export complete after the sequence ends successfully.

TotalBudget includes initial submission, credential acquisition through the SDK, polling, and
result reads. Export time starts on the first enumeration and includes pauses by the consumer.
GetAsync uses the configured statement budget for its own read/completion call. The deadline token
reaches every I/O and wait; dependencies must honor cancellation. A terminal response received at
or after the deadline is rejected. Caller cancellation stays OperationCanceledException; local
expiry becomes StatementBudgetExceededException. Its existing StatementId property is empty when
creation did not return an ID, and otherwise preserves the accepted ID.

Local deadline expiration does not attempt or imply remote cancellation. An accepted statement
can continue consuming compute. Hosts choose cancellation/recovery policy using the known ID;
an unknown creation outcome requires reconciliation rather than a claim that nothing ran.

## Consequences

Old constructor calls and binaries remain supported; callers can encounter explicit failures for
results that previously appeared successful but were incomplete. Adoption must handle those
failures. No new package dependency is required on either net8.0 or net10.0.

Contract tests exercise actual SDK serialization with a fake workspace and storage service,
three-chunk results, truncation, malformed metadata, expiry renewal, and cancellation. Fake-clock
tests cover submission, poll, and consumer-delay deadlines. A live external JSON test and measured
memory/latency profiles are separate acceptance evidence; unit tests do not establish either.

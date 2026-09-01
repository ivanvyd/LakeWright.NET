# Chunked tenant-scoped export

Date: 2026-08-30

Status: Accepted

## Context

The `IStatementExecutor` surface (ADR 0004) returns the whole result of a statement in
one `StatementOutcome` value. That is the right shape for an interactive query that
returns a few hundred rows: a single round-trip, a single materialised array, and the
caller can pattern-match on `Success` / `Pending` / `Failure` / `LargeResult` without
holding any state.

It is the wrong shape for a background export that walks ten thousand rows, or any
result bigger than the warehouse's `INLINE` cap (25 MiB). An exporter that calls
`IStatementExecutor` and then iterates `StatementOutcome.Success.Rows` has buffered the
whole result in memory before the first row is written to disk. The `LargeResult`
variant in turn returns a `Uri[]` of presigned chunk links, but the executor's caller
is expected to fetch them itself with the same auth, and the executor offers no streaming
helper. The pattern that fills the gap today is "every adopter re-implements chunked
HTTP fetch over the same presigned URLs", with the SAS-vs-Authorization-header gotcha
rediscovered each time.

The gap analysis flagged this in §3.5: an export surface that walks the warehouse's
`EXTERNAL_LINKS` disposition and yields rows one at a time, with the cancellation,
auth, and chunk-URL shape handled once.

## Decision

Add `ITenantScopedExport.StreamAsync(TenantScopedStatement, CancellationToken) →
IAsyncEnumerable<ExportRow>` to `LakeWright.Databricks`. The default implementation
is `DatabricksTenantScopedExport`. The stream yields a single header row first (a row
whose `Column` is non-null) and then yields one `ExportRow` per data row, in the
warehouse's order, with the cell values string-typed.

The export asks the warehouse for `SqlStatementDisposition.EXTERNAL_LINKS` regardless
of the configured default, and uses `StatementFormat.JSON_ARRAY` for the chunk
envelope. JSON_ARRAY is chosen over `ARROW_STREAM` so the chunk-fetch side does not
require an Apache Arrow dependency: the warehouse's JSON_ARRAY shape is the same
`{ "data_array": [[...]] }` envelope as its INLINE response, parseable with
`System.Text.Json`. The warehouse still bins the result into chunks itself, so the
exporter's memory profile is bounded by one chunk at a time.

The chunk fetch uses a plain `HttpClient` (no `Authorization` header). The presigned
SAS URLs do not accept that header: Azure blob SAS signs the request, and Azure
rejects requests that carry both a SAS and an `Authorization` header with HTTP 400.
The exporter's `HttpClient` is registered as a typed client via
`AddHttpClient<ITenantScopedExport, DatabricksTenantScopedExport>()` so a future adopter
who needs a custom handler chain (retries, telemetry, custom proxy) has the standard
extension point.

Cancellation is honored at every yield, including inside the chunk fetch itself, so a
cancelled export stops without consuming the next chunk. Errors surface as
`HttpRequestException` with a status code: a 401/403 from the warehouse is a
`REQUEST_REJECTED`, a chunk-fetch 5xx is the same exception type carrying the chunk's
status. A still-running statement (`PENDING` / `RUNNING` after `WaitTimeout`) is a
programming error, not a transient failure — the executor's `Get` path covers that
case, and the export is not a polling surface.

## Consequences

- An adopter with a 10,000-row result that was previously forced to either (a)
  re-implement chunked HTTP fetch or (b) ship the result in one blob via the executor's
  `LargeResult` now has a single call that streams.
- The streaming surface does not do column projection or row filtering: which columns
  and which filters belong in the caller. The library's job is to deliver every row,
  in order, without buffering the world.
- The export's HttpClient is a typed client. An adopter that already calls
  `AddHttpClient("name", ...)` for other reasons can configure the typed client
  through the same `IHttpClientBuilder` they would use for any other typed client.
- The export asks for `EXTERNAL_LINKS` even when the host's default is `INLINE`. The
  trade-off is a chunk round-trip even for small results, in exchange for a uniform
  memory profile. A future adopter who wants a "small result inline, large result
  chunked" optimisation belongs in `IStatementExecutor`, not in the streaming export.
- The "I cannot test the chunk fetch path with the real Databricks client without a
  workspace" gap remains. The unit tests stub the workspace and chunk servers with
  WireMock; `LiveDatabricksTests` (which need a real workspace) cover the request
  shape end-to-end. This split matches every other Databricks client in the library.

## Alternatives considered

- **Reuse the executor's `LargeResult` and add `StreamAsync` there.** That would
  conflate "I have a statement, run it" with "stream its result", and would require
  the executor to be both synchronous-returning and async-streaming on different
  code paths. The current split — executor returns outcomes, export streams rows —
  keeps each surface small.
- **Use `ARROW_STREAM` for chunks.** That would have required the `Apache.Arrow` NuGet
  package, which is a multi-megabyte dependency for a single consumer. The JSON shape
  is the same envelope as the warehouse's INLINE response, so JSON parsing keeps the
  library honest about what it actually depends on.
- **Let the caller fetch the chunks themselves.** This is the gap the export closes.
  The "you implement it" answer means the SAS-vs-Authorization gotcha, the chunk
  ordering invariant, and the cancellation contract all leak into every adopter.

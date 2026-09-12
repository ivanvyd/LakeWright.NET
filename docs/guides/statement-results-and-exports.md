# Statement results and exports

Use `IStatementExecutor` for bounded interactive results and `ITenantScopedExport` for streamed
JSON rows. Both require a resolved tenant context and parameterized statements.

`StatementOutcome.Success.Rows` contains the complete inline result. The reader fetches additional
chunks and checks their indices, offsets, and row counts. Server truncation returns a Failure with
`ErrorCode = "RESULT_TRUNCATED"` and `IsTruncated = true`; missing or inconsistent data returns
`RESULT_INCOMPLETE`. `RESULT_LIMIT_EXCEEDED` means local inline materialization exceeded its
25 MiB accounting budget, including estimated row/cell overhead. Use export or narrow the query.

`LargeResult.Links` and `LargeResult.Chunks` contain the initial external-link page. The descriptors
include expiration and continuation. For complete JSON exports, use the exporter instead of
iterating Links yourself. External URLs contain temporary credentials: do not log them or send
workspace Authorization to storage. Keep custom HTTP handlers and telemetry free of signed URLs.

Exports request external JSON_ARRAY blobs, emit one header, then stream rows with string/null
cells. They resolve the remaining pages and renew expired links before download. A 401/403 can
trigger one renewal before body consumption. A failed later chunk throws; rows already written
are partial. Write to a temporary artifact and publish or mark it complete only after successful
enumeration. Breaking out of the loop is cancellation by the consumer, not completion evidence.

Both result paths allow at most 100,000 chunks. Export row parsing caps new input between emitted
rows at 1 MiB, with serializer read-ahead, to bound an oversized or malformed row. A single very
large cell can therefore require a different export format. Large numbers of ordinary rows do
not accumulate in the library. Peak process memory and throughput still need workload-specific
measurement; the SDK retains metadata and the current response chunk.

`StatementOptions.TotalBudget` covers submission, polling, and result reads. For an export it
starts when enumeration begins and includes consumer pauses. Set it for the expected download
and destination speed, not only warehouse execution time. `WaitTimeout` accepts `0s` or whole
seconds from `5s` through `50s`; it is the server wait on submission, not the local total budget.

A local timeout raises `StatementBudgetExceededException`. Its StatementId is available when the
workspace supplied one; an empty value means creation acceptance is unknown. Caller cancellation
remains OperationCanceledException. Neither condition proves remote execution stopped. The host
must choose whether to cancel/reconcile the statement; the library does not resubmit it.

Low-level `IStatementExecutor.GetAsync` and `CancelAsync` do not establish ownership of a
statement ID. A host must load that ID from an operation authorized for the resolved tenant;
do not expose those methods directly with a request-supplied statement ID. Chunk continuation
inside the executor/exporter stays within the initiating call. `RawDataExportService` exposes
an opaque operation and checks both tenant and owner before starting a streamed export.

The local tests use the [documented Databricks wire contract](https://docs.databricks.com/api/statement-execution/v1/statement-execution).
They do not claim a live workspace export passed. See [ADR 0029](../decisions/0029-complete-statement-results-and-deadlines.md)
for compatibility, limits, and deadline decisions.

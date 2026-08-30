# 17. A publish gate for `__aibi_external_value`

## Status

Accepted. 2026-08-30.

## Context

A dashboard that uses the `__aibi_external_value` claim pattern only
keeps tenants apart if the column actually flows from the claim through
a SQL filter that constrains the dataset. A board that mentions the
column inside a string literal — `WHERE col = '__aibi_external_value'`
— passes a substring search but ships unscoped, and any tenant that
opens it sees every row.

The gap analysis (gap §3.4) records a real instance of exactly this
bypass in production, in `private-project.HasTenantScopedDatasets`. A gate that
"looks for the marker in the dataset SQL" is a tempting, plausible
implementation that is silently wrong. The fix is structural: do not
treat the contents of a string literal as code.

## Decision

`LakeWright.Embedding.DashboardPublishGate` is a small, dependency-free
static class that exposes two methods:

- `Inspect(string? datasetSql)` — runs against one dataset. Returns a
  `PublishGateVerdict` with `Passed`, a `Reason` on failure, and a list
  of byte offsets at which the marker was found as a real SQL token.
- `InspectAll(IReadOnlyList<string> datasetSqls)` — runs against every
  dataset on a board. Fails closed on the first dataset that does not
  reference the marker; otherwise returns the aggregated hits.

The implementation is a single-pass byte scanner that tracks three
string states:

- Single-quoted SQL string literal, with the standard `''` doubled-quote
  escape.
- `--` line comment, running to the next newline.
- `/* ... */` block comment, non-nesting (the SQL standard).

The scanner also recognises backtick-quoted identifiers and refuses to
match the marker inside them. A reference to the marker is recorded
only when:

- the surrounding bytes are not identifier characters (`x__aibi_external_value`
  does not match), and
- the comparison is case-insensitive, and
- the match is **not** inside a string literal, line comment, block
  comment, or backtick identifier.

## What this closes and what it does not

This closes the string-literal bypass that bit private-project, and the comment
forms of the same trick. It does not, and is not intended to, defeat a
board that reconstructs the marker by concatenation (`'__aibi_' ||
'external_value'`). Such a board is genuinely unscoped; the gate
correctly refuses it. Closing that case is the warehouse's
`parsed_query` job, not this one's. The contract with callers is
explicit: this is a defense-in-depth check, not a proof of
correctness.

Empty, whitespace, or `null` input fails closed. A board with no
datasets fails closed. The gate is pure (no I/O, no clock), so it is
trivial to call from a publish pipeline, a unit test, or a CI hook.

## Consequences

- A library consumer that runs every candidate dashboard through the
  gate before publishing ships the same safety net the gap analysis
  called for, in a tested form that does not contain the bypass.
- The gate's output is a structured verdict (offsets, reason), so a
  CI integration can log the exact byte offset of each reference for
  audit and review.
- The gate adds zero runtime dependencies. It lives next to the embed
  broker because the same workspace trust boundary owns both.

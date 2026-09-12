# ADR 0025: Marker lint for `__aibi_external_value`

## Status

Superseded in part by [ADR 0028](0028-revision-bound-dashboard-isolation-evidence.md). 2026-09-12.

The tokenizer remains accepted as a marker lint. Its earlier implication that marker presence could
prove tenant filtering is superseded.

## Context

A dashboard that uses the `__aibi_external_value` claim pattern only
keeps tenants apart if the column actually flows from the claim through
a SQL filter that constrains the dataset. A board that mentions the
column inside a string literal — `WHERE col = '__aibi_external_value'`
— passes a substring search but ships unscoped, and any tenant that
opens it sees every row.

Security testing reproduced exactly this string-literal bypass. A gate that
"looks for the marker in the dataset SQL" is a tempting, plausible
implementation that is silently wrong. The fix is structural: do not
treat the contents of a string literal as code.

## Decision

`LakeWright.Embedding.DashboardMarkerLint` is a small, dependency-free
static class that exposes three methods:

- `Inspect(string? datasetSql)` — runs against one dataset. Returns a
  `PublishGateVerdict` with `Passed`, a `Reason` on failure, and a list
  of byte offsets at which the marker was found as a real SQL token.
- `InspectAll(IReadOnlyList<string> datasetSqls)` — runs against every
  dataset on a board. Fails closed on the first dataset that does not
  reference the marker; otherwise returns the aggregated hits.
- `InspectDashboard(string serializedDashboard)` — parses a Lakeview
  dashboard definition, reads each dataset's `queryLines` or `query`, and
  returns the named per-dataset verdicts. Invalid JSON, no datasets, and
  empty SQL fail closed.

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

This closes the reproduced string-literal bypass and the comment forms of the same trick for a
marker-presence lint. It does not, and is not intended to, defeat a
board that reconstructs the marker by concatenation (`'__aibi_' ||
'external_value'`). Such a board is genuinely unscoped; the gate
correctly refuses it. Closing that case is the warehouse's
`parsed_query` job, not this one's. The contract with callers is explicit: this is linting, not a
proof of row ownership, tenant filtering, or publish safety. A source-owned revision-bound verifier
is required for those properties; see ADR 0028.

Empty, whitespace, or `null` input fails closed. A board with no
datasets fails closed. The gate is pure (no I/O, no clock), so it is
trivial to call from a publish pipeline, a unit test, or a CI hook.

## Consequences

- A library consumer that runs every candidate dashboard through the lint gets useful author
  feedback, but must not use the result to approve publishing or token minting.
- The gate's output is a structured verdict (offsets, reason), so a
  CI integration can log the exact byte offset of each reference for
  audit and review.
- The gate adds zero runtime dependencies. It lives next to the embed
  broker because the same workspace trust boundary owns both.

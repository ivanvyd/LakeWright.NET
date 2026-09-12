# ADR 0031: Explicit exact numeric fields

Status: Accepted

Date: 2026-09-12

## Context

RawDataKind.Number uses DOUBLE. An integer such as 9007199254740993 cannot be represented exactly
by that type. Treating every existing Number field as decimal would change its contract.

## Decision

Keep Number as finite, approximate DOUBLE. Add WholeNumber for signed 64-bit BIGINT and FixedPoint for
fixed-point input with source-owned precision and scale. Append enum values to preserve existing
numeric identities. FixedPoint fields require explicit precision 1-38 and scale 0-precision; propagate
that metadata to result columns through additive properties.

Parse exact decimal digits without converting through double or System.Decimal, whose range is
smaller than Databricks DECIMAL. Accept an optional sign and decimal point with invariant digits;
reject grouping, exponent notation, overflows, and values requiring rounding. Trailing fractional
zeroes do not require rounding. Parameter types derive only from validated source metadata; values
remain bound parameters. Reject nonfinite Number filters rather than sending NaN/Infinity.

## Consequences

Adopters choose exact kinds for exact columns. Existing Number semantics remain approximate;
nonfinite filters now fail validation before any warehouse call. Public constructor signatures and
existing enum values remain unchanged. Local tests prove parameter bytes and rejection behavior;
they do not establish live warehouse execution. Databricks documents precision and scale in its
[DECIMAL type contract](https://docs.databricks.com/aws/en/sql/language-manual/data-types/decimal-type).

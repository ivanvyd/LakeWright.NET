# Raw-data numeric fields

The source definition chooses numeric meaning. Requests only provide bound values.

| Kind | SQL parameter | Input contract |
|---|---|---|
| Number | DOUBLE | Finite, approximate floating point; invariant exponent notation is accepted |
| WholeNumber | BIGINT | Exact signed 64-bit integer, including values above 2^53 |
| FixedPoint | DECIMAL(p,s) | Exact decimal digits with source-owned precision 1-38 and scale 0-p |

Configure an exact amount column explicitly:

```csharp
new RawDataField
{
    Name = "amount",
    Column = "amount",
    DisplayName = "Amount",
    Kind = RawDataKind.FixedPoint,
    Precision = 18,
    Scale = 2,
    Filterable = true,
    Sortable = true,
}
```

`9007199254740993` remains exact as WholeNumber; Number is approximate and can change that value.
FixedPoint does not convert through double or System.Decimal, so all 38 supported digits are
available. Equal, In, and range filters use the same parser. Decimal grouping and exponent notation
are rejected; `123.4500` is valid at scale 2 because dropping trailing zeroes changes no value.
`123.451` is rejected at scale 2 rather than rounded. NaN and infinities are rejected for all numeric
filter kinds. Returned RawDataColumn metadata includes Precision and Scale for FixedPoint fields.

These contracts are covered by local parameter tests. Live warehouse behavior for the new kinds
has not been verified. See [ADR 0031](../decisions/0031-exact-raw-data-numerics.md).

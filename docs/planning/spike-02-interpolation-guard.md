# Spike 02: making interpolated SQL a compile error

Run 2026-07-31. This records a design that was wrong, the evidence that it was wrong, and the one
that works. The wrong version is kept because it passed its own test.

## The requirement

`TenantScopedStatement.Create(ctx, sql, params)` must accept a constant statement and reject an
interpolated string, at compile time rather than at review time.

## Attempt 1: a `FormattableString` overload. Does not work.

The theory was that an interpolated string literal binds to a `FormattableString` overload in
preference to a `string` one, and that EF Core separates `FromSqlRaw` from `FromSqlInterpolated`
this way.

Both halves are wrong. C# overload resolution prefers `string` for an interpolated string literal,
because `string` is its natural type. And EF Core separates those two methods **by name**, not by
overload.

Measured, with a probe file calling `Create(ctx, $"... WHERE id = '{userSuppliedId}'")` present in
the tree:

```
=== build WITH interpolated SQL in the tree ===

Build succeeded.
```

**The guard compiled and did nothing.**

Worse, the test covering it passed. It asserted that an overload taking `FormattableString` existed
and carried `Obsolete(error: true)`, which was true, and irrelevant. A test written from the same
misunderstanding as the code confirms the misunderstanding.

## Attempt 2: an interpolated string handler. Works.

A parameter whose type carries `[InterpolatedStringHandler]` **is** preferred over `string` when the
argument is an interpolated string literal. So the blocked overload takes
`BlockedSqlInterpolation`, and is marked `Obsolete(error: true)`.

Same probe, after the change:

```
error CS0619: 'TenantScopedStatement.Create(TenantContext, BlockedSqlInterpolation,
params StatementParameter[])' is obsolete: 'Interpolating into SQL is an injection
risk. Pass a constant statement and supply values as StatementParameter arguments.'
```

A constant string still binds to the `string` overload and compiles, so the ergonomics are
unchanged for correct callers.

The handler's constructor also throws, which covers reflection and `dynamic` callers that never
meet the compiler check.

## What the test suite can and cannot do

A test running inside the built assembly cannot observe a compile error in that assembly. The suite
therefore asserts the *mechanism*: that the blocked parameter's type carries
`[InterpolatedStringHandler]`, and that the overload is `Obsolete` with `IsError`. Delete either and
the test fails.

The compile failure itself is evidenced above. Reproduce it by adding a call with an interpolated
string and building.

## The lesson worth keeping

The first design's test passed. The guard was inert. What caught it was compiling a deliberate
violation and watching the build succeed when it should have failed.

Any control asserted only by a test written alongside it is unverified. The isolation suite claims
to prevent cross-tenant reads, so each control in it needs a demonstration that it fails when the
control is removed, not only that it passes when the control is present.

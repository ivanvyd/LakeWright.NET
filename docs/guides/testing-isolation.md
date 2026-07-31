# Testing tenant isolation

The isolation suite is the only reason to trust anything else in this repository. A suite that
passes proves nothing on its own: it has to be shown failing when the control it guards is removed.

## Running it

```bash
dotnet test --filter "Category=TenantIsolation"
```

Needs Docker. It starts one Postgres container for the assembly and a fresh database per test, so
a test cannot pass by reading another test's rows.

It is a separate required check in CI rather than part of the general test job, so a filter that
accidentally excludes it fails the build instead of going quiet.

## What it covers

Structural, asserted by reflection because the shapes must not exist at all:

- Every `TenantScopedStatement.Create` overload takes a `TenantContext` first.
- `IStatementExecutor.ExecuteAsync` accepts nothing that could carry SQL, a catalog or a schema
  from the caller.
- The interpolation guard is an `[InterpolatedStringHandler]` and is `Obsolete(error: true)`.
- Hostile schema names are rejected.

Behavioural, against real Postgres:

- A member resolves to their own schema.
- Naming another tenant resolves to nothing.
- An unknown tenant and one you cannot reach are indistinguishable.
- A suspended organization refuses its own members.
- Two organizations cannot share a schema.
- Audit events cannot be edited or deleted.

## The proof

Run 2026-07-31. The membership predicate was removed from `EfTenantContextResolver`, leaving:

```csharp
.Where(m => m.OrganizationId == tenantId
         && m.Organization!.State == OrganizationState.Active)
```

This is the realistic mistake: the query still filters by organization and still checks state, so
it looks careful. It just no longer checks who is asking.

Result:

```
[FAIL] CrossTenantResolutionTests.An_unknown_tenant_is_indistinguishable_from_one_you_cannot_reach
[FAIL] CrossTenantResolutionTests.Naming_another_tenant_resolves_to_nothing
Failed!  - Failed: 2, Passed: 11, Total: 13
```

Restored:

```
Passed!  - Failed: 0, Passed: 13, Total: 13
```

Two tests failed and they were the two that should have. The other eleven passing is the useful
part: it means the suite localises the break rather than going uniformly red.

## A test can pass for the wrong reason

Breaking the control is not enough on its own; you have to break the *specific* thing the test
claims to guard.

The concurrency test for the operation claim loop asserts that ten workers never claim the same
row. Deleting `FOR UPDATE SKIP LOCKED` left it green, because the single-statement update is atomic
either way and a competing worker simply blocks and re-evaluates. The test was real, and the
comment describing what made it pass was wrong.

Replacing the claim with a select-then-update pair — the realistic mistake — did turn it red. So
the test earns its place, and the code comment now says that `SKIP LOCKED` buys throughput rather
than correctness.

Four controls in this repository have now shipped with a test that passed while the control did
nothing or did something other than advertised. Assume yours is the fifth until you have watched
it fail.

## Adding an endpoint

Any change touching tenant resolution, the query layer or authentication adds a case here in the
same pull request. When you add one, break the thing it guards and watch it fail before you trust
it. `spike-02-interpolation-guard.md` documents a control whose test passed while the control did
nothing, which is what this practice exists to catch.

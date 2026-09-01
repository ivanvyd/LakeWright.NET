# LakeWright.NET 1.1.0 whole-application regression checklist

## Target

- Branch: release-preparation branch based on the final merged `main`
- Local sample: `http://localhost:8080`
- Live platform: the authenticated non-production Databricks profile already configured for this repository
- Release candidate version: `1.1.0`

## Safety and cleanup

- [ ] Confirm the worktree is clean before testing and record the exact commit.
- [ ] Use a unique Docker Compose project and temporary artifact/cache directories.
- [ ] Do not grant Databricks permissions, deploy bundles, start jobs, or change workspace resources.
- [ ] Stop only processes and containers started by this run; remove its Compose volumes.
- [ ] Record blocked live checks as `SKIPPED`, never as `PASS` or product `FAIL`.

## Repository gates

- [ ] Locked restore succeeds for `LakeWright.slnx` and the load harness.
- [ ] Release build succeeds for the solution and load harness with zero warnings/errors.
- [ ] `dotnet format` reports no changes.
- [ ] Documentation links and repository claims pass.
- [ ] Vulnerability scans report no moderate-or-higher direct or transitive advisories for the solution, load harness, or maintenance tools.
- [ ] Full non-live tests pass with TRX and XPlat coverage artifacts.
- [ ] The independently gated `TenantIsolation` category passes.

## Audit partition lifecycle

- [ ] All audit-partition PostgreSQL integration tests pass.
- [ ] The suite proves populated migration, exact copy validation, finalize, rollback, cleanup/remigration, UTC/DST month boundaries, retention, bounded locks and history, ACL restoration, and forced-RLS hidden-row preservation.
- [ ] Lifecycle validation refuses missing/corrupted state, registry indexes/primary key, disabled or replica-only trigger modes, and a non-`SECURITY DEFINER` identity function.
- [ ] Maintenance CLI help exposes `migrate`, `validate`, `finalize`, `rollback`, and `maintain` without executing DDL.

## Billing attribution

- [ ] Billing, cost endpoint, shared statement-session, and tenant-ownership tests pass.
- [ ] The suite proves fixed SQL and bound parameters, report/price-window proration, corrections, mixed currencies, 500-run cap, polling deadline/cancellation, redacted upstream errors, and tenant filtering before the account-wide query.
- [ ] If the non-production workspace identity can read `system.billing.usage` and `system.billing.list_prices`, run the documented read-only live billing check; otherwise record the exact permission blocker as `SKIPPED`.

## Package regression

- [ ] Pack all seven public libraries as `1.1.0`; confirm seven `.nupkg` and seven `.snupkg` files.
- [ ] Inspect target frameworks: Core and Embedding contain `net8.0` and `net10.0`; the other five contain `net10.0`.
- [ ] Restore and run an isolated .NET 8 consumer against only the local package directory plus nuget.org.
- [ ] Consumer verifies `DashboardPublishGate.Inspect` and `InspectAll` dataset indices and can resolve the 1.1.0 public package graph.

## Load and Docker sample

- [ ] Run the load harness at 50 RPS for 30 seconds; record latency, errors, and peak PostgreSQL pool use.
- [ ] Build and start Signalboard with Docker Compose; PostgreSQL becomes healthy and the app remains running.
- [ ] `/health` and `/openapi/v1.json` return 200.
- [ ] Replaying the same idempotency key returns 202 with the same operation ID.
- [ ] Reusing that key with a different operation kind returns 422.
- [ ] A cross-tenant read returns 404 and a Viewer write returns 403.

## Browser regression

- [ ] Run the existing Playwright smoke against the Compose sample and retain screenshots.
- [ ] Anonymous home/sign-in pages render with three seeded people and no console errors.
- [ ] Alice can start an operation interactively without navigation.
- [ ] Vera sees Acme data but has a disabled Start control with an explanation.
- [ ] Bob sees Globex and no Acme data.
- [ ] Dark mode and the 390px viewport render without page-level horizontal overflow.
- [ ] Run the UI/UX frontend-change gate and record `RUN` or `SKIP` with its exact reason.

## Databricks and CI

- [ ] Confirm the installed Databricks CLI version is the pinned supported version.
- [ ] Run authenticated `bundle validate` and non-mutating `bundle plan` against the existing development profile/catalog.
- [ ] Confirm all required PR checks pass before merge.
- [ ] After merge, confirm `main` CI, security, load, docs, bundle, and tenant-isolation checks pass.

## Publication

- [ ] Land version/changelog/evidence through a pull request without assigning reviewers.
- [ ] Create and locally verify a signed annotated `v1.1.0` tag on the verified `main` commit.
- [ ] Push the tag and monitor the Release workflow through package publication.
- [ ] Confirm the GitHub Release contains seven packages and the CycloneDX SBOM.
- [ ] Download each `.nupkg`, verify provenance attestation and SHA-256 consistency, and confirm all seven public NuGet V3 registrations expose 1.1.0.
- [ ] Restore and run the consumer again from an empty package cache using only nuget.org.
- [ ] Confirm no feature or Dependabot pull request remains open, or document any intentional exception.

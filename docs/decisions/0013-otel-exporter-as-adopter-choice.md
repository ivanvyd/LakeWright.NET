# ADR 0013: Observability export is the adopter's choice, with a reference wiring in the sample

Status: accepted
Date: 2026-08-29

## Context

The library publishes four `System.Diagnostics` instruments under `LakeWright.Multitenancy`: two counters, one histogram, and a denied-resolution counter. The `LakeWrightTelemetry` class is explicit that none of them carry a tenant identifier, that the choice of exporter is the adopter's, and that the library takes no OpenTelemetry dependency. The compatibility matrix records the gap: "no observability *export*. `LakeWrightTelemetry` publishes four instruments and `TelemetryTests` asserts each one, but nothing here exports them."

That has been true since the four instruments landed. A subscriber can wire their own exporter with two lines, the getting-started guide says so, and a public search confirms the shape. What was missing was a working reference: a sample that shows the wiring concretely rather than describing it, and the matrix row that says "verified" rather than "documents the gap."

## Decision

**The library continues to take no OpenTelemetry dependency.** The docstring on `LakeWrightTelemetry` is the rule, and adding a package to a layer whose whole point is to stay out of the exporter-selection business would be the change that proves the rule.

**The sample (`samples/Signalboard`) ships a reference wiring, opt-in via configuration.** It depends on `OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol`, both pinned in `Directory.Packages.props` under a new `Observability (sample only)` section. `Program.cs` reads a `Lakewright:OpenTelemetry:Enabled` flag and an `OtlpEndpoint` value; when set, it registers an OTel pipeline that subscribes to the existing meter and activity source. When unset, the sample runs as it did before, and the new packages are pulled in by the sample project rather than the library.

**The cost endpoint and the OTel wiring both flow through `Program.cs` rather than the library.** Same reasoning: a product with a different observability stack should not have to fight the library to get the wires they want.

**`docs/guides/getting-started.md` keeps the two-line snippet that already shows how to subscribe.** The reference in the sample is what makes the snippet concrete; the docstring is what the maintainer reads next time someone reaches for a tag.

## Consequences

**A subscriber takes the existing `Lakewright:OpenTelemetry:Enabled` flag as a hint, not a constraint.** The pattern in the sample is one of many valid OTLP wirings; an adopter with a collector already configured in their platform reads the same flags and supplies the right endpoint. The library continues not to know about it.

**`OpenTelemetry.Extensions.Hosting` and `OpenTelemetry.Exporter.OpenTelemetryProtocol` are now transitive dependencies of the sample, not the library.** A contributor who only ships the library has no new packages to review. A contributor running the sample has two more, and a CI failure on a vulnerable version of either is a sample failure rather than a library failure.

**The compatibility matrix gains one row:** "observability export via the sample's opt-in OTel pipeline" is now `Verified` (against a local OTLP collector, not a vendor) and the previous "no observability *export*" line is removed.

**The cardinality-bomb rule is now test-enforced.** `TelemetryTenantGuardTests` walks the library's source and asserts no metric call site tags with `tenant`, `tenantid`, `tenant_id`, `organizationid`, or `organization_id`. A future change that does so fails the build with the offending line. The rule was already in the docstring; it is now also a gate.

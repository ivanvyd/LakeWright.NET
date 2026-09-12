# Local verification tooling

`LakeWright.Tooling` packages the small checks that are useful before a dashboard, a tenancy
policy, or a package candidate is promoted. Dashboard inspection does not mutate the workspace.
The consumer check executes a bundled fixture against local synthetic HTTP endpoints, without
Databricks credentials or real warehouse calls.

Install a prerelease explicitly from the same feed as the candidate being assessed:

```bash
dotnet tool install --global LakeWright.Tooling --prerelease --add-source <package-source>
```

## Lint dashboard markers

Pass a serialized `.lvdash.json` file to inspect every dataset SQL definition with the same
marker linter used by the library:

```bash
lakewright inspect-dashboard dashboard.lvdash.json
```

The command returns JSON. Exit code `0` means every dataset contains an executable tenant marker;
`1` means marker lint failed; `2` means invalid input or configuration. Marker presence does not
prove tenant filtering and does not authorize publication or token minting. Strict embedding needs
the source-owned, revision-bound evidence described in [ADR 0028](../decisions/0028-revision-bound-dashboard-isolation-evidence.md).
To inspect a live dashboard by id,
provide `DATABRICKS_HOST` and `DATABRICKS_TOKEN` through the environment. The token is used only
for that request and is never written to output.

## Check the net8 consumer floor

After packing a candidate into a local package source, verify its narrow net8 dependency floor:

```bash
lakewright verify-floor ./artifacts 2.0.0-rc.1
```

The command requires exact-version Embedding and Databricks nupkgs plus their LakeWright dependency
closure in that directory. Empty, incomplete, duplicate, and wrong-version sources fail before
restore. It snapshots these candidates, uses an isolated cache, and maps `LakeWright.*` exclusively
to that snapshot. External dependencies resolve from nuget.org. Resolved package versions and SHA-512
hashes must match the supplied bytes; a public or previously cached package cannot stand in for a
missing candidate.

It builds and runs the bundled net8 consumer with the lowest supported Microsoft.Extensions pins,
including synthetic tenant resolution, SQL, and token-broker flows, and rejects persistence
dependencies. Successful output identifies each candidate and hash. This is separate from checking
already published packages on nuget.org and from the full tenant-isolation suite. Package API
validation compares every library under `src/` with stable 2.0.0 during pack; Tooling is a CLI and
uses these packaged command checks instead of a public-library API baseline.

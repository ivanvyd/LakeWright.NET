# Local verification tooling

`LakeWright.Tooling` packages the small checks that are useful before a dashboard, a tenancy
policy, or a package candidate is promoted. It is a local guardrail: it neither creates workspace
objects nor executes a statement.

Install a prerelease explicitly from the same feed as the candidate being assessed:

```bash
dotnet tool install --global LakeWright.Tooling --prerelease --add-source <package-source>
```

## Inspect a dashboard before publish

Pass a serialized `.lvdash.json` file to inspect every dataset SQL definition with the same
publish gate used by the library:

```bash
lakewright inspect-dashboard dashboard.lvdash.json
```

The command returns JSON. Exit code `0` means every dataset passed; `1` means the dashboard is
not publishable; `2` means invalid input or configuration. To inspect a live dashboard by id,
provide `DATABRICKS_HOST` and `DATABRICKS_TOKEN` through the environment. The token is used only
for that request and is never written to output.

## Check the net8 consumer floor

After packing a candidate into a local package source, verify its narrow net8 dependency floor:

```bash
lakewright verify-floor ./artifacts 2.0.0-rc.1
```

The command restores and compiles a clean net8 consumer against `LakeWright.Embedding` and
`LakeWright.Databricks`, then rejects an assets graph that contains EF Core, Npgsql, or PostgreSQL.
It checks compatibility and the persistence boundary; run the repository's consumer-floor and
tenant-isolation tests as part of a full release gate.

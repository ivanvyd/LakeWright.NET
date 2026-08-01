# 06 �?" Deploying Databricks assets as code, and the CI/CD story

Research date: **2026-07-31**. All findings below are VERIFIED against live primary sources on this date unless
explicitly labelled RECALLED or UNVERIFIED. Databricks docs pages carry a "Last updated" date; I record it per page
so we can tell how fresh each claim is.

Cloud note: Databricks docs are split per cloud (`/aws/en/`, `/gcp/en/`, and Microsoft Learn for Azure). The bundle
content is the same across all three. I cite the AWS path as canonical and the Learn path where the AWS page 404'd.

---

## 1. NAMING �?" the headline finding

### VERIFIED: the current official name is **Declarative Automation Bundles**

The source prompt's name is **correct**. "Databricks Asset Bundles" (DABs) is the *historical* name and is now
only used parenthetically in the docs.

**Exact current docs heading:**

> # What are Declarative Automation Bundles?

�?" <https://docs.databricks.com/aws/en/dev-tools/bundles/> (page last updated **Jul 10, 2026**)

The same page carries the standing gloss:

> Declarative Automation Bundles (formerly known as Databricks Asset Bundles)

### The rename event

Release notes entry, quoted verbatim:

> ## Databricks Asset Bundles is now Declarative Automation Bundles
>
> **March 16, 2026**
>
> Databricks Asset Bundles has been renamed to Declarative Automation Bundles. See [Why was Databricks Asset
> Bundles renamed to Declarative Automation Bundles?](../../dev-tools/bundles/faqs#rename).

�?" <https://learn.microsoft.com/en-us/azure/databricks/release-notes/dev-tools/bundles>
(also at <https://docs.databricks.com/aws/en/release-notes/dev-tools/bundles>)

**Correction to a bad intermediate reading (recorded so nobody re-derives it):** a first summarising pass over the
release-notes page attributed the rename to "Databricks CLI 0.218.0". That is wrong. The March 16, 2026 rename
entry cites **no CLI version at all** �?" it is a documentation/branding change, not a CLI feature. CLI **0.218.0** is
a different, much older entry: *"Declarative Automation Bundles is generally available �?" April 23, 2024"*. Do not
put "0.218.0" next to the rename in our docs.

### Why they renamed, and whether anything breaks

From the FAQ (<https://docs.databricks.com/aws/en/dev-tools/bundles/faqs#rename>):

> The new name Declarative Automation Bundles more accurately reflects the usage and capabilities of bundles. In
> addition, the term _assets_ caused some confusion as it has more than one meaning in Databricks.

And critically:

> This name change is non-breaking. The `bundle` CLI command and all of your existing configuration does not need
> to be modified.

### Guidance for Lakewright.NET docs

- Use **Declarative Automation Bundles** as the product name throughout.
- The CLI verb is still `databricks bundle �?�` and the file is still `databricks.yml`. Nothing in code changes.
- On first mention, write "Declarative Automation Bundles (formerly Databricks Asset Bundles)". The community and
  virtually all blog content, Stack Overflow answers, and conference talks still say "DABs" �?" one parenthetical
  buys us searchability without looking dated.
- The docs do **not** state that the "DAB" acronym is deprecated; they simply stop using it. I'd avoid it in prose
  but keep it in a glossary entry.
- **Do not** confuse this with **Lakeflow**, which is a separate 2025 product umbrella (Lakeflow Connect, Spark
  Declarative Pipelines �?" formerly DLT, and Lakeflow Jobs �?" formerly Workflows). Lakeflow is *what you deploy*;
  bundles are *how you deploy it*.

---

## 2. Bundle mechanics �?" `databricks.yml`

Primary source: <https://docs.databricks.com/aws/en/dev-tools/bundles/reference> (last updated **Jul 24, 2026**)
and <https://docs.databricks.com/aws/en/dev-tools/bundles/settings> (last updated **Mar 16, 2026**).

A bundle must contain exactly one `databricks.yml` at the project root. It may pull in other files via `include`.

### 2.1 Top-level mappings (VERIFIED, complete list)

| Key | Purpose |
|---|---|
| `bundle` | Bundle metadata. Subkeys: `name` (required), `uuid`, `cluster_id`, `databricks_cli_version`, `deployment`, `engine`, `git` |
| `variables` | Custom variables: `description`, `default`, `type`, `lookup` |
| `workspace` | Workspace connection + path layout (see below) |
| `artifacts` | Build settings: `type`, `path`, `build`, `dynamic_version`, `files` |
| `include` | Path globs pulling in further config files |
| `resources` | The Databricks objects to deploy |
| `sync` | File sync rules: `include`, `exclude`, `paths` |
| `targets` | Deployment environments |
| `presets` | Deployment-wide defaults (see 5.2) |
| `permissions` | `user_name` / `group_name` / `service_principal_name` + `level` |
| `run_as` | `user_name` XOR `service_principal_name` |
| `scripts` | Named executable commands (`content`), runnable via `bundle run` |
| `python` | Python-defined config: `mutators`, `resources`, `venv_path` |
| `experimental` | Preview flags: `immutable_folder`, `python`, `python_wheel_wrapper`, `record_deployment_history`, `scripts`, `skip_artifact_cleanup`, `skip_name_prefix_for_schema`, `use_legacy_run_as` |

`workspace` subkeys (VERIFIED): `account_id`, `artifact_path`, `auth_type`, `azure_client_id`, `azure_environment`,
`azure_login_app_id`, `azure_tenant_id`, `azure_use_msi`, `azure_workspace_resource_id`, `client_id`, `file_path`,
`google_service_account`, `host`, `profile`, `resource_path`, `root_path`, `state_path`, `workspace_id`.

`targets.<name>` subkeys (VERIFIED): `artifacts`, `bundle`, `cluster_id`, `compute_id`, `default`, `git`, `mode`,
`permissions`, `presets`, `resources`, `run_as`, `sync`, `variables`, `workspace`.

### 2.2 Resource types supported today

Source: <https://docs.databricks.com/aws/en/dev-tools/bundles/resources> (last updated **Jul 27, 2026**).
Full current list (VERIFIED, 30 types):

```
alert                    external_location        postgres_role            secret_scope
app                      genie_spaces             postgres_synced_table    sql_warehouse
catalog                  job                      quality_monitor          synced_database_table
cluster                  model                    registered_model         vector_search_endpoint
dashboard                model_serving_endpoint   schema                   vector_search_index
database_catalog         pipeline                 volume
database_instance        postgres_branch
experiment               postgres_catalog
                         postgres_database
                         postgres_endpoint
                         postgres_project
```

Answering the specific questions in the brief:

- **jobs** �?" yes (`job`).
- **pipelines** �?" yes (`pipeline`, i.e. Spark Declarative Pipelines on Lakeflow).
- **model serving endpoints** �?" yes (`model_serving_endpoint`). Note the `run_as` incompatibility in §5.3.
- **schemas / volumes** �?" yes (`schema`, `volume`), since CLI 0.225.0 and 0.236.0 respectively.
- **apps** �?" yes (`app`), since CLI 0.239.0 (Jan 2025).
- **dashboards** �?" yes (`dashboard`), AI/BI dashboards, since CLI 0.232.0.
- **SQL warehouses** �?" yes (`sql_warehouse`).
- **catalogs** �?" **yes** (`catalog`) �?" but see the engine caveat in §2.3: UC catalogs and external locations are
  supported **only on the direct deployment engine**, not the legacy Terraform engine.
- **grants** �?" **not** a standalone top-level resource type. UNVERIFIED whether grants can be expressed nested
  inside `catalog` / `schema` / `volume` resource bodies; the resource docs say each resource's keys mirror the
  corresponding REST API create-request fields, which *suggests* nested grants where the API supports them, but I
  did not confirm this against a concrete example. **Flag for the implementation spike.** If grants are not
  expressible, that is a genuine argument for a thin Terraform layer alongside bundles (see §7).

Also notable for us: **Lakebase / Postgres** has first-class bundle support (`database_instance`,
`database_catalog`, `postgres_project`, `postgres_branch`, `postgres_endpoint`, `postgres_role`,
`postgres_database`, `postgres_catalog`, `synced_database_table`, `postgres_synced_table`). Cross-reference R05.

### 2.3 IMPORTANT: the deployment engine changed under our feet

Source: <https://docs.databricks.com/aws/en/dev-tools/bundles/direct> (last updated **Jul 27, 2026**).

Release note, **June 10, 2026**, CLI **1.3.0**:

> The direct deployment engine is now generally available (GA). New bundles created using Databricks CLI version
> 1.3.0 and above use the direct deployment engine by default instead of the Terraform deployment engine.

This is the single most consequential recent change for our accelerator:

- Bundles **used to** shell out to the Terraform provider under the hood. As of CLI 1.3.0 they use the **Databricks
  Go SDK** directly. New bundles default to `direct`.
- Opt in/out via `bundle.engine: direct` in `databricks.yml`, or the `DATABRICKS_BUNDLE_ENGINE` env var. The config
  file wins if both are set.
- `databricks bundle deployment migrate` (**Public Preview**) converts an existing `terraform.tfstate` to the direct
  engine's `resources.json`.
- Claimed benefits: "up to 40% faster" deploys, granular validation with detailed diffs, replayable plans, simpler
  setup (no Terraform binary download, so fewer corporate-firewall problems).
- **Semantic difference that will bite people** �?" quoted behaviour: on the Terraform engine, *removing a field from
  your config leaves the deployed value unchanged*; on the direct engine, *removing a field reverts it to the
  resource's default*. The direct engine is the more correct declarative semantic, but it means a config cleanup
  can silently change deployed behaviour during migration.
- Direct-engine-only resources: **Unity Catalog catalogs, external locations, Genie spaces, vector/AI search
  endpoints.**

**Recommendation for Lakewright.NET:** pin `bundle.engine: direct` explicitly rather than relying on the default, and
set `bundle.databricks_cli_version: ">=1.3.0"` (or higher) so a contributor on an old CLI fails fast instead of
deploying with different semantics. The no-Terraform-binary property is also a real win for locked-down enterprise
networks, which is our target audience.

---

## 3. CLI

### 3.1 Version (VERIFIED 2026-07-31)

Latest release: **v1.10.0**, released **29 Jul 2026** �?" <https://github.com/databricks/cli/releases>

Recent cadence is roughly weekly: v1.10.0 (29 Jul), v1.9.0 (22 Jul), v1.8.0 (15 Jul), v1.7.0 (09 Jul),
v1.6.0 (02 Jul), v1.5.0 (24 Jun).

The install docs state a **minimum supported version of 0.205.0**, but that floor is meaningless for us �?" we need
�?�1.3.0 for the direct engine.

### 3.2 Install methods

Source: <https://docs.databricks.com/aws/en/dev-tools/cli/install> (last updated **Jun 24, 2026**)

| Platform | Command |
|---|---|
| Windows (WinGet) | `winget search databricks` then `winget install Databricks.DatabricksCLI` |
| Windows (Chocolatey) | `choco install databricks-cli` |
| Windows (WSL) | use the Linux curl method inside WSL |
| macOS (Homebrew) | `brew tap databricks/tap && brew trust databricks/tap && brew install databricks` |
| Linux / macOS (curl) | `curl -fsSL https://raw.githubusercontent.com/databricks/setup-cli/main/install.sh \| sh` |
| Any | manual `.zip` extraction from the GitHub release |

For a .NET accelerator targeting Windows developers, **WinGet is the right documented default**, with the curl
script for WSL/CI and Homebrew for macOS contributors.

### 3.3 `databricks bundle` subcommands

Source: <https://docs.databricks.com/aws/en/dev-tools/cli/bundle-commands> (last updated **Jul 23, 2026**)

| Command | Description (quoted) | Notable flags |
|---|---|---|
| `validate` | "Validate bundle configuration files are syntactically correct" | �?" |
| `plan` | "Show the deployment plan for the current bundle configuration" | `--cluster-id`, `--force`, `--select` |
| `deploy` | Deploy the bundle to the remote workspace | `--auto-approve`, `--cluster-id`, `--fail-on-active-runs`, `--force`, `--force-lock`, `--plan`, `--select` |
| `run` | Execute a job, pipeline, or script from the bundle | `--no-wait`, `--restart`, `--only`, `--params`, `--validate-only` |
| `destroy` | "Delete jobs, pipelines, other resources, and artifacts that were previously deployed" | `--auto-approve`, `--force-lock` |
| `summary` | Output bundle identity and resources with deep links | `--force-pull` |
| `deployment` | `bind`, `unbind`, `migrate` | �?" |
| `generate` | Generate config from existing workspace resources | `--key`, `--bind`, `--force` |
| `init` | "Initialize a new bundle using a bundle template" | `--branch`, `--config-file`, `--output-dir`, `--tag`, `--template-dir` |
| `open` | Navigate to a bundle resource in the workspace | `--force-pull` |
| `schema` | "Display JSON Schema for the bundle configuration" | �?" |
| `sync` | Sync file changes to the remote workspace | `--dry-run`, `--full`, `--interval`, `--output`, `--watch` |

**Does a plan/dry-run exist today? YES.** `databricks bundle plan` is a documented, first-class command (shipped
alongside the direct engine work). `databricks bundle deploy --plan` also exists. As of CLI 1.2.0 (June 4, 2026)
both `plan` and `deploy` take `--select` to target individual resources �?" but the release note explicitly warns
**"This option is not intended for use in production."**

Stability: everything in the table above is documented as generally available except **`bundle deployment migrate`**,
which is marked **Public Preview**.

`databricks bundle schema` is worth wiring into our repo: it emits the JSON Schema, which gives contributors
IntelliSense/validation for `databricks.yml` in VS Code and Rider.

---

## 4. CI/CD

### 4.1 The official position

<https://docs.databricks.com/aws/en/dev-tools/ci-cd/> (last updated **Jul 10, 2026**):

> Declarative Automation Bundles are the recommended approach to CI/CD on Databricks.

### 4.2 Recommended pipeline shape

<https://docs.databricks.com/aws/en/dev-tools/ci-cd/flows> (last updated **Jul 10, 2026**) gives four stages:

1. **Compile and test** �?" "Triggered on a pull request or a commit to the main branch. Compile code and run unit
   tests. Output a versioned file, for example, `my-app-1.0.jar`."
2. **Upload artifacts** �?" store built files in Unity Catalog volumes or an external repo, keyed by Git commit hash
   or semver.
3. **Validate bundle** �?" "Run `databricks bundle validate` to ensure that the `databricks.yml` configuration is
   correct."
4. **Deploy bundle** �?" "Use `databricks bundle deploy` to deploy the bundle to a staging or production environment."

### 4.3 Is there an official GitHub Action? YES

**`databricks/setup-cli`** �?" <https://github.com/databricks/setup-cli> �?" described in the Databricks docs as
"A composite action that sets up the Databricks CLI in a GitHub Actions workflow."

It accepts a `version` input; omitting it installs the latest.

```yaml
- uses: databricks/setup-cli@main
  with:
    version: 1.10.0
```

**Supply-chain note for an OSS accelerator:** `@main` is a moving target on a third-party action. Our own CI should
pin to a tag (e.g. `databricks/setup-cli@v1.10.0`) or, stricter, to a commit SHA, and pin the `version:` input so
builds are reproducible. The Databricks docs examples use `@main`; that is convenience, not a security
recommendation, and we should say so in our docs rather than copying it blindly.

### 4.4 Auth in CI �?" both options, and which to prefer

Databricks' GitHub Actions page (<https://docs.databricks.com/aws/en/dev-tools/ci-cd/github>, last updated
**Jun 11, 2026**) shows **both**:

**(a) OAuth M2M service principal secret** �?" used in the page's bundle-deployment examples, passing a token via the
`DATABRICKS_TOKEN` env var from a GitHub secret. Underlying mechanism documented at
<https://docs.databricks.com/aws/en/dev-tools/auth/oauth-m2m> (last updated **Jul 22, 2026**):

- Env vars: `DATABRICKS_HOST`, `DATABRICKS_CLIENT_ID`, `DATABRICKS_CLIENT_SECRET`
  (plus `DATABRICKS_ACCOUNT_ID` and an accounts-console host for account-level ops).
- Secrets are created under Settings �?' Identity and access �?' Service principals �?' Secrets �?' Generate secret.
- **Max 5 secrets per service principal; max lifetime 730 days.** So this option carries a mandatory rotation
  obligation �?" at minimum a 2-year hard expiry, and realistically much shorter.
- Note: this page does **not** contain a statement recommending federation over secrets. The preference below is
  my engineering judgement plus the existence of the federation feature, not a quoted Databricks directive.

**(b) GitHub OIDC �?' Databricks workload identity federation (no stored secret)** �?" this is the better option and it
is fully documented: <https://docs.databricks.com/aws/en/dev-tools/auth/provider-github> (last updated
**Apr 21, 2026**).

Setup is two steps: create a federation policy on the service principal, then set env vars in the workflow.

Federation policy fields:
- **Issuer:** `https://token.actions.githubusercontent.com`
- **Subject:** built from the GitHub Actions job context
- **Audiences:** defaults to your Databricks account ID if omitted
- **Entity type:** "Branch" (default) or **"Environment" (recommended)**
- **Subject claim:** `sub` by default; use `job_workflow_ref` for reusable workflows

Verified working example workflow, quoted from the docs:

```yaml
name: GitHub Actions Demo
run-name: ${{ github.actor }} is testing out GitHub Actions
on: workflow_dispatch
permissions:
  id-token: write
  contents: read
jobs:
  my_script_using_wif:
    runs-on: ubuntu-latest
    environment: prod
    env:
      DATABRICKS_AUTH_TYPE: github-oidc
      DATABRICKS_HOST: https://my-workspace.cloud.databricks.com/
      DATABRICKS_CLIENT_ID: a1b2c3d4-ee42-1eet-1337-f00b44r
    steps:
      - name: Checkout repository
        uses: actions/checkout@v4
      - name: Install Databricks CLI
        uses: databricks/setup-cli@main
      - name: Run Databricks CLI commands
        run: databricks current-user me
```

Key gotcha, quoted: **"Set `permissions: id-token: write` on the calling workflow, not the reusable workflow."**
GitHub only mints the required claim when the permission is on the caller.

Note that `DATABRICKS_CLIENT_ID` and `DATABRICKS_HOST` are **not secrets** �?" they are a service principal UUID and
a workspace URL. They belong in GitHub *variables*, not *secrets*. That means an OIDC-based pipeline can have
literally zero repository secrets, which is a genuinely strong story for an OSS accelerator.

**Recommendation: OIDC federation as the documented default; OAuth M2M client secret as the fallback** for users on
platforms without OIDC (self-hosted runners in odd configurations, some Azure DevOps setups).

### 4.5 Running CI safely from untrusted forks

This is a real problem for an open-source accelerator and Databricks' docs do **not** address it. The constraint
comes from GitHub, quoted from
<https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows>:

> With the exception of `GITHUB_TOKEN`, secrets are not passed to the runner when a workflow is triggered from a
> forked repository. The `GITHUB_TOKEN` has read-only permissions in pull requests from forked repositories.

And on the tempting workaround:

> Running untrusted code on the `pull_request_target` trigger may lead to security vulnerabilities. These
> vulnerabilities include cache poisoning and granting unintended access to write privileges or secrets.

OIDC is subject to the same restriction �?" `id-token: write` is not granted to fork PRs, so an OIDC-authenticated
deploy job simply cannot run from a fork. That is the desired behaviour.

**Recommended job split for Lakewright.NET:**

| Job | Trigger | Auth | Runs on fork PRs? |
|---|---|---|---|
| `build-and-unit-test` | `pull_request` | none | yes |
| `bundle-validate` | `pull_request` | none *(see caveat)* | yes |
| `bundle-plan` | `pull_request` from same-repo branches only | OIDC | no |
| `deploy-staging` | `push` to `main` | OIDC | n/a |
| `deploy-prod` | `push` to `main`, gated on a GitHub **Environment** with required reviewers | OIDC | n/a |

Gate the privileged jobs with an explicit condition, matching the pattern Databricks' own docs use:

```yaml
if: github.event_name == 'push' && github.ref == 'refs/heads/main'
```

and for the plan job, `if: github.event.pull_request.head.repo.full_name == github.repository`.

Use **GitHub Environments** for `staging` and `prod` �?" this pairs with the Databricks federation policy's
recommended entity type ("Environment"), so the trust relationship is scoped to a reviewed environment rather than
to any branch. Do **not** reach for `pull_request_target`.

**CAVEAT �?" UNVERIFIED, needs a spike:** I have assumed `databricks bundle validate` runs without workspace
credentials. That is true for pure syntax/schema checking, but a bundle using **`lookup` variables** (which resolve
object IDs by querying the workspace) will need auth to validate, and `targets.*.workspace.host` resolution may
also require a reachable workspace. **Action: test `bundle validate --target dev` with no credentials in a clean
container before we promise a credential-free fork-PR validation job.** If it turns out validate needs auth, the
fallback is to run `bundle schema` + a JSON-Schema lint on fork PRs and keep full validate behind the trusted gate.

---

## 5. Environment separation

### 5.1 Recommended topology

<https://docs.databricks.com/aws/en/dev-tools/ci-cd/flows>: **"Maintain separate workspaces for development,
staging, and production."** Environment differences are handled by parameterising config, not hardcoding values.

Databricks CI/CD best practices additionally recommend branching strategies aligned to those environments, and
service principals for production deploys.

### 5.2 Development mode �?" documented behaviour

Source: <https://docs.databricks.com/aws/en/dev-tools/bundles/deployment-modes> (last updated **Jul 24, 2026**)

`mode: development` applies these automatically:

- **Name prefixing:** resources are prefixed `[dev ${workspace.current_user.short_name}]`.
- **Tagging:** jobs and pipelines get a `dev` tag.
- **Pipelines** are marked `development: true`.
- **Schedules and triggers** on jobs and quality monitors are **paused** by default.
- **Concurrent runs** are enabled on all deployed jobs (faster iteration).
- **Deployment lock is disabled**, since conflicts are unlikely in a personal dev deployment.
- `--cluster-id <id>` may override cluster definitions at deploy time.

The name prefix is what delivers **per-developer isolation**: every developer deploying the same bundle to the same
dev workspace gets their own uniquely-named copy of every resource, keyed on their short name. Combined with a
per-user `workspace.root_path` (the default layout is
`/Workspace/Users/${workspace.current_user.userName}/.bundle/${bundle.name}/${bundle.target}`), developers do not
collide. This is the documented answer to "per-developer isolated dev deployments" �?" we should not invent our own
prefixing scheme.

### 5.3 Production mode �?" validations

`mode: production` enforces:

- Pipelines must have `development: false`.
- The current Git branch must match the target's configured branch (override with `--force`).
- If **not** using a service principal for `run_as`, it validates that `artifact_path`, `file_path`, `root_path`,
  and `state_path` are not overridden to a specific user, and that `run_as` and `permissions` are both specified.
- No cluster override is permitted.
- Databricks recommends service principals for `run_as` so workspace security policies are enforced.

### 5.4 `presets` �?" the customisation escape hatch

Full list (VERIFIED from the reference page): `artifacts_dynamic_version`, `jobs_max_concurrent_runs`,
`name_prefix`, `pipelines_development`, `source_linked_deployment`, `tags`, `trigger_pause_status`.

`presets` can be set at top level or per target, so you can e.g. keep `mode: development` ergonomics but re-enable
schedules with `trigger_pause_status: UNPAUSED`.

### 5.5 `run_as`

Source: <https://docs.databricks.com/aws/en/dev-tools/bundles/run-as> (last updated **Jul 29, 2026**)

- Accepts `user_name` **or** `service_principal_name` (mutually exclusive). "Non-admins can only set this field to
  their own email."
- Settable at three levels: top-level `run_as`, per-target `targets.*.run_as`, and per-resource.
- When the **deploying** identity differs from the `run_as` identity, **only jobs and pipelines are supported**.
- **Not supported for model serving endpoints** �?" "An error occurs if these resources are defined in a bundle where
  `run_as` is also configured." **This matters for Lakewright.NET**: if our accelerator ships a model serving
  endpoint in the same bundle as a service-principal `run_as`, deployment fails. Plan to split serving endpoints
  into a separate bundle, or set `run_as` per-resource on jobs/pipelines only.
- The deploying identity needs `CAN_USE` on the service principal.

### 5.6 Variables and secrets

Source: <https://docs.databricks.com/aws/en/dev-tools/bundles/variables> (last updated **Mar 16, 2026**)

Value precedence, highest first:
1. `--var="key=value"` CLI flag
2. `BUNDLE_VAR_<name>` environment variable
3. `.databricks/bundle/<target>/variable-overrides.json`
4. `targets.<target>.variables` in YAML
5. the variable's `default`

Variable kinds: plain (string), `type: complex` (structured objects), and `lookup` (resolve an object's ID by name
�?" supported for alert, cluster_policy, cluster, dashboard, job, pipeline, warehouse, notification destinations,
and others).

Substitutions available include `${bundle.name}`, `${bundle.target}`, `${workspace.host}`,
`${workspace.current_user.userName}`, `${workspace.current_user.short_name}`, and cross-resource references like
`${resources.jobs.<key>.id}`.

**Secrets: there is no documented mechanism for referencing a Databricks secret from a bundle *variable*.** The
supported path is the `secret_scope` **resource** type (create/manage scopes declaratively) plus the normal
`{{secrets/<scope>/<key>}}` reference syntax inside job/task definitions, which is a Databricks-runtime feature
rather than a bundle feature. For CI, credentials arrive as **environment variables** (`DATABRICKS_CLIENT_ID` etc.)
or via OIDC, never in `databricks.yml`. Our docs should state plainly: **no secret values ever go in
`databricks.yml`**, and the file is expected to be committed.

---

## 6. Testing Databricks assets

Source: <https://docs.databricks.com/aws/en/developers/best-practices> (last updated **Jul 10, 2026**)

Databricks recommends a three-layer approach, quoted:

1. **Unit tests** �?" "Keep business logic in importable `src/` modules and cover it with `pytest` or an equivalent
   framework. Run these on every pull request so failures block merges."
2. **Bundle validation** �?" "Run `bundle validate` locally. In CI, prefer `bundle deploy` to a non-production
   workspace to catch YAML and resource-mapping issues before production deploys."
3. **Integration tests in staging** �?" "After deploying to staging, run end-to-end jobs with completion checks and
   critical data-quality assertions such as row-count or schema expectations."

The promotion gate, quoted: **"Treat 'all tests pass on the main branch and in staging' as the gate for promoting
artifacts to production."**

For pipelines specifically, Databricks recommends using the built-in development and validation features "rather
than ad-hoc notebook runs", and testing against small datasets that deliberately include error records. Spark
Declarative Pipelines **expectations** surface data-quality results in a table, which integration tests can assert
against.

Development workflow: trunk-based �?" develop in a dev workspace, short-lived feature branches, merge to `main`,
CI/CD auto-deploys to staging for automated tests, then promote to production.

### The ephemeral-target gap

**The brief asks about `bundle run` in CI against an ephemeral target followed by `bundle destroy`. This pattern is
NOT documented by Databricks.** The `flows` page has no guidance on temporary test infrastructure or cleanup, and I
found no official page describing it. Recording this honestly as a gap.

The primitives clearly exist, so the pattern is constructible (this is **my inference, UNVERIFIED end-to-end**):

```
databricks bundle deploy  --target ci        # ci target uses mode: development
databricks bundle run     --target ci <job>  # smoke test
databricks bundle destroy --target ci --auto-approve
```

with the `ci` target using `mode: development` (so it gets prefixing and paused schedules) and a `name_prefix`
preset keyed on the PR number or run ID to isolate concurrent CI runs. Two things to design deliberately:

- **Destroy must run in an `if: always()` step**, or a failed smoke test leaks resources and cost accumulates.
- **`bundle destroy` deletes data-bearing resources.** With `schema` and `volume` in the bundle, a careless
  ephemeral-target teardown can drop real tables. Keep data resources out of the ephemeral bundle, or point them at
  a throwaway catalog.

Since this is a documented gap rather than a documented pattern, Lakewright.NET shipping a working, tested
ephemeral-CI-target recipe is a genuine differentiator �?" but we must build and verify it ourselves rather than
citing Databricks for it.

---

## 7. Alternatives

### Terraform �?" `databricks/databricks` provider

VERIFIED: latest **v1.123.0**, released **2026-07-29** �?"
<https://raw.githubusercontent.com/databricks/terraform-provider-databricks/main/CHANGELOG.md>,
repo <https://github.com/databricks/terraform-provider-databricks>. It lives under the official `databricks` GitHub
org and is linked from the Databricks CI/CD landing page as a supported approach.

Its coverage is materially **broader** than bundles: account-level resources (`mws_*` �?" workspace provisioning,
networking, private endpoints), identity (SCIM users, groups, service principals), Unity Catalog metastores and
their assignments, permissions and grants, plus all the workspace-level objects. Bundles deliberately scope
themselves to *the artifacts a project deploys*; Terraform covers *the platform the project runs on*.

**When to pick Terraform for an accelerator like Lakewright.NET:** for anything that provisions the platform rather
than deploying a workload �?" creating the workspaces themselves, wiring the metastore, managing per-tenant catalogs
and grants at scale, network config, and account-level identity. This is directly relevant to the multi-tenancy
story (cross-ref R04): if tenant onboarding means "create a catalog, create groups, apply grants", Terraform (or
the REST API) is the better-fitting tool, since `grants` is not a bundle resource type (§2.2). Note the irony that
bundles are *moving away* from Terraform internally (§2.3), which decouples the two �?" a Terraform layer for
platform provisioning is no longer double-counting the same dependency.

**Recommended split:** Terraform (or Bicep/ARM on Azure) for platform and tenant provisioning; bundles for the
application workloads �?" jobs, pipelines, apps, dashboards, serving endpoints. Do not use Terraform to deploy jobs
and pipelines when bundles exist; you lose dev-mode isolation, `bundle run`, and the whole local dev loop.

### Pulumi �?" `pulumi-databricks`

VERIFIED: latest **v1.98.0**, published **3 July 2026** �?" <https://www.pulumi.com/registry/packages/databricks/>,
source <https://github.com/pulumi/pulumi-databricks>. It is a bridged provider over the Terraform provider, so
resource coverage tracks Terraform's closely, with the benefit of real languages �?" **including C#**, which is
superficially attractive for a .NET accelerator.

**The signal against it:** Databricks has **retired its own Pulumi documentation.** The page now sits at
<https://docs.databricks.com/aws/en/archive/dev-tools/pulumi> under an `/archive/` path and carries the banner
**"This documentation has been retired and might not be updated."** It further notes Pulumi is developed by a third
party and directs users to Pulumi Support rather than Databricks. The provider itself is actively maintained by
Pulumi and is not deprecated �?" but Databricks no longer documents or supports the path.

**When to pick Pulumi:** essentially only if the consuming organisation has already standardised on Pulumi.
For Lakewright.NET I'd recommend against making it the documented default despite the C# appeal �?" an open-source
accelerator should sit on the paths the vendor documents and supports, and we would be adopting a bridged provider
plus a retired documentation trail in exchange for language ergonomics on what is a small amount of infrastructure
code. Worth one paragraph in an "alternatives" appendix, not a supported path.

---

## 8. Minimal working `databricks.yml` skeleton

Constructed from verified keys on the reference (Jul 24, 2026), deployment-modes (Jul 24, 2026), run-as
(Jul 29, 2026), and examples (Jul 8, 2026) pages. Every key used below appears in the verified key lists in §2.1.

**Not deployed against a live workspace** �?" no Databricks workspace was available in this research session. Treat
as schema-correct-by-construction; validate with `databricks bundle validate` before publishing.

```yaml
# databricks.yml �?" Lakewright.NET
bundle:
  name: lakewright
  # Pin the engine explicitly rather than relying on the CLI-version default.
  engine: direct
  # Fail fast if a contributor is on a CLI older than the direct-engine GA.
  databricks_cli_version: ">=1.3.0"

include:
  - resources/*.yml

variables:
  catalog:
    description: Unity Catalog catalog that this deployment writes to.
  warehouse_id:
    description: SQL warehouse backing dashboards and SQL tasks.

targets:
  # Per-developer sandbox. mode: development prefixes every resource with
  # [dev <short_name>], pauses schedules, and disables the deployment lock,
  # so several developers share one workspace without colliding.
  dev:
    mode: development
    default: true
    workspace:
      host: https://adb-1111111111111111.1.azuredatabricks.net
    variables:
      catalog: lakewright_dev

  # Ephemeral target for pull-request smoke tests. Deployed and destroyed by CI.
  # Uses development mode for the prefixing, with the prefix keyed on the CI run.
  ci:
    mode: development
    workspace:
      host: https://adb-1111111111111111.1.azuredatabricks.net
    presets:
      name_prefix: "[ci ${var.ci_run_id}] "
    variables:
      catalog: lakewright_ci

  prod:
    mode: production
    workspace:
      host: https://adb-2222222222222222.2.azuredatabricks.net
      root_path: /Workspace/.bundle/${bundle.name}/${bundle.target}
    # Production runs as a service principal, not as the deploying human.
    # Required by production mode unless you pin per-user paths.
    run_as:
      service_principal_name: 5cf3z04b-a73c-4x46-9f3d-52da7999069e
    variables:
      catalog: lakewright_prod
    permissions:
      - group_name: lakewright-platform-admins
        level: CAN_MANAGE
```

Notes on the skeleton:

- `run_as` takes the service principal's **application ID (UUID)**, not a display name.
- Because `run_as` is set, do **not** put a `model_serving_endpoint` in this bundle (§5.3) �?" it will error. Either
  move serving endpoints to their own bundle or set `run_as` per-resource on jobs and pipelines only.
- The `ci` target's `name_prefix` preset needs `ci_run_id` declared as a variable and supplied via
  `BUNDLE_VAR_ci_run_id=${{ github.run_id }}`. UNVERIFIED that a variable substitution is legal inside a
  `presets.name_prefix` value �?" confirm during the spike; the fallback is `--var` at deploy time or a literal.
- Workspace paths are auto-prefixed with `/Workspace` (behaviour change in CLI 0.230.0), so do not write
  `/Workspace/${workspace.root_path}/...`.

---

## 9. Open items for the implementation spike

1. **`bundle validate` without credentials** �?" does it work? Determines whether fork PRs can run it (§4.5).
2. **Grants in bundles** �?" can grants be expressed nested in `catalog`/`schema`/`volume`, or is Terraform required
   for tenant grant management? Load-bearing for the multi-tenancy design (§2.2, §7).
3. **Variable substitution inside `presets.name_prefix`** �?" legal or not (§8).
4. **Ephemeral CI target + `bundle destroy`** �?" build and verify the recipe end to end; it is undocumented (§6).
5. **Direct-engine field-removal semantics** �?" confirm the "removed field reverts to default" behaviour against a
   real resource before we advertise the engine pin (§2.3).

---

## 10. Source index

| # | URL | Last updated |
|---|---|---|
| 1 | <https://docs.databricks.com/aws/en/dev-tools/bundles/> | Jul 10, 2026 |
| 2 | <https://learn.microsoft.com/en-us/azure/databricks/release-notes/dev-tools/bundles> | Jul 14, 2026 |
| 3 | <https://docs.databricks.com/aws/en/dev-tools/bundles/faqs> | �?" |
| 4 | <https://docs.databricks.com/aws/en/dev-tools/bundles/reference> | Jul 24, 2026 |
| 5 | <https://docs.databricks.com/aws/en/dev-tools/bundles/settings> | Mar 16, 2026 |
| 6 | <https://docs.databricks.com/aws/en/dev-tools/bundles/resources> | Jul 27, 2026 |
| 7 | <https://docs.databricks.com/aws/en/dev-tools/bundles/direct> | Jul 27, 2026 |
| 8 | <https://docs.databricks.com/aws/en/dev-tools/bundles/deployment-modes> | Jul 24, 2026 |
| 9 | <https://docs.databricks.com/aws/en/dev-tools/bundles/run-as> | Jul 29, 2026 |
| 10 | <https://docs.databricks.com/aws/en/dev-tools/bundles/variables> | Mar 16, 2026 |
| 11 | <https://docs.databricks.com/aws/en/dev-tools/bundles/examples> | Jul 8, 2026 |
| 12 | <https://docs.databricks.com/aws/en/dev-tools/cli/bundle-commands> | Jul 23, 2026 |
| 13 | <https://docs.databricks.com/aws/en/dev-tools/cli/install> | Jun 24, 2026 |
| 14 | <https://github.com/databricks/cli/releases> | v1.10.0, Jul 29, 2026 |
| 15 | <https://github.com/databricks/setup-cli> | �?" |
| 16 | <https://docs.databricks.com/aws/en/dev-tools/ci-cd/> | Jul 10, 2026 |
| 17 | <https://docs.databricks.com/aws/en/dev-tools/ci-cd/flows> | Jul 10, 2026 |
| 18 | <https://docs.databricks.com/aws/en/dev-tools/ci-cd/github> | Jun 11, 2026 |
| 19 | <https://docs.databricks.com/aws/en/dev-tools/auth/provider-github> | Apr 21, 2026 |
| 20 | <https://docs.databricks.com/aws/en/dev-tools/auth/oauth-m2m> | Jul 22, 2026 |
| 21 | <https://docs.databricks.com/aws/en/developers/best-practices> | Jul 10, 2026 |
| 22 | <https://docs.github.com/en/actions/reference/workflows-and-actions/events-that-trigger-workflows> | �?" |
| 23 | <https://github.com/databricks/terraform-provider-databricks> | v1.123.0, Jul 29, 2026 |
| 24 | <https://www.pulumi.com/registry/packages/databricks/> | v1.98.0, Jul 3, 2026 |
| 25 | <https://docs.databricks.com/aws/en/archive/dev-tools/pulumi> | RETIRED |

### Dead links encountered (do not cite)

- `docs.databricks.com/aws/en/dev-tools/bundles/targets` �?' 404 (content lives in `reference`)
- `docs.databricks.com/aws/en/dev-tools/ci-cd/best-practices` �?' 404 on the AWS path
- `learn.microsoft.com/en-us/azure/databricks/dev-tools/ci-cd/best-practices` �?' 404
- `docs.azure.cn/en-us/databricks/dev-tools/ci-cd/best-practices` �?' 404

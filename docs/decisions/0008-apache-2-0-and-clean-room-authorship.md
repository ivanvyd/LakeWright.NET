# ADR 0008: Apache-2.0, and clean-room authorship

Status: accepted
Date: 2026-07-31

## Context

Two licensing questions. Which license this project uses, and what we may adapt from the Databricks
ecosystem.

The second turned out to matter more. Databricks Labs projects and Solution Accelerators are not
open source. `ucx`, `dqx` and `dbldatagen` all ship under the proprietary Databricks License, which
states: "You may not use the Licensed Materials except in connection with your use of the Databricks
Services." That is incompatible with Apache-2.0 redistribution.

## Decision

Apache-2.0, with a DCO sign-off and no contributor licence agreement.

No code is adapted from Databricks Labs projects or Solution Accelerators. Where those repositories
solve a problem we also face, we read the documentation they were built from and write our own.

## Consequences

Apache-2.0 over MIT for the explicit patent grant in section 3. This is a component that companies
embed in commercial products, and the patent grant is what makes that reviewable by their counsel.
The sibling repository `production-databricks-patterns` is MIT; the difference is deliberate, because
a template someone copies and a dependency someone links are not the same risk.

DCO over a CLA because a CLA is a barrier for a project with no legal entity behind it to hold the
rights a CLA would assign.

A dependency under a copyleft license would make the Apache-2.0 promise in the README false, so
`dependency-review-action` denies GPL, AGPL and LGPL rather than leaving it to review.

Being genuinely Apache-2.0 is more permissive than most of Databricks' own community output, which is
worth stating plainly in the README because readers assume the opposite.

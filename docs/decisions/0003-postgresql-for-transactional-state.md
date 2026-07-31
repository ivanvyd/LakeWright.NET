# ADR 0003: PostgreSQL for transactional state, not Lakebase

Status: accepted
Date: 2026-07-31

## Context

Transactional SaaS state (organisations, membership, subscriptions, operation records, audit events)
needs row-level writes with transactions and millisecond reads. Databricks is not that store.

Lakebase is Databricks' managed Postgres, positioned for exactly this tier, with Unity Catalog
governance and synced tables to the lakehouse. It is Postgres 17 over the standard wire protocol, so
Npgsql and EF Core work.

## Decision

Standard PostgreSQL, with Lakebase documented as a deployment option rather than a fork.

Two reasons. Contributors must be able to run the project with `docker run postgres` and no cloud
account; a transactional store that requires a Databricks account puts the platform on the critical
path of every unit test. And Lakebase is generally available on AWS but beta on Azure, which is the
reference cloud here.

## Consequences

The EF Core model targets standard Postgres and therefore runs unmodified on Lakebase, Azure Database
for PostgreSQL, or a container. No abstraction layer is introduced to achieve this; it follows from
not using anything Lakebase-specific.

We give up synced tables, so moving operational state into the lakehouse for analysis is our problem.
For v0.1 that is a job reading from Postgres, which is a documented pattern rather than a novel one.

Revisit when Lakebase reaches general availability on Azure. The trigger is written down so the
decision gets re-opened by a fact rather than by taste.

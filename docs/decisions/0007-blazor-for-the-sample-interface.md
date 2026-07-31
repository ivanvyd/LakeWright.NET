# ADR 0007: Blazor for the sample interface

Status: accepted
Date: 2026-07-31

## Context

The sample product needs a user interface good enough that the architecture is believable. The
candidates were a Blazor Web App and React with TypeScript.

React has the larger contributor pool and the richer component and charting ecosystem. It also adds
a Node toolchain, a second dependency graph, a second security surface, and a token-handling story
for a single-page application.

## Decision

Blazor Web App with Interactive Server rendering, and Static SSR for content pages.

## Consequences

The trade-off, stated rather than glossed: we accept a smaller contributor pool and a thinner
component ecosystem in exchange for one language across the repository, cookie authentication with
no token handling in the browser, and no Node in CI.

A .NET team evaluating this project is the target reader, and for them a single-language repository
is a feature. A frontend specialist arriving to contribute will find less familiar ground, and that
cost is real.

Interactive Server holds a circuit per user, which is a scaling characteristic worth understanding
before copying the sample into production. The documentation says so rather than leaving it as a
surprise.

The interface is deliberately a sample and not a component library. Nothing in `Signalboard` is
published as a package, so this decision constrains the demonstration rather than the reusable core.
If it proves wrong, replacing it touches one project.

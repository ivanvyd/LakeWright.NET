# ADR 0009: A separate, optional AI module, and a shim we intend to give away

Status: accepted
Date: 2026-08-01

## Context

Databricks model serving speaks an OpenAI-compatible API, so reaching it from .NET needs the stock
`Microsoft.Extensions.AI.OpenAI` client pointed at a workspace rather than a bespoke integration.
Chat and tool calling work through it unchanged.

Streaming does not. Databricks attaches a `usage` object to every streaming chunk, and on all but
the last, `completion_tokens` and `total_tokens` are `null`. The OpenAI deserialiser types those as
numbers and throws part-way through, so the caller loses a response it was already rendering.
Measured against `databricks-claude-sonnet-5` on 2026-08-01:

```
{"cache_read_input_tokens":0,"completion_tokens":null,"prompt_tokens":9,"total_tokens":null,...}
{"cache_read_input_tokens":0,"completion_tokens":16,"prompt_tokens":9,"total_tokens":25,...}
```

## Decision

**The AI module is a separate project, `LakeWright.AI`, and no other package references it.** A
product that queries a warehouse and runs jobs takes no dependency on an AI client, a model
protocol, or their transitive graph. `AddLakeWrightDatabricks` does not register it; an adopter who
wants it calls `AddDatabricksChatClient` deliberately.

**The shim strips the incomplete `usage` object rather than repairing it to zero,** and we intend to
offer it upstream rather than keep it.

## Consequences

Stripping over zeroing, stated rather than glossed: zeros deserialise cleanly and then lie. A caller
metering tokens would add several chunks of zero and record the total as whatever the last chunk
said, arriving at the right answer by accident and the wrong one the moment the shape changes.
Absent is what OpenAI's own protocol does — usage arrives once, on a final chunk, and only when
asked for — so a consumer written against OpenAI behaves correctly without knowing the shim exists.

Carrying a vendor workaround costs us something. It belongs in the Databricks serving layer or in
the .NET client, and a private fix nobody offers upstream is how every consumer of that API ends up
writing the same forty lines. The obligation this record creates is to file it; whether anyone
accepts it is not ours to decide.

The shim is testable in both directions, which is what keeps it honest. `LiveChatTests` asserts that
the same call **fails** without it. The day Databricks fixes the payload, that test goes red, and
the correct response is to delete the shim rather than to keep it working.

A separate project means a separate package, and one more thing to version. That is the price of not
putting an AI dependency in front of an adopter who came for tenant isolation.

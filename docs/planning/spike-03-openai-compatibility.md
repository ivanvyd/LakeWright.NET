# Spike 03: Microsoft.Extensions.AI against Databricks Model Serving

Run 2026-07-31 against `lakewright-dev`, endpoint `databricks-claude-haiku-4-5` (pay-per-token).
`Microsoft.Extensions.AI` and `Microsoft.Extensions.AI.OpenAI` 10.8.3, `OpenAI` 2.12.0.

**Kill condition partly triggered.** Chat and tool calling work through the stock OpenAI client.
Streaming does not. The AI module stays in v0.1; streaming needs a documented shim, and the
research note claiming full OpenAI compatibility was too generous.

## Setup under test

```csharp
var options = new OpenAIClientOptions { Endpoint = new Uri($"{host}/serving-endpoints") };
var openAi  = new OpenAIClient(new ApiKeyCredential(token), options);
IChatClient chat = openAi.GetChatClient("databricks-claude-haiku-4-5").AsIChatClient();
```

The Databricks token is the Entra access token from spike 01. No Databricks secret.

## Results

| Capability | Result |
|---|---|
| Non-streaming chat | **Works.** Returned the expected text with usage `in=12 out=4`. |
| Tool calling via `UseFunctionInvocation` | **Works.** The function was invoked and its result used in the answer. |
| Streaming chat | **Fails** on the first chunk. |
| Usage on a streaming call | **Fails**, same cause. |

The streaming exception:

```
InvalidOperationException: The requested operation requires an element of type 'Number',
but the target element has type 'Null'.
```

## Why streaming fails

Databricks attaches a `usage` object to **every** streaming chunk, with two of its fields null:

```json
"usage": { "cache_read_input_tokens": 0,
           "completion_tokens": null,
           "prompt_tokens": 8,
           "total_tokens": null,
           "cache_creation_input_tokens": 0 }
```

OpenAI's schema treats `completion_tokens` and `total_tokens` as non-nullable integers, and only
emits `usage` on the final chunk, and then only when `stream_options.include_usage` is set. The
generated deserialiser therefore throws on the first chunk it reads.

This is a Databricks deviation from the OpenAI wire format, not a bug in the .NET client. Note also
the non-OpenAI fields `cache_read_input_tokens` and `cache_creation_input_tokens`, which are
Anthropic's, surfacing through the compatibility layer.

## What this means for the module

- **Chat and tool calling need no client code.** That part of ADR-level planning holds.
- **Streaming needs a shim**: a pipeline policy that repairs or strips `usage` on chunks before
  deserialisation. It is small, roughly a `PipelinePolicy` over the response stream, and it is a
  precise, well-evidenced upstream contribution: either to Databricks, whose payload is
  non-conforming, or to the .NET client, which could tolerate nulls.
- **Per-tenant token metering cannot rely on streaming usage.** `prompt_tokens` is populated per
  chunk but `completion_tokens` is null throughout, so output tokens are unavailable until the
  shim exists. Budget enforcement on streaming conversations has to estimate, and the docs must
  say so rather than implying exact metering.

## Correction to earlier research

The ecosystem note recorded "Databricks exposes an OpenAI-compatible API" and inferred that
`Microsoft.Extensions.AI.OpenAI` would work. That inference was carried as **[G] unverified** and
flagged as a blocking spike, which was the right call: it is two thirds right, and the missing
third is the part a chat interface needs most.

## Not tested

Embeddings, structured outputs, `stream_options.include_usage`, provisioned-throughput endpoints,
and whether other model families on the same workspace emit the same malformed `usage`. The shim
should be written against more than one endpoint before it is trusted.

# Checkpoint and resume Genie messages

`AskAsync` starts a conversation and waits. `ContinueAsync` submits another question and waits.
Neither operation resumes a message that Genie has already accepted.

Use the separate operations when a job must persist accepted identifiers before waiting:

```csharp
GenieAcceptedMessage accepted = await conversations.StartConversationAsync(
    tenant, ownerKey, question, cancellationToken);

// Persist both identifiers in the application's durable job record here.
// A later process uses the same authorized tenant and owner, without submitting again.
GenieAnswer answer = await conversations.WaitForResponseAsync(
    tenant, ownerKey, accepted.ConversationId, accepted.MessageId, cancellationToken);
```

`SendMessageAsync` submits one follow-up and returns its new message ID without polling.
`GetMessageAsync` reads a message once. Its snapshot includes the raw `Status` and `IsTerminal`.
An unfamiliar status has `GenieOutcome.Unknown`; it is terminal if Genie also returns an error.
`WaitForResponseAsync` polls with GET only. Calling it again resumes that same message.

Resolve `tenant` from authenticated membership. Use an opaque, stable `ownerKey` that identifies
the application's authorized principal within its tenant. Ownership records carry no tenant, so
build the key from both, for example the tenant ID and the principal ID: a principal's key from
one tenant must not match its key in another. All read, wait and follow-up operations
check ownership before acquiring credentials or calling Databricks. Identifiers alone grant no
access. Keep tenant-to-agent mappings and owner-key construction consistent across processes.

Starting records ownership before returning accepted IDs. The ownership write has an independent,
bounded deadline and completes even if the caller cancels after known acceptance. Applications
must still persist the returned message ID themselves. There is no transaction between remote
acceptance and application storage. An ownership-write failure exposes known IDs through
`ConversationOwnershipPersistenceException` for trusted reconciliation.

Each separate operation uses `GenieOptions.ResponseTimeout`. Waiting returns `GenieOutcome.TimedOut`
with known IDs; a GET that outlives the per-request HTTP timeout is repeated while that deadline
lasts. A single read that runs out of time throws `TimeoutException`. `QUERY_RESULT_EXPIRED` is
reported as `Completed`: the result rows expired, the answer text and SQL did not. Cancellation
stops local work and does not cancel the remote message. `AskAsync` and `ContinueAsync` retain one total response
deadline over submission and polling.

There are no automatic POST retries. A connection failure or timeout before acceptance is observed
does not prove rejection. Repeating that submission may create another message. GET failures can
be retried explicitly with the saved identifiers. An application that adds HTTP retries must
exclude unsafe submissions.

The default ownership store lasts only for the current process. For restarts or replicas, register
`LakeWright.Caching.Redis` with a shared `IConnectionMultiplexer` and call
`AddLakeWrightRedisConversationOwnership()`, or supply a durable `IConversationOwnership`.
Ownership retention and application checkpoint retention must cover the intended resume window.

The new interface operations have default implementations that throw `NotSupportedException`.
Existing custom `IGenieConversations` implementations continue to compile; they must implement
these operations before consumers can use them. The built-in typed HTTP client registration keeps
its existing name and pipeline configuration.

The registration disables its default HTTP request loggers. At the .NET 8 dependency floor, those
loggers can record bearer headers at Trace. Application-added HTTP loggers, telemetry and manually
constructed clients remain the host's responsibility. Do not record tokens or raw response bodies.
Registration also removes earlier loggers for that same named client, and loggers added for every
client with `ConfigureHttpClientDefaults`, whatever the registration order: the factory applies
defaults before named configuration. Configure deliberate custom HTTP logging per named client,
after the library registration, and apply your own redaction.

[OAuth custom identity claims](https://learn.microsoft.com/en-us/azure/databricks/dev-tools/auth/oauth-custom-identity-claims)
can govern SQL results. They do not partition service-principal-owned conversation history.
These ownership checks remain necessary even when the host configures claim-based data policies.
LakeWright does not mint claim-bound credentials or manage their caches.

The [candidate consumer fixture](../../tests/conversation-consumer/README.md) exercises the packaged
API with synthetic HTTP responses on .NET 8 and .NET 10. Live workspace behavior remains unverified
by that fixture.

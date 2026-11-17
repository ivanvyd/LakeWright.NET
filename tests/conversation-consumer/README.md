# Candidate Conversations and HTTP privacy fixture

This executable consumes candidate NuGet packages, without project references, credentials or live
Databricks calls. It checks accepted IDs before polling, GET-only resume, owner denial and an
existing custom interface adapter. The configured typed-client handler also verifies that the
existing HTTP pipeline registration remains usable. It probes all eleven library-owned HTTP
factories at Trace with synthetic bearer, Basic and query-signature values, checking formatted
messages, structured values and scopes. Custom host logging and the upstream SDK are outside that check.
The registrations remove earlier named-client loggers. Configure deliberate custom logging afterward.

From the repository root:

```powershell
dotnet pack src/LakeWright.Core/LakeWright.Core.csproj -c Release -p:Version=3.0.0-consumer-check -o artifacts/conversation-packages
dotnet pack src/LakeWright.Conversations/LakeWright.Conversations.csproj -c Release -p:Version=3.0.0-consumer-check -o artifacts/conversation-packages
dotnet pack src/LakeWright.Databricks/LakeWright.Databricks.csproj -c Release -p:Version=3.0.0-consumer-check -o artifacts/conversation-packages
dotnet pack src/LakeWright.Embedding/LakeWright.Embedding.csproj -c Release -p:Version=3.0.0-consumer-check -o artifacts/conversation-packages
dotnet pack src/LakeWright.Embedding.Ops/LakeWright.Embedding.Ops.csproj -c Release -p:Version=3.0.0-consumer-check -o artifacts/conversation-packages
$candidateFeed = (Resolve-Path artifacts/conversation-packages).Path
dotnet restore tests/conversation-consumer/ConversationConsumer.csproj -p:RestoreAdditionalProjectSources=$candidateFeed
dotnet run --project tests/conversation-consumer/ConversationConsumer.csproj -c Release -f net8.0 --no-restore
dotnet run --project tests/conversation-consumer/ConversationConsumer.csproj -c Release -f net10.0 --no-restore
```

`LakeWrightVersion` can select another candidate version. Use a distinct version after changing
the packages, because NuGet caches packages by identity. This fixture does not claim the new API
exists in the latest published release. Packaging does not publish it.

using System.ClientModel.Primitives;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LakeWright.AI;

/// <summary>
/// Removes the incomplete <c>usage</c> object Databricks puts on every streaming chunk.
/// </summary>
/// <remarks>
/// Databricks model serving is OpenAI-compatible except here. On a streaming chat completion it
/// attaches <c>usage</c> to *every* chunk, and on all but the last one
/// <c>completion_tokens</c> and <c>total_tokens</c> are <c>null</c>. Measured against
/// `databricks-claude-sonnet-5` on 2026-08-01:
///
/// <code>
/// {"cache_read_input_tokens":0,"completion_tokens":null,"prompt_tokens":9,"total_tokens":null,...}
/// {"cache_read_input_tokens":0,"completion_tokens":16,"prompt_tokens":9,"total_tokens":25,...}
/// </code>
///
/// The OpenAI .NET deserialiser types those as numbers, so it throws part-way through the stream
/// and the caller loses the response it was already rendering.
///
/// This strips <c>usage</c> from a chunk when it is incomplete, rather than repairing the nulls to
/// zero. Zeros would deserialise and then lie: a caller metering tokens would add several chunks
/// of zero and record the total as whatever the last chunk said, which is right by accident.
/// Absent is what OpenAI's own protocol does — usage arrives once, on a final chunk, and only when
/// asked for — so a consumer written against OpenAI behaves correctly without knowing this policy
/// exists.
///
/// The last chunk carries real numbers and passes through untouched, so token metering still works.
///
/// This belongs upstream, in either the Databricks serving layer or the .NET client, and the
/// project's position is to offer it there rather than to keep a private fix. Until then, an
/// adopter who removes this policy gets an exception on the first streaming call, not a subtle
/// wrong number.
/// </remarks>
internal sealed class StreamingUsageRepairPolicy : PipelinePolicy
{
    public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        ProcessNext(message, pipeline, currentIndex);
        Repair(message);
    }

    public override async ValueTask ProcessAsync(
        PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
    {
        await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        Repair(message);
    }

    /// <remarks>
    /// Wraps rather than buffers. The content stream stays the client's to read at its own pace,
    /// which is what streaming is for, and the transport's own buffering step is not handed a
    /// stream someone else already consumed.
    /// </remarks>
    private static void Repair(PipelineMessage message)
    {
        if (message.Response?.ContentStream is not { } stream) { return; }

        var contentType = message.Response.Headers.TryGetValue("Content-Type", out var value) ? value : null;
        if (contentType?.Contains("event-stream", StringComparison.OrdinalIgnoreCase) != true) { return; }

        message.Response.ContentStream = new SseUsageRepairStream(stream);
    }

    /// <summary>The line-level repair. Internal so the stream and the tests can both reach it.</summary>
    internal static string RepairLine(string line)
    {
        const string Prefix = "data: ";

        if (!line.StartsWith(Prefix, StringComparison.Ordinal)) { return line; }

        var payload = line[Prefix.Length..];
        if (payload is "[DONE]" or "") { return line; }

        JsonNode? node;
        try
        {
            node = JsonNode.Parse(payload);
        }
        catch (JsonException)
        {
            // Not our problem to diagnose: pass it through and let the client report it.
            return line;
        }

        if (node is not JsonObject chunk
            || chunk["usage"] is not JsonObject usage
            || usage["completion_tokens"] is not null)
        {
            return line;
        }

        chunk.Remove("usage");
        return Prefix + chunk.ToJsonString();
    }
}

using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LakeWright.Embedding;

internal static class EmbeddingHttp
{
    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient client,
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException exception)
        {
            throw new TransportException("The Databricks workspace could not be reached.", exception);
        }
        catch (TaskCanceledException exception) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TransportException("The Databricks workspace request timed out.", exception);
        }
    }

    public static JsonDocument ParseJson(string body, string operation)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException)
        {
            throw new WorkspaceRejectedException(
                HttpStatusCode.BadGateway,
                $"The workspace returned invalid JSON for {operation}.");
        }
    }

    public static JsonObject ParseObject(string body, string operation)
    {
        try
        {
            return JsonNode.Parse(body) as JsonObject
                ?? throw new WorkspaceRejectedException(
                    HttpStatusCode.BadGateway,
                    $"The workspace returned a non-object JSON value for {operation}.");
        }
        catch (JsonException)
        {
            throw new WorkspaceRejectedException(
                HttpStatusCode.BadGateway,
                $"The workspace returned invalid JSON for {operation}.");
        }
    }
}

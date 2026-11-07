using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

// The published client owns its HTTP transport. Use the consumer-floor loopback fixture pattern
// to exercise its actual serialization without a workspace or an upstream proxy.
internal sealed class OfflineWorkspace : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Task _request;
    private int _requests;

    internal OfflineWorkspace(CancellationToken cancellationToken)
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        BaseAddress = $"http://127.0.0.1:{port}";
        _listener.Prefixes.Add(BaseAddress + "/");
        _listener.Start();
        _request = HandleAsync(cancellationToken);
    }

    internal string BaseAddress { get; }
    internal int Requests => Volatile.Read(ref _requests);
    internal Task VerifyRequestAsync() => _request;

    private async Task HandleAsync(CancellationToken cancellationToken)
    {
        var context = await _listener.GetContextAsync().WaitAsync(cancellationToken);
        Interlocked.Increment(ref _requests);
        using var payload = await JsonDocument.ParseAsync(context.Request.InputStream, cancellationToken: cancellationToken);
        var root = payload.RootElement;
        var parameter = root.GetProperty("parameters").EnumerateArray().Single();
        var valid = context.Request.HttpMethod == "POST"
            && context.Request.Url!.AbsolutePath == "/api/2.0/sql/statements"
            && root.GetProperty("catalog").GetString() == "analytics"
            && root.GetProperty("schema").GetString() == "tenant_acme"
            && root.GetProperty("warehouse_id").GetString() == "offline-warehouse"
            && root.GetProperty("statement").GetString() == "SELECT :value AS value"
            && parameter.GetProperty("name").GetString() == "value"
            && parameter.GetProperty("value").GetString() == "1";
        var body = Encoding.UTF8.GetBytes("""{"statement_id":"offline-statement","status":{"state":"SUCCEEDED"},"manifest":{"schema":{"columns":[{"name":"value"}]},"total_row_count":1,"total_chunk_count":1},"result":{"chunk_index":0,"row_offset":0,"row_count":1,"data_array":[["1"]]}}""");
        context.Response.ContentType = "application/json";
        context.Response.ContentLength64 = body.Length;
        await context.Response.OutputStream.WriteAsync(body, cancellationToken);
        context.Response.Close();
        if (!valid) { throw new InvalidOperationException("The serialized query escaped its expected tenant scope."); }
    }

    public void Dispose() => _listener.Close();
}

using System.Net;
using System.Text;
using Azure.Core;
using LakeWright.Conversations;
using LakeWright.Core.Tenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>Tenants, responses and clients shared by the Genie conversation tests.</summary>
internal static class GenieTestKit
{
    public static readonly TenantId AcmeId = TenantId.Parse("0198f000-0000-7000-8000-00000000ac11");
    public static readonly TenantId GlobexId = TenantId.Parse("0198f000-0000-7000-8000-00000000617b");

    public static TenantContext Tenant(TenantId id) => TenantContextFactory.ForTenant(id, "analytics");

    public static HttpResponseMessage JsonResponse(string body) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(body, Encoding.UTF8, "application/json"),
    };

    public static GenieConversations Conversations(
        HttpClient http,
        GenieOptions options,
        TimeProvider time,
        IConversationOwnership? ownership = null,
        TokenCredential? credential = null) =>
        new(http, credential ?? new CountingCredential(), Options.Create(options), time, ownership);

    /// <summary>The in-memory store <c>AddLakeWrightGenie</c> registers.</summary>
    public static IConversationOwnership BuiltInOwnership()
    {
        var services = new ServiceCollection();
        services.AddLakeWrightGenie(new ConfigurationBuilder().Build());
        using var provider = services.BuildServiceProvider();
        return provider.GetRequiredService<IConversationOwnership>();
    }
}

/// <summary>One client over a recording handler, with Acme mapped to the agent <c>space</c>.</summary>
internal sealed class GenieFixture : IDisposable
{
    private readonly HttpClient _http;
    private readonly GenieOptions _options;

    public GenieFixture(
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond,
        TimeSpan? responseTimeout = null,
        IConversationOwnership? ownership = null,
        FakeTimeProvider? time = null)
    {
        Handler = new RecordingHandler(respond);
        _http = new HttpClient(Handler) { BaseAddress = new Uri("https://workspace.invalid/") };
        _options = new GenieOptions { ResponseTimeout = responseTimeout ?? TimeSpan.FromSeconds(5) };
        _options.Spaces.Add(GenieTestKit.AcmeId.ToString(), "space");
        Ownership = ownership ?? new TestOwnership();
        Time = time ?? new FakeTimeProvider();
        Client = NewClient();
    }

    public RecordingHandler Handler { get; }
    public CountingCredential Credential { get; } = new();
    public IConversationOwnership Ownership { get; }
    public FakeTimeProvider Time { get; }
    public IGenieConversations Client { get; }

    /// <summary>Maps another tenant to its own agent. Clients see the change, because they share the options.</summary>
    public GenieFixture MapSpace(TenantId tenant, string space)
    {
        _options.Spaces[tenant.ToString()] = space;
        return this;
    }

    /// <summary>Points every unmapped tenant at an acknowledged shared agent.</summary>
    public GenieFixture ShareSpace(string space)
    {
        _options.SharedSpaceId = space;
        _options.AcknowledgeNoTenantIsolation = true;
        return this;
    }

    public GenieConversations NewClient() => GenieTestKit.Conversations(_http, _options, Time, Ownership, Credential);

    public void Dispose() => _http.Dispose();
}

internal sealed class RecordingHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> respond) : HttpMessageHandler
{
    public List<HttpMethod> Methods { get; } = [];
    public List<string> Paths { get; } = [];
    public int Calls => Methods.Count;
    public Action? ResponseDelay { get; set; }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Methods.Add(request.Method);
        Paths.Add(request.RequestUri!.AbsolutePath);
        var response = await respond(request, cancellationToken);
        ResponseDelay?.Invoke();
        return response;
    }
}

internal sealed class CountingCredential : TokenCredential
{
    public int Calls { get; private set; }

    public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls++;
        return new AccessToken("synthetic-token", DateTimeOffset.MaxValue);
    }

    public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) =>
        ValueTask.FromResult(GetToken(requestContext, cancellationToken));
}

/// <summary>
/// A host's own store written against 3.0: it does not override the tenant-aware members, so the
/// owner key alone grants access.
/// </summary>
internal sealed class TestOwnership : IConversationOwnership
{
    private readonly Dictionary<string, string> _owners = new(StringComparer.Ordinal);

    public ValueTask RecordAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_owners.TryGetValue(conversationId, out var owner) && owner != ownerKey)
        {
            throw new ConversationOwnershipException(conversationId);
        }

        _owners[conversationId] = ownerKey;
        return ValueTask.CompletedTask;
    }

    public ValueTask<bool> IsOwnerAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(_owners.TryGetValue(conversationId, out var owner) && owner == ownerKey);
    }

    public ValueTask<IReadOnlyList<string>> ListAsync(string ownerKey, CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<string>>([.. _owners.Where(pair => pair.Value == ownerKey).Select(pair => pair.Key)]);

    public ValueTask RemoveAsync(string conversationId, string ownerKey, CancellationToken cancellationToken = default)
    {
        if (_owners.TryGetValue(conversationId, out var owner) && owner == ownerKey)
        {
            _owners.Remove(conversationId);
        }

        return ValueTask.CompletedTask;
    }
}

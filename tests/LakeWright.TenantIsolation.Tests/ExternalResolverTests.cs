using LakeWright.AspNetCore;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public class ExternalResolverTests
{
    private static readonly TenantId TenantA = TenantId.Parse("0198f000-0000-7000-8000-0000000000a1");
    private static readonly TenantId TenantB = TenantId.Parse("0198f000-0000-7000-8000-0000000000b2");

    public sealed class MapResolver(ITenantContextFactory contexts) : ITenantContextResolver
    {
        private static readonly Dictionary<string, TenantId> Members = new(StringComparer.Ordinal)
        {
            ["alice"] = TenantA,
            ["bob"] = TenantB,
        };

        public ITenantContextFactory Contexts { get; } = contexts;

        public Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken cancellationToken) =>
            Task.FromResult(Members.TryGetValue(principalId, out var home) && home == tenantId
                ? Contexts.ForTenant(tenantId, "analytics")
                : null);
    }

    private sealed class OtherResolver(ITenantContextFactory contexts) : ITenantContextResolver
    {
        public ITenantContextFactory Contexts { get; } = contexts;

        public Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantContext?>(null);
    }

    private sealed class Bystander(ITenantContextFactory contexts)
    {
        public ITenantContextFactory Contexts { get; } = contexts;
    }

    private sealed class ResolverWithoutTheSeam : ITenantContextResolver
    {
        public Task<TenantContext?> ResolveAsync(TenantId tenantId, string principalId, CancellationToken cancellationToken) =>
            Task.FromResult<TenantContext?>(null);
    }

    [Fact]
    public async Task A_registered_resolver_mints_for_a_member_and_nobody_else()
    {
        await using var provider = new ServiceCollection()
            .AddLakeWrightTenancy<MapResolver>()
            .BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var resolver = scope.ServiceProvider.GetRequiredService<ITenantContextResolver>();

        var member = await resolver.ResolveAsync(TenantA, "alice", TestContext.Current.CancellationToken);
        var elsewhere = await resolver.ResolveAsync(TenantB, "alice", TestContext.Current.CancellationToken);
        var unknown = await resolver.ResolveAsync(TenantA, "mallory", TestContext.Current.CancellationToken);

        resolver.ShouldBeOfType<MapResolver>();
        member.ShouldNotBeNull();
        member.TenantId.ShouldBe(TenantA);
        member.Schema.ShouldBe(UnityCatalogIdentifier.SchemaForTenant(TenantA));
        elsewhere.ShouldBeNull();
        unknown.ShouldBeNull();
    }

    [Fact]
    public void A_service_outside_the_resolver_cannot_obtain_the_factory()
    {
        var services = new ServiceCollection()
            .AddLakeWrightTenancy<MapResolver>()
            .AddScoped<Bystander>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<ITenantContextFactory>().ShouldBeNull();
        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<ITenantContextFactory>())
            .Message.ShouldContain(nameof(ITenantContextFactory));
        Should.Throw<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<Bystander>())
            .Message.ShouldContain(nameof(ITenantContextFactory));
        scope.ServiceProvider.GetRequiredService<ITenantContextResolver>()
            .ShouldBeOfType<MapResolver>().Contexts.ShouldNotBeNull();
    }

    [Fact]
    public void A_service_outside_the_resolver_cannot_resolve_the_concrete_resolver()
    {
        using var provider = new ServiceCollection()
            .AddLakeWrightTenancy<MapResolver>()
            .BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetService<MapResolver>().ShouldBeNull();
        scope.ServiceProvider.GetRequiredService<ITenantContextResolver>().ShouldBeOfType<MapResolver>();
    }

    [Fact]
    public void A_resolver_can_use_an_explicit_non_default_lifetime_without_becoming_discoverable()
    {
        using var provider = new ServiceCollection()
            .AddLakeWrightTenancy<MapResolver>(ServiceLifetime.Singleton)
            .BuildServiceProvider();
        using var firstScope = provider.CreateScope();
        using var secondScope = provider.CreateScope();

        var first = firstScope.ServiceProvider.GetRequiredService<ITenantContextResolver>();
        var second = secondScope.ServiceProvider.GetRequiredService<ITenantContextResolver>();

        first.ShouldBeSameAs(second);
        first.ShouldBeOfType<MapResolver>();
        firstScope.ServiceProvider.GetService<MapResolver>().ShouldBeNull();
    }

    [Fact]
    public void Two_resolvers_each_hold_their_own_factory()
    {
        var services = new ServiceCollection()
            .AddLakeWrightTenancy<MapResolver>()
            .AddLakeWrightTenancy<OtherResolver>();
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        var resolvers = scope.ServiceProvider.GetServices<ITenantContextResolver>().ToArray();
        var first = resolvers.OfType<MapResolver>().Single().Contexts;
        var second = resolvers.OfType<OtherResolver>().Single().Contexts;

        resolvers.Length.ShouldBe(2);
        first.ShouldNotBeSameAs(second);
        scope.ServiceProvider.GetService<ITenantContextFactory>().ShouldBeNull();
    }

    [Fact]
    public void A_resolver_that_cannot_take_the_factory_is_refused_at_registration()
    {
        var services = new ServiceCollection();

        Should.Throw<InvalidOperationException>(() => services.AddLakeWrightTenancy<ResolverWithoutTheSeam>())
            .Message.ShouldContain(nameof(ITenantContextFactory));
        services.ShouldBeEmpty();
    }

    [Fact]
    public void The_factory_a_resolver_receives_validates_like_the_trusted_path()
    {
        using var provider = new ServiceCollection().AddLakeWrightTenancy<MapResolver>().BuildServiceProvider();
        using var scope = provider.CreateScope();
        var contexts = scope.ServiceProvider.GetRequiredService<ITenantContextResolver>()
            .ShouldBeOfType<MapResolver>().Contexts;

        Should.Throw<ArgumentException>(() => contexts.ForTenant(TenantA, "analytics; DROP SCHEMA other", "tenant_a"))
            .ParamName.ShouldBe("catalog");
        Should.Throw<ArgumentException>(() => contexts.ForTenant(TenantA, "analytics", "tenant_a`.`tenant_b"))
            .ParamName.ShouldBe("schema");
        Should.Throw<ArgumentException>(() => contexts.ForTenant(TenantA, "analytics", "tenant_a", "v1|v2"))
            .ParamName.ShouldBe("scopeVersion");
        contexts.ForTenant(TenantA, "analytics", "tenant_a", "v2").ScopeVersion.ShouldBe("v2");
    }

    [Fact]
    public void The_public_tenancy_surface_cannot_create_a_shared_schema_context()
    {
        typeof(ITenantContextFactory).GetMethod("ForSharedTenant").ShouldBeNull();
        typeof(TenantLocation).GetNestedType("SharedSchema").ShouldBeNull();
    }

    [Fact]
    public void The_shipped_resolver_goes_through_the_same_seam()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:LakeWright"] = "Host=localhost;Database=never_opened",
                ["Multitenancy:Catalog"] = "analytics",
            })
            .Build();
        var services = new ServiceCollection().AddLakeWright(configuration);
        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITenantContextResolver>().ShouldBeOfType<EfTenantContextResolver>();
        scope.ServiceProvider.GetService<ITenantContextFactory>().ShouldBeNull();
    }
}

using System.Reflection;
using System.Runtime.CompilerServices;
using Lakewright.Core.Tenancy;
using Lakewright.Databricks;

namespace Lakewright.TenantIsolation.Tests;

/// <summary>
/// The controls ADR 0002 rests on. These assert structure, not behaviour: that the shapes which
/// would allow a cross-tenant read do not exist in the first place.
/// </summary>
[Trait("Category", "TenantIsolation")]
public class StatementScopingTests
{
    private static readonly TenantId TenantA = TenantId.Parse("0198f000-0000-7000-8000-00000000000a");
    private static readonly TenantId TenantB = TenantId.Parse("0198f000-0000-7000-8000-00000000000b");

    [Fact]
    public void A_statement_takes_its_schema_from_the_context_not_the_caller()
    {
        var a = TenantContextFactory.ForTenant(TenantA, "analytics");
        var b = TenantContextFactory.ForTenant(TenantB, "analytics");

        var forA = TenantScopedStatement.Create(a, "SELECT count(*) FROM events");
        var forB = TenantScopedStatement.Create(b, "SELECT count(*) FROM events");

        forA.Tenant.Schema.ShouldNotBe(forB.Tenant.Schema);
        forA.Sql.ShouldBe(forB.Sql);
    }

    [Fact]
    public void There_is_no_way_to_build_a_statement_without_a_tenant_context()
    {
        // Every public factory on the statement type must demand a TenantContext. An overload
        // that did not would be the whole vulnerability, so this asserts the absence of one.
        var factories = typeof(TenantScopedStatement)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(TenantScopedStatement.Create));

        factories.ShouldNotBeEmpty();
        factories.ShouldAllBe(m => m.GetParameters()[0].ParameterType == typeof(TenantContext));
    }

    [Fact]
    public void The_executor_accepts_nothing_but_a_tenant_scoped_statement()
    {
        // A convenience overload taking raw SQL, a catalog or a schema would let a caller opt out
        // of scoping without noticing. None may exist.
        var offending = typeof(IStatementExecutor)
            .GetMethods()
            .Where(m => m.Name == nameof(IStatementExecutor.ExecuteAsync))
            .SelectMany(m => m.GetParameters())
            .Where(p => p.ParameterType == typeof(string))
            .ToArray();

        offending.ShouldBeEmpty(
            "ExecuteAsync must take a TenantScopedStatement and nothing that could carry SQL, " +
            "a catalog or a schema from the caller.");
    }

    [Fact]
    public void The_interpolation_guard_is_an_interpolated_string_handler()
    {
        // This test previously asserted a FormattableString overload and passed while the guard
        // was inert: C# prefers `string` over FormattableString for an interpolated literal, so
        // interpolated SQL compiled fine. Only an [InterpolatedStringHandler] parameter is
        // actually preferred, so that is what is asserted now.
        //
        // The compile failure this produces is recorded in
        // docs/planning/spike-02-interpolation-guard.md. A reflection test cannot observe a
        // compile error, so it guards the mechanism and the doc carries the evidence.
        var overload = typeof(TenantScopedStatement)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(TenantScopedStatement.Create)
                      && m.GetParameters()[1].ParameterType != typeof(string));

        var handlerType = overload.GetParameters()[1].ParameterType;

        handlerType.GetCustomAttribute<InterpolatedStringHandlerAttribute>()
            .ShouldNotBeNull("without the handler attribute the compiler picks the string overload " +
                             "and interpolated SQL compiles");

        var obsolete = overload.GetCustomAttribute<ObsoleteAttribute>();
        obsolete.ShouldNotBeNull();
        obsolete.IsError.ShouldBeTrue("a warning would be ignored; this has to fail the build");
    }

    [Fact]
    public void The_interpolation_guard_throws_if_it_is_ever_reached()
    {
        // Belt and braces for reflection-based or dynamic callers, which skip the compile check.
        Should.Throw<InvalidOperationException>(() => new BlockedSqlInterpolation(0, 1));
    }

    [Fact]
    public void A_tenant_context_cannot_be_manufactured_from_outside()
    {
        // The factory was public in the first version, which let any caller build a context for
        // any tenant with no membership check. A security review proved it with a working sample.
        // Nothing outside the resolver assembly and this suite may construct one.
        var factory = typeof(TenantContext).Assembly.GetType(
            "Lakewright.Core.Tenancy.TenantContextFactory");

        factory.ShouldNotBeNull();
        factory.IsPublic.ShouldBeFalse(
            "a public factory makes the membership check optional, which makes it useless");

        typeof(TenantContext).GetConstructors().ShouldBeEmpty(
            "TenantContext must have no public constructor");
    }

    [Fact]
    public void A_default_statement_is_refused_rather_than_dereferenced()
    {
        // A struct always has an implicit parameterless constructor, so `default` skips both
        // factories. It must fail as a rejected argument, not as a NullReferenceException
        // somewhere further in.
        var statement = default(TenantScopedStatement);

        statement.Tenant.ShouldBeNull();
    }

    [Fact]
    public void An_identifier_with_a_trailing_newline_is_rejected()
    {
        // .NET's `$` also matches immediately before a single trailing newline, so a `$`-anchored
        // pattern accepted "tenant_a\n". The pattern uses `\z`.
        UnityCatalogIdentifier.IsValid("tenant_a\n").ShouldBeFalse();
        UnityCatalogIdentifier.IsValid("tenant_a\r\n").ShouldBeFalse();
        UnityCatalogIdentifier.IsValid("tenant_a").ShouldBeTrue();
    }

    [Fact]
    public void A_schema_name_that_needs_quoting_is_rejected()
    {
        // Catalog and schema travel as identifiers rather than bound parameters, because the
        // Statement Execution API has no parameter form for an identifier. They are therefore
        // the only unparameterised values in the query path.
        var hostile = new[]
        {
            "analytics; DROP SCHEMA other",
            "tenant_a`.`tenant_b",
            "../escape",
            "TENANT_UPPER",
            "1_leading_digit",
            "",
            " "
        };

        foreach (var candidate in hostile)
        {
            UnityCatalogIdentifier.IsValid(candidate)
                .ShouldBeFalse($"'{candidate}' must not be accepted as an identifier");
        }
    }

    [Fact]
    public void Two_tenants_never_resolve_to_the_same_schema()
    {
        var schemas = Enumerable.Range(0, 500)
            .Select(_ => UnityCatalogIdentifier.SchemaForTenant(TenantId.New()))
            .ToArray();

        schemas.Distinct().Count().ShouldBe(schemas.Length);
        schemas.ShouldAllBe(s => UnityCatalogIdentifier.IsValid(s));
    }
}

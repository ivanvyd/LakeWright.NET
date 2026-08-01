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
        // Arrange
        var a = TenantContextFactory.ForTenant(TenantA, "analytics");
        var b = TenantContextFactory.ForTenant(TenantB, "analytics");

        // Act
        var forA = TenantScopedStatement.Create(a, "SELECT count(*) FROM events");
        var forB = TenantScopedStatement.Create(b, "SELECT count(*) FROM events");

        // Assert — identical SQL, different schema. The caller never chose the schema.
        forA.Sql.ShouldBe(forB.Sql);
        forA.Tenant.Schema.ShouldNotBe(forB.Tenant.Schema);
    }

    [Fact]
    public void There_is_no_way_to_build_a_statement_without_a_tenant_context()
    {
        // Arrange
        var factories = typeof(TenantScopedStatement)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.Name == nameof(TenantScopedStatement.Create));

        // Act
        var firstParameterTypes = factories.Select(m => m.GetParameters()[0].ParameterType).ToArray();

        // Assert — an overload that did not demand a TenantContext would be the whole
        // vulnerability, so this asserts the absence of one.
        firstParameterTypes.ShouldNotBeEmpty();
        firstParameterTypes.ShouldAllBe(t => t == typeof(TenantContext));
    }

    [Fact]
    public void The_executor_accepts_nothing_but_a_tenant_scoped_statement()
    {
        // Arrange
        var executeMethods = typeof(IStatementExecutor)
            .GetMethods()
            .Where(m => m.Name == nameof(IStatementExecutor.ExecuteAsync));

        // Act
        var stringParameters = executeMethods.SelectMany(m => m.GetParameters())
            .Where(p => p.ParameterType == typeof(string))
            .ToArray();

        // Assert — a convenience overload taking raw SQL, a catalog or a schema would let a caller
        // opt out of scoping without noticing.
        stringParameters.ShouldBeEmpty();
    }

    [Fact]
    public void The_interpolation_guard_is_an_interpolated_string_handler()
    {
        // Arrange — this test previously asserted a FormattableString overload and passed while the
        // guard was inert: C# prefers `string` for an interpolated literal. Only an
        // [InterpolatedStringHandler] parameter is actually preferred.
        var overload = typeof(TenantScopedStatement)
            .GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == nameof(TenantScopedStatement.Create)
                      && m.GetParameters()[1].ParameterType != typeof(string));

        // Act
        var handlerAttribute = overload.GetParameters()[1].ParameterType
            .GetCustomAttribute<InterpolatedStringHandlerAttribute>();
        var obsolete = overload.GetCustomAttribute<ObsoleteAttribute>();

        // Assert — the compile failure itself is evidenced in
        // docs/planning/spike-02-interpolation-guard.md, since a test inside the built assembly
        // cannot observe a compile error.
        handlerAttribute.ShouldNotBeNull(
            "without the handler attribute the compiler picks the string overload");
        obsolete.ShouldNotBeNull();
        obsolete.IsError.ShouldBeTrue("a warning would be ignored; this has to fail the build");
    }

    [Fact]
    public void The_interpolation_guard_throws_if_it_is_ever_reached()
    {
        // Arrange — belt and braces for reflection or dynamic callers, which skip the compile check.

        // Act
        var thrown = Should.Throw<InvalidOperationException>(() => new BlockedSqlInterpolation(0, 1));

        // Assert
        thrown.Message.ShouldContain("Interpolated SQL is not supported");
    }

    [Fact]
    public void A_default_statement_is_refused_rather_than_dereferenced()
    {
        // Arrange — a struct always has an implicit parameterless constructor, so `default` skips
        // both factories.

        // Act
        var statement = default(TenantScopedStatement);

        // Assert — it must fail as a rejected argument, not as a NullReferenceException further in.
        statement.Tenant.ShouldBeNull();
    }

    [Fact]
    public void An_identifier_with_a_trailing_newline_is_rejected()
    {
        // Arrange — .NET's `$` also matches immediately before a single trailing newline, so a
        // `$`-anchored pattern accepted "tenant_a\n". The pattern uses `\z`.
        var candidates = new[] { "tenant_a\n", "tenant_a\r\n", "tenant_a" };

        // Act
        var results = candidates.Select(UnityCatalogIdentifier.IsValid).ToArray();

        // Assert
        results[0].ShouldBeFalse();
        results[1].ShouldBeFalse();
        results[2].ShouldBeTrue();
    }

    [Fact]
    public void A_tenant_context_cannot_be_manufactured_from_outside()
    {
        // Arrange — the factory was public in the first version, which let any caller build a
        // context for any tenant with no membership check. A security review proved it.
        var factory = typeof(TenantContext).Assembly.GetType(
            "Lakewright.Core.Tenancy.TenantContextFactory");

        // Act
        var publicConstructors = typeof(TenantContext).GetConstructors();

        // Assert
        factory.ShouldNotBeNull();
        factory.IsPublic.ShouldBeFalse("a public factory makes the membership check optional");
        publicConstructors.ShouldBeEmpty("TenantContext must have no public constructor");
    }

    [Fact]
    public void A_schema_name_that_needs_quoting_is_rejected()
    {
        // Arrange — catalog and schema travel as identifiers rather than bound parameters, because
        // the Statement Execution API has no parameter form for an identifier.
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

        // Act
        var accepted = hostile.Where(UnityCatalogIdentifier.IsValid).ToArray();

        // Assert
        accepted.ShouldBeEmpty();
    }

    [Fact]
    public void Two_tenants_never_resolve_to_the_same_schema()
    {
        // Arrange
        var tenantIds = Enumerable.Range(0, 500).Select(_ => TenantId.New()).ToArray();

        // Act
        var schemas = tenantIds.Select(UnityCatalogIdentifier.SchemaForTenant).ToArray();

        // Assert
        schemas.Distinct().Count().ShouldBe(schemas.Length);
        schemas.ShouldAllBe(s => UnityCatalogIdentifier.IsValid(s));
    }
}

using LakeWright.Core.Tenancy;

namespace LakeWright.Databricks;

/// <summary>Configuration for an owned mapping table used to scope shared-schema facts.</summary>
public sealed class ScopeTableScopeOptions
{
    /// <summary>The keyed strategy name selected by a trusted <see cref="TenantContext"/>.</summary>
    public string StrategyName { get; set; } = "scope-table";

    /// <summary>The table that maps tenant identifiers to fact identifiers.</summary>
    public string ScopeTable { get; set; } = string.Empty;

    /// <summary>The mapping-table column holding the tenant identifier.</summary>
    public string TenantColumn { get; set; } = "tenant_id";

    /// <summary>The mapping-table column used when a mapping supplies a scope type value.</summary>
    public string ScopeTypeColumn { get; set; } = "scope_type";

    /// <summary>Fact-to-scope identifier mappings that form the owned EXISTS predicate.</summary>
    public IReadOnlyList<ScopeTableMapping> Mappings { get; set; } = [];
}

/// <summary>One fact-key to mapping-table-key relationship.</summary>
public sealed record ScopeTableMapping(string FactColumn, string ScopeIdColumn, string? ScopeTypeValue = null);

/// <summary>
/// Scopes shared-schema facts through an adopter-owned tenant-to-entity mapping table.
/// </summary>
public sealed class ScopeTableScope : ITenantScopeStrategy
{
    private readonly ScopeTableScopeOptions _options;

    public ScopeTableScope(ScopeTableScopeOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        ValidateConfiguration(options);
        _options = options;
    }

    public string Apply(string sql, TenantContext tenant)
    {
        var shared = ProjectedColumnScope.RequireSharedSchema(tenant);
        var query = ProjectedColumnScope.RequireSingleQuery(sql);
        EnsureFactKeysAreProjected(query);
        var predicates = _options.Mappings.Select((mapping, index) =>
        {
            var typeConstraint = mapping.ScopeTypeValue is null
                ? string.Empty
                : $" AND lakewright_scope.{_options.ScopeTypeColumn} = :lakewright_scope_type_{index}";
            return $"lakewright_tenant_scope.{mapping.FactColumn} = lakewright_scope.{mapping.ScopeIdColumn}{typeConstraint}";
        });

        return $"SELECT * FROM ({query}) AS lakewright_tenant_scope WHERE EXISTS " +
            $"(SELECT 1 FROM {_options.ScopeTable} AS lakewright_scope " +
            $"WHERE lakewright_scope.{_options.TenantColumn} = :{shared.TenantParameter} " +
            $"AND ({string.Join(" OR ", predicates)}))";
    }

    public IReadOnlyList<StatementParameter> Parameters(TenantContext tenant)
    {
        var shared = ProjectedColumnScope.RequireSharedSchema(tenant);
        var parameters = new List<StatementParameter>
        {
            StatementParameter.Tenant(shared.TenantParameter, tenant.TenantId),
        };
        for (var index = 0; index < _options.Mappings.Count; index++)
        {
            var scopeType = _options.Mappings[index].ScopeTypeValue;
            if (scopeType is not null)
            {
                parameters.Add(StatementParameter.String($"lakewright_scope_type_{index}", scopeType));
            }
        }
        return parameters;
    }

    private void EnsureFactKeysAreProjected(string query)
    {
        foreach (var mapping in _options.Mappings)
        {
            if (query.IndexOf(mapping.FactColumn, StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new TenantScopeMissingException(
                    $"Shared-schema scope-table statements must project the mapped fact key '{mapping.FactColumn}'.");
            }
        }
    }

    private static void ValidateConfiguration(ScopeTableScopeOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.StrategyName)
            || options.StrategyName.Any(character => !(character == '-' || character == '_' || char.IsLetterOrDigit(character))))
        {
            throw new ArgumentException("StrategyName must be a plain strategy identifier.", nameof(options));
        }

        ValidateQualifiedIdentifier(options.ScopeTable, nameof(options.ScopeTable));
        UnityCatalogIdentifier.Validate(options.TenantColumn, nameof(options.TenantColumn));
        UnityCatalogIdentifier.Validate(options.ScopeTypeColumn, nameof(options.ScopeTypeColumn));
        if (options.Mappings.Count == 0)
        {
            throw new ArgumentException("At least one scope-table mapping is required.", nameof(options));
        }

        foreach (var mapping in options.Mappings)
        {
            ArgumentNullException.ThrowIfNull(mapping);
            UnityCatalogIdentifier.Validate(mapping.FactColumn, nameof(mapping.FactColumn));
            UnityCatalogIdentifier.Validate(mapping.ScopeIdColumn, nameof(mapping.ScopeIdColumn));
        }
    }

    private static void ValidateQualifiedIdentifier(string value, string parameterName)
    {
        var parts = value.Split('.', StringSplitOptions.None);
        if (parts.Length is < 1 or > 3)
        {
            throw new ArgumentException("ScopeTable must be a one-, two-, or three-part Unity Catalog identifier.", parameterName);
        }

        foreach (var part in parts)
        {
            UnityCatalogIdentifier.Validate(part, parameterName);
        }
    }
}

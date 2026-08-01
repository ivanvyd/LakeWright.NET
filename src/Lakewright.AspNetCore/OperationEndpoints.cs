using System.Security.Claims;
using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;

namespace Lakewright.AspNetCore;

/// <summary>
/// The asynchronous operation API from ADR 0005: start one, then poll it.
/// </summary>
/// <remarks>
/// Every route sits under <c>/organizations/{organizationId}</c>, which is what makes the tenant
/// middleware run. An endpoint outside that prefix has no tenant and
/// <see cref="TenantContextAccessorExtensions.Required"/> throws rather than serving unscoped data.
/// </remarks>
public static class OperationEndpoints
{
    public static IEndpointRouteBuilder MapLakewrightOperations(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes
            .MapGroup($"/organizations/{{{TenantResolutionMiddleware.RouteValue}}}/operations")
            .WithTags("Operations");

        group.MapPost("/", StartAsync)
            .RequireAuthorization(TenantPolicies.Member)
            .WithName("StartOperation")
            .WithSummary("Starts a long-running analysis and returns where to poll for it.");

        group.MapGet("/{operationId:guid}", GetAsync)
            .RequireAuthorization(TenantPolicies.Viewer)
            .WithName("GetOperation")
            .WithSummary("Reads the state of an operation this organization owns.");

        return routes;
    }

    /// <summary>
    /// Accepts the work and returns immediately.
    /// </summary>
    /// <remarks>
    /// 202 rather than 200 because nothing has happened yet beyond a row being written. The
    /// Statement Execution API caps a synchronous wait at 50 seconds, so anything real is
    /// asynchronous whether the API admits it or not.
    /// </remarks>
    private static async Task<IResult> StartAsync(
        HttpContext http,
        ITenantContextAccessor tenants,
        OperationStore store,
        StartOperationRequest request,
        CancellationToken cancellationToken)
    {
        var tenant = tenants.Required();
        var principalId = PrincipalId(http);

        if (string.IsNullOrWhiteSpace(request?.Kind))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [nameof(StartOperationRequest.Kind)] = ["A kind is required."]
            });
        }

        var operation = await store.CreateAsync(tenant, principalId, request.Kind, cancellationToken);

        return Results.AcceptedAtRoute(
            "GetOperation",
            new { organizationId = tenant.TenantId.Value, operationId = operation.Id },
            OperationResponse.From(operation));
    }

    /// <summary>
    /// Reads an operation, or 404.
    /// </summary>
    /// <remarks>
    /// The lookup goes through <see cref="OperationStore"/>, which filters on the resolved tenant,
    /// so an identifier belonging to another organization is simply not found. That is why this
    /// returns 404 rather than 403: a 403 would confirm the identifier is real.
    /// </remarks>
    private static async Task<IResult> GetAsync(
        ITenantContextAccessor tenants,
        OperationStore store,
        Guid operationId,
        CancellationToken cancellationToken)
    {
        var operation = await store.FindAsync(tenants.Required(), operationId, cancellationToken);

        return operation is null
            ? Results.NotFound()
            : Results.Ok(OperationResponse.From(operation));
    }

    private static string PrincipalId(HttpContext http) =>
        http.User.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? http.User.FindFirstValue("sub")
        ?? throw new InvalidOperationException(
            "An authenticated principal reached a tenant endpoint without a subject claim.");
}

public sealed record StartOperationRequest(string Kind);

/// <summary>
/// What a customer sees.
/// </summary>
/// <remarks>
/// Carries the product-facing state and never the Databricks run identifier or the platform's own
/// error text. The run id is an internal correlation key, and exposing it would invite an endpoint
/// keyed on it, which is the cross-tenant read <see cref="OperationStore"/> exists to prevent.
/// </remarks>
public sealed record OperationResponse(
    Guid Id,
    string Kind,
    string State,
    DateTimeOffset CreatedAt,
    DateTimeOffset? CompletedAt)
{
    public static OperationResponse From(Operation operation) => new(
        operation.Id,
        operation.Kind,
        operation.State.ToString(),
        operation.CreatedAt,
        operation.CompletedAt);
}

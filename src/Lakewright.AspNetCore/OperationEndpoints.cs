using System.Security.Claims;
using Lakewright.Core.Tenancy;
using Lakewright.Multitenancy;
using Lakewright.Multitenancy.Model;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
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
    /// <summary>The header a caller sends to make a retried start safe.</summary>
    public const string IdempotencyKeyHeader = "Idempotency-Key";

    public static IEndpointRouteBuilder MapLakewrightOperations(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes
            .MapGroup($"/organizations/{{{TenantResolutionMiddleware.RouteValue}}}/operations")
            .WithTags("Operations")

            // The floor for anything added to this group later. Without it, an endpoint that
            // forgets its own RequireAuthorization falls through to the application's fallback
            // policy, which asks only for an authenticated user — so a Viewer would reach an
            // action meant for an Admin. Tenant resolution does not help there: it checks
            // membership, not role, so a Viewer at that organization resolves fine.
            .RequireAuthorization(TenantPolicies.Viewer);

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
    ///
    /// Send an <c>Idempotency-Key</c> and a retried POST returns the original operation instead of
    /// starting a second Databricks run. A replay answers 202 with the same body, because a caller
    /// that cannot distinguish the replay from the original is exactly the point.
    /// </remarks>
    private static async Task<IResult> StartAsync(
        HttpContext http,
        [FromServices] ITenantContextAccessor tenants,
        [FromServices] OperationStore store,
        [FromBody] StartOperationRequest request,
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

        if (!TryReadIdempotencyKey(http, out var clientRequestId))
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                [IdempotencyKeyHeader] =
                    [$"Must be 1 to {OperationStore.MaxClientRequestIdLength} characters."]
            });
        }

        var operation = await store.CreateAsync(
            tenant, principalId, request.Kind, clientRequestId, cancellationToken);

        // Returning the stored operation for a key the caller reused with different content would
        // answer a question it did not ask. RFC 9110 has no status for this; the Idempotency-Key
        // draft settles on 422, and a 409 would suggest retrying with the same key helps.
        if (!string.Equals(operation.Kind, request.Kind, StringComparison.Ordinal))
        {
            return Results.Problem(
                title: "Idempotency key reused",
                detail: $"This key already started an operation of kind '{operation.Kind}'.",
                statusCode: StatusCodes.Status422UnprocessableEntity);
        }

        return Results.AcceptedAtRoute(
            "GetOperation",
            new { organizationId = tenant.TenantId.Value, operationId = operation.Id },
            OperationResponse.From(operation));
    }

    private static bool TryReadIdempotencyKey(HttpContext http, out string? key)
    {
        key = null;

        if (!http.Request.Headers.TryGetValue(IdempotencyKeyHeader, out var values))
        {
            return true;
        }

        var value = values.ToString();

        if (string.IsNullOrWhiteSpace(value) || value.Length > OperationStore.MaxClientRequestIdLength)
        {
            return false;
        }

        key = value;
        return true;
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
        [FromServices] ITenantContextAccessor tenants,
        [FromServices] OperationStore store,
        [FromRoute] Guid operationId,
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

using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.AspNetCore;

public static class CostEndpoints
{
    public static IEndpointRouteBuilder MapLakeWrightCost(this IEndpointRouteBuilder routes)
    {
        ArgumentNullException.ThrowIfNull(routes);

        var group = routes
            .MapGroup($"/organizations/{{{TenantResolutionMiddleware.RouteValue}}}/cost")
            .WithTags("Cost")
            .RequireAuthorization(TenantPolicies.Viewer);

        group.MapGet("/", GetCostAsync)
            .WithName("GetCost")
            .WithSummary("Reports the tenant's compute consumption for the requested window.");

        return routes;
    }

    /// <summary>
    /// Returns a <see cref="TenantCostSummary"/> for the resolved tenant.
    /// </summary>
    /// <remarks>
    /// The window is bounded to 31 days to keep the application database from being asked to sum
    /// a multi-year range into a number nobody actually wants. The default window is the last
    /// 7 days, which is what a customer-facing usage page tends to render.
    /// </remarks>
    private static async Task<IResult> GetCostAsync(
        HttpContext http,
        [FromServices] ITenantContextAccessor tenants,
        [FromServices] ICostAttribution cost,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        var tenant = tenants.Required();
        var effectiveUntil = until ?? http.RequestServices
            .GetRequiredService<TimeProvider>()
            .GetUtcNow();
        var effectiveFrom = from ?? effectiveUntil.AddDays(-7);

        if (effectiveFrom >= effectiveUntil)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["from must be earlier than until."]
            });
        }

        // Reject windows that end in the distant future, so a caller cannot ask for a 30-day
        // window anchored at year 9999 and have the implementation scan a multi-millennium range.
        var maxUntil = DateTimeOffset.UtcNow.AddDays(1);
        if (effectiveUntil > maxUntil)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["until"] = ["until cannot be more than one day in the future."]
            });
        }

        if ((effectiveUntil - effectiveFrom).TotalDays > 31)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["Window cannot exceed 31 days."]
            });
        }

        try
        {
            var summary = await cost.ResolveAsync(
                tenant,
                effectiveFrom,
                effectiveUntil,
                cancellationToken);
            return Results.Ok(summary);
        }
        catch (BillingUsageException exception)
        {
            return Results.Problem(
                statusCode: StatusCodes.Status502BadGateway,
                title: "Billing usage is unavailable.",
                extensions: new Dictionary<string, object?>
                {
                    ["code"] = exception.Code,
                    ["transient"] = exception.IsTransient
                });
        }
    }
}

using LakeWright.Core.Cost;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

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
        [FromServices] ITenantContextAccessor tenants,
        [FromServices] ICostAttribution cost,
        [FromServices] TimeProvider timeProvider,
        [FromQuery] DateTimeOffset? from,
        [FromQuery] DateTimeOffset? until,
        CancellationToken cancellationToken)
    {
        var tenant = tenants.Required();
        var now = timeProvider.GetUtcNow();
        var effectiveUntil = until ?? now;
        var effectiveFrom = from ?? effectiveUntil.AddDays(-7);

        var validationError = ValidateWindow(effectiveFrom, effectiveUntil, now);
        if (validationError is not null)
        {
            return validationError;
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
            if (exception.Code == "REPORT_TOO_LARGE")
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status422UnprocessableEntity,
                    title: "Billing report is too large.",
                    detail: $"Narrow the window to at most {BillingUsageLimits.MaxJobRunsPerReport} distinct job runs.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = exception.Code,
                        ["maxJobRuns"] = BillingUsageLimits.MaxJobRunsPerReport
                    });
            }

            if (exception.Code == "BILLING_BUSY")
            {
                return Results.Problem(
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Billing usage is busy.",
                    detail: "Retry after an in-flight billing statement completes.",
                    extensions: new Dictionary<string, object?>
                    {
                        ["code"] = exception.Code,
                        ["transient"] = true
                    });
            }

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

    private static IResult? ValidateWindow(
        DateTimeOffset from,
        DateTimeOffset until,
        DateTimeOffset now)
    {
        try
        {
            BillingUsageLimits.ValidateReportWindow(from, until, now);
            return null;
        }
        catch (ArgumentException)
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] = ["from must be earlier than until."]
            });
        }
        catch (BillingUsageException exception) when (
            exception.Code == "REPORT_WINDOW_IN_FUTURE")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["until"] = ["until cannot be more than one day in the future."]
            });
        }
        catch (BillingUsageException exception) when (
            exception.Code == "REPORT_WINDOW_TOO_LARGE")
        {
            return Results.ValidationProblem(new Dictionary<string, string[]>
            {
                ["from"] =
                [$"Window cannot exceed {BillingUsageLimits.MaxReportWindowDays} days."]
            });
        }
    }
}

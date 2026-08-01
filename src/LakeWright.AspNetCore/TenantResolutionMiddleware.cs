using System.Security.Claims;
using LakeWright.Core.Tenancy;
using LakeWright.Multitenancy;
using LakeWright.Multitenancy.Model;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace LakeWright.AspNetCore;

/// <summary>
/// Turns the tenant identifier in a route into a resolved <see cref="TenantContext"/>, or a 404.
/// </summary>
/// <remarks>
/// The identifier in the URL is a claim the caller makes, not a fact. This is the one place it
/// becomes a fact, by asking the application database whether the authenticated principal is a
/// member. Nothing downstream re-checks, because nothing downstream can construct a
/// <see cref="TenantContext"/> without going through the resolver.
///
/// It answers 404, never 403. A 403 confirms the tenant exists, which is the difference between
/// refusing a request and confirming a guess.
///
/// Because the refusal is a 404, the audit row it writes is the only trace of the attempt. A
/// tenant probing for another tenant's identifiers looks like ordinary traffic in an access log.
/// </remarks>
public sealed class TenantResolutionMiddleware(RequestDelegate next)
{
    /// <summary>Route value carrying the tenant. Endpoints that omit it are not tenant-scoped.</summary>
    public const string RouteValue = "organizationId";

    public async Task InvokeAsync(
        HttpContext context,
        ITenantContextResolver resolver,
        LakeWrightDbContext db,
        AuditLog audit)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(db);
        ArgumentNullException.ThrowIfNull(audit);

        if (context.Request.RouteValues.TryGetValue(RouteValue, out var raw)
            && raw?.ToString() is { Length: > 0 } value)
        {
            if (!Guid.TryParse(value, out var parsed))
            {
                // A malformed identifier is indistinguishable from one that does not exist.
                await WriteNotFoundAsync(context);
                return;
            }

            var principalId = context.User.FindFirstValue(ClaimTypes.NameIdentifier)
                              ?? context.User.FindFirstValue("sub");

            if (string.IsNullOrEmpty(principalId))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return;
            }

            var tenant = await resolver.ResolveAsync(
                new TenantId(parsed), principalId, context.RequestAborted);

            if (tenant is null)
            {
                // Recorded against the tenant the caller asked for, not one it belongs to: the
                // question "who tried to reach Acme" is the one an incident asks.
                audit.Record(
                    new TenantId(parsed), principalId, AuditActions.TenantAccessDenied,
                    resourceType: nameof(Organization), resourceId: parsed.ToString());

                await db.SaveChangesAsync(context.RequestAborted);

                LakeWrightTelemetry.TenantAccessDenied.Add(1);
                await WriteNotFoundAsync(context);
                return;
            }

            context.Features.Set(tenant);
        }

        await next(context);
    }

    private static Task WriteNotFoundAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status404NotFound;
        return Task.CompletedTask;
    }
}

/// <summary>Reads the tenant resolved for the current request.</summary>
public sealed class HttpTenantContextAccessor(IHttpContextAccessor accessor) : ITenantContextAccessor
{
    public TenantContext? Current => accessor.HttpContext?.Features.Get<TenantContext>();
}

/// <summary>
/// Requires a resolved tenant, and says so at the point of use.
/// </summary>
/// <remarks>
/// An endpoint that reaches tenant data calls this rather than reading the accessor directly. If
/// the middleware did not resolve a tenant, that is a routing mistake — the endpoint is registered
/// on a path without the tenant segment — and it fails loudly here rather than quietly serving
/// something.
/// </remarks>
public static class TenantContextAccessorExtensions
{
    public static TenantContext Required(this ITenantContextAccessor accessor) =>
        accessor?.Current
        ?? throw new InvalidOperationException(
            $"No tenant resolved. The endpoint must sit under a route containing " +
            $"{{{TenantResolutionMiddleware.RouteValue}}}.");
}

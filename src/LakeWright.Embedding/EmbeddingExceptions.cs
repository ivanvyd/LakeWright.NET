using System.Net;
using LakeWright.Core;

namespace LakeWright.Embedding;

/// <summary>A workspace request could not reach its destination or complete in transit.</summary>
public sealed class TransportException(string message, Exception innerException)
    : LakeWrightException(message, innerException);

/// <summary>A Databricks workspace rejected a request or returned an invalid protocol response.</summary>
public sealed class WorkspaceRejectedException : LakeWrightException
{
    public WorkspaceRejectedException(HttpStatusCode statusCode, string bodyExcerpt)
        : base($"Databricks answered {(int)statusCode}: {bodyExcerpt}")
    {
        StatusCode = statusCode;
        BodyExcerpt = bodyExcerpt;
    }

    public HttpStatusCode StatusCode { get; }

    /// <summary>A bounded response excerpt suitable for diagnostics, never a full payload.</summary>
    public string BodyExcerpt { get; }
}

/// <summary>The requested dashboard has no published revision that can be embedded.</summary>
public sealed class NotPublishedException(string dashboardId, string bodyExcerpt)
    : LakeWrightException($"Dashboard '{dashboardId}' is not published: {bodyExcerpt}")
{
    public string DashboardId { get; } = dashboardId;

    public string BodyExcerpt { get; } = bodyExcerpt;
}

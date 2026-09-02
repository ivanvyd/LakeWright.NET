using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LakeWright.Core;
using LakeWright.Core.Jobs;
using LakeWright.Core.Tenancy;

namespace LakeWright.Embedding.Ops;

internal interface IJobsApi
{
    Task<long> ResolveJobIdAsync(RefreshJob job, CancellationToken cancellationToken);
    Task<IReadOnlyList<JobsRun>> ListRunsAsync(long jobId, bool activeOnly, CancellationToken cancellationToken);
    Task<long> RunNowAsync(long jobId, TenantContext tenant, string idempotencyToken, CancellationToken cancellationToken);
    Task<JobsRun> GetRunAsync(long runId, CancellationToken cancellationToken);
    void Invalidate(RefreshJob job);
}

internal sealed record JobsRun(
    long RunId,
    long JobId,
    RefreshRunState State,
    DateTimeOffset? StartedAt,
    DateTimeOffset? EndedAt,
    string? TenantId,
    IReadOnlyList<RefreshTaskStatus> Tasks,
    string? FailureReason);

internal sealed class DatabricksJobsApi(
    HttpClient http,
    IOpsTokenBroker tokens,
    DashboardRefreshOptions options,
    TimeProvider timeProvider) : IJobsApi
{
    private readonly object _cacheLock = new();
    private readonly Dictionary<string, (long Id, DateTimeOffset ExpiresAt)> _jobIds = new(StringComparer.Ordinal);

    public async Task<long> ResolveJobIdAsync(RefreshJob job, CancellationToken cancellationToken)
    {
        if (job.Id is { } id)
        {
            return id;
        }

        var name = job.Name!;
        lock (_cacheLock)
        {
            if (_jobIds.TryGetValue(name, out var cached) && cached.ExpiresAt > timeProvider.GetUtcNow())
            {
                return cached.Id;
            }
        }

        string? pageToken = null;
        do
        {
            var url = "api/2.2/jobs/list?limit=25" + (pageToken is null
                ? string.Empty
                : "&page_token=" + Uri.EscapeDataString(pageToken));
            using var payload = await SendJsonAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
            var root = payload.RootElement;
            if (root.TryGetProperty("jobs", out var jobs) && jobs.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in jobs.EnumerateArray())
                {
                    if (!TryJob(entry, out var candidateId, out var candidateName)
                        || !string.Equals(candidateName, name, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    lock (_cacheLock)
                    {
                        _jobIds[name] = (candidateId, timeProvider.GetUtcNow() + options.JobLookupCacheDuration);
                    }
                    return candidateId;
                }
            }

            pageToken = root.TryGetProperty("next_page_token", out var next)
                && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        throw new InvalidOperationException($"No Databricks job named '{name}' exists in the ops workspace.");
    }

    public async Task<IReadOnlyList<JobsRun>> ListRunsAsync(long jobId, bool activeOnly, CancellationToken cancellationToken)
    {
        var runs = new List<JobsRun>();
        string? pageToken = null;
        do
        {
            var url = $"api/2.2/jobs/runs/list?job_id={jobId}&active_only={activeOnly.ToString().ToLowerInvariant()}&completed_only=false&limit=25";
            if (pageToken is not null)
            {
                url += "&page_token=" + Uri.EscapeDataString(pageToken);
            }

            using var payload = await SendJsonAsync(HttpMethod.Get, url, null, cancellationToken).ConfigureAwait(false);
            var root = payload.RootElement;
            if (root.TryGetProperty("runs", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                runs.AddRange(items.EnumerateArray().Select(ParseRun));
            }

            pageToken = root.TryGetProperty("next_page_token", out var next)
                && next.ValueKind == JsonValueKind.String
                ? next.GetString()
                : null;
        }
        while (!string.IsNullOrWhiteSpace(pageToken));

        return runs;
    }

    public async Task<long> RunNowAsync(long jobId, TenantContext tenant, string idempotencyToken, CancellationToken cancellationToken)
    {
        var parameters = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [TenantScopedJobRun.TenantIdParameter] = tenant.TenantId.ToString(),
            [TenantScopedJobRun.CatalogParameter] = tenant.Catalog,
            [TenantScopedJobRun.SchemaParameter] = tenant.Schema,
        };
        using var payload = await SendJsonAsync(
            HttpMethod.Post,
            "api/2.2/jobs/run-now",
            JsonSerializer.SerializeToElement(new
            {
                job_id = jobId,
                job_parameters = parameters,
                idempotency_token = idempotencyToken,
            }),
            cancellationToken).ConfigureAwait(false);

        if (!payload.RootElement.TryGetProperty("run_id", out var runId) || !runId.TryGetInt64(out var value))
        {
            throw new InvalidOperationException("The Jobs API accepted run-now without returning run_id.");
        }

        return value;
    }

    public async Task<JobsRun> GetRunAsync(long runId, CancellationToken cancellationToken)
    {
        using var payload = await SendJsonAsync(HttpMethod.Get, $"api/2.2/jobs/runs/get?run_id={runId}", null, cancellationToken).ConfigureAwait(false);
        return ParseRun(payload.RootElement);
    }

    public void Invalidate(RefreshJob job)
    {
        if (job.Name is null)
        {
            return;
        }

        lock (_cacheLock)
        {
            _jobIds.Remove(job.Name);
        }
    }

    private async Task<JsonDocument> SendJsonAsync(
        HttpMethod method,
        string relativeUrl,
        JsonElement? body,
        CancellationToken cancellationToken)
    {
        var token = await tokens.AcquireAsync(cancellationToken).ConfigureAwait(false);
        using var request = new HttpRequestMessage(method, relativeUrl);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.AccessToken);
        if (body is { } content)
        {
            request.Content = JsonContent.Create(content);
        }

        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var responseBody = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new JobsApiException(response.StatusCode, responseBody.Length <= 1024 ? responseBody : responseBody[..1024]);
        }

        return JsonDocument.Parse(responseBody);
    }

    private static bool TryJob(JsonElement entry, out long id, out string? name)
    {
        id = 0;
        name = null;
        if (!entry.TryGetProperty("job_id", out var idProperty) || !idProperty.TryGetInt64(out id))
        {
            return false;
        }

        if (entry.TryGetProperty("settings", out var settings)
            && settings.ValueKind == JsonValueKind.Object
            && settings.TryGetProperty("name", out var nameProperty)
            && nameProperty.ValueKind == JsonValueKind.String)
        {
            name = nameProperty.GetString();
        }

        return !string.IsNullOrWhiteSpace(name);
    }

    internal static JobsRun ParseRun(JsonElement element)
    {
        var id = element.TryGetProperty("run_id", out var runId) && runId.TryGetInt64(out var runValue)
            ? runValue
            : throw new InvalidOperationException("A Jobs API run omitted run_id.");
        var jobId = element.TryGetProperty("job_id", out var job) && job.TryGetInt64(out var jobValue) ? jobValue : 0;
        var state = ParseState(
            element.TryGetProperty("status", out var statusElement) ? statusElement :
            element.TryGetProperty("state", out var stateElement) ? stateElement : default,
            out var reason);
        var tenantId = FindTenantParameter(element);
        var tasks = element.TryGetProperty("tasks", out var taskItems) && taskItems.ValueKind == JsonValueKind.Array
            ? taskItems.EnumerateArray().Select(ParseTask).ToArray()
            : Array.Empty<RefreshTaskStatus>();
        return new JobsRun(id, jobId, state, UnixMilliseconds(element, "start_time"), UnixMilliseconds(element, "end_time"), tenantId, tasks, reason);
    }

    private static RefreshTaskStatus ParseTask(JsonElement task)
    {
        var key = task.TryGetProperty("task_key", out var taskKey) && taskKey.ValueKind == JsonValueKind.String
            ? taskKey.GetString() ?? string.Empty
            : string.Empty;
        var state = ParseState(
            task.TryGetProperty("status", out var taskStatus) ? taskStatus :
            task.TryGetProperty("state", out var taskState) ? taskState : default,
            out var reason);
        return new RefreshTaskStatus(key, state, reason);
    }

    private static RefreshRunState ParseState(JsonElement state, out string? failureReason)
    {
        failureReason = null;
        var lifecycle = ReadString(state, "life_cycle_state") ?? ReadString(state, "state");
        var result = ReadString(state, "result_state")
            ?? (state.ValueKind == JsonValueKind.Object && state.TryGetProperty("termination_details", out var termination)
                ? ReadString(termination, "code")
                : null);
        // Databricks state_message can include a SQL fragment or user-supplied parameter values.
        // Preserve a safe summary for portal callers; operators can inspect the workspace run.
        failureReason = result is "SUCCESS" or null ? null : "The job run reported a terminal failure.";

        return lifecycle switch
        {
            "PENDING" or "QUEUED" or "BLOCKED" or "WAITING" => RefreshRunState.Queued,
            "RUNNING" or "TERMINATING" => RefreshRunState.Running,
            "TERMINATED" when result is "SUCCESS" => RefreshRunState.Succeeded,
            "TERMINATED" when result is "CANCELED" or "CANCELLED" => RefreshRunState.Cancelled,
            "TERMINATED" => RefreshRunState.Failed,
            _ => RefreshRunState.Running,
        };
    }

    private static string? ReadString(JsonElement element, string propertyName) =>
        element.ValueKind == JsonValueKind.Object
        && element.TryGetProperty(propertyName, out var property)
        && property.ValueKind == JsonValueKind.String
            ? property.GetString()
            : null;

    private static string? FindTenantParameter(JsonElement run)
    {
        if (run.TryGetProperty("job_parameters", out var jobParameters))
        {
            if (jobParameters.ValueKind == JsonValueKind.Object
                && jobParameters.TryGetProperty(TenantScopedJobRun.TenantIdParameter, out var tenant)
                && tenant.ValueKind == JsonValueKind.String)
            {
                return tenant.GetString();
            }

            if (jobParameters.ValueKind == JsonValueKind.Array)
            {
                foreach (var parameter in jobParameters.EnumerateArray())
                {
                    if (parameter.TryGetProperty("name", out var name)
                        && name.GetString() == TenantScopedJobRun.TenantIdParameter
                        && parameter.TryGetProperty("value", out var value)
                        && value.ValueKind == JsonValueKind.String)
                    {
                        return value.GetString();
                    }
                }
            }
        }

        if (run.TryGetProperty("overriding_parameters", out var overrides)
            && overrides.ValueKind == JsonValueKind.Object
            && overrides.TryGetProperty("job_parameters", out var overridingJobParameters)
            && overridingJobParameters.ValueKind == JsonValueKind.Object
            && overridingJobParameters.TryGetProperty(TenantScopedJobRun.TenantIdParameter, out var overridingTenant)
            && overridingTenant.ValueKind == JsonValueKind.String)
        {
            return overridingTenant.GetString();
        }

        return null;
    }

    private static DateTimeOffset? UnixMilliseconds(JsonElement element, string name) =>
        element.TryGetProperty(name, out var time) && time.TryGetInt64(out var milliseconds) && milliseconds > 0
            ? DateTimeOffset.FromUnixTimeMilliseconds(milliseconds)
            : null;
}

internal sealed class JobsApiException(HttpStatusCode statusCode, string bodyExcerpt)
    : LakeWrightException($"Databricks Jobs answered {(int)statusCode}.")
{
    public HttpStatusCode StatusCode { get; } = statusCode;

    public string BodyExcerpt { get; } = bodyExcerpt;

    public bool IsMissingJob => StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest
        && BodyExcerpt.Contains("does not exist", StringComparison.OrdinalIgnoreCase);
}

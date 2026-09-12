using System.Text.Json;
using System.Text.Json.Serialization;
using Azure.Core;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using WireMock.ResponseBuilders;
using WireMock.Server;
using Request = WireMock.RequestBuilders.Request;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class StatementResultContractTests : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
    private readonly WireMockServer _workspace = WireMockServer.Start();
    private readonly WireMockServer _storage = WireMockServer.Start();

    [Fact]
    public async Task Inline_success_contains_every_chunk_in_order()
    {
        Initial(totalRows: 3, chunks: 3, Inline(0, "one", 1));
        Chunk(1, Inline(1, "two", 2));
        Chunk(2, Inline(2, "three", null));
        var outcome = (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.Success>();
        outcome.Rows.Select(row => row[0]).ShouldBe(["one", "two", "three"]);
        outcome.TotalRowCount.ShouldBe(3);
    }

    [Fact]
    public async Task A_link_only_continuation_uses_the_trusted_numeric_chunk_endpoint()
    {
        Initial(2, 2, new
        {
            chunk_index = 0,
            row_offset = 0,
            row_count = 1,
            data_array = WireRows("one"),
            next_chunk_internal_link = "https://untrusted.invalid/credential"
        });
        Chunk(1, Inline(1, "two", null));
        (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.Success>().Rows.Count.ShouldBe(2);
        _workspace.LogEntries.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Server_truncation_is_an_explicit_failure_before_any_chunk_download(bool external)
    {
        Initial(1, 1, external ? External(0, 1, null) : Inline(0, "one", null), truncated: true);
        var failure = (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.Failure>();
        failure.ErrorCode.ShouldBe("RESULT_TRUNCATED");
        failure.IsTruncated.ShouldBeTrue();
        _storage.LogEntries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 2)]
    public async Task An_incomplete_or_repeated_inline_chunk_cannot_be_success(int secondIndex, int secondOffset)
    {
        Initial(2, 2, Inline(0, "one", 1));
        Chunk(1, new
        {
            chunk_index = secondIndex,
            row_offset = secondOffset,
            row_count = 1,
            data_array = WireRows("two")
        });
        (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.Failure>().ErrorCode.ShouldBe("RESULT_INCOMPLETE");
    }

    [Fact]
    public async Task Missing_inline_continuation_does_not_hide_missing_rows()
    {
        Initial(2, 2, Inline(0, "one", null));
        (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.Failure>().ErrorCode.ShouldBe("RESULT_INCOMPLETE");
    }

    [Fact]
    public async Task A_zero_chunk_row_count_cannot_hide_a_nonempty_chunk()
    {
        Initial(1, 1, new { chunk_index = 0, row_offset = 0, row_count = 0, data_array = WireRows("one") });
        (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.Failure>().ErrorCode.ShouldBe("RESULT_INCOMPLETE");
    }

    [Fact]
    public async Task Inline_materialization_has_a_finite_storage_budget()
    {
        Initial(1, 1, Inline(0, new string('x', 13 * 1024 * 1024), null));
        (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.Failure>().ErrorCode.ShouldBe("RESULT_LIMIT_EXCEEDED");
    }

    [Fact]
    public async Task A_manifest_declaring_a_chunk_requires_result_metadata_even_when_empty()
    {
        Initial(0, 1, result: null);
        (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.Failure>().ErrorCode.ShouldBe("RESULT_INCOMPLETE");
    }

    [Fact]
    public async Task Exports_walk_three_external_pages_and_validate_all_rows()
    {
        Initial(3, 3, External(0, 1, 1));
        Chunk(1, External(1, 1, 2));
        Chunk(2, External(2, 1, null));
        Blob(0, """[["one"]]""");
        Blob(1, """[["λ"]]""");
        Blob(2, """[[null]]""");
        var rows = await ExportRows();
        rows.Count(row => row.Column is not null).ShouldBe(1);
        rows.Skip(1).Select(row => row.Values[0]).ShouldBe(["one", "λ", null]);
        _storage.LogEntries.ShouldAllBe(entry => entry.RequestMessage != null && entry.RequestMessage.Headers != null
            && !entry.RequestMessage.Headers.ContainsKey("Authorization"));
    }

    [Fact]
    public async Task An_expired_link_is_renewed_by_index_before_fetch()
    {
        Initial(1, 1, External(0, 1, null, expiration: DateTimeOffset.UtcNow.AddMinutes(-1), suffix: "expired"));
        Chunk(0, External(0, 1, null));
        Blob(0, """[["renewed"]]""");
        var metadata = (await Executor().ExecuteAsync(Statement(), TestContext.Current.CancellationToken))
            .ShouldBeOfType<StatementOutcome.LargeResult>();
        metadata.Chunks[0].ExpiresAt.ShouldNotBeNull();
        metadata.Chunks[0].ExpiresAt.GetValueOrDefault().ShouldBeLessThan(DateTimeOffset.UtcNow);
        (await ExportRows())[1].Values[0].ShouldBe("renewed");
        _storage.LogEntries.Count.ShouldBe(1);
        _storage.LogEntries[0].RequestMessage?.Path.ShouldBe("/chunk-0");
    }

    [Fact]
    public async Task A_forbidden_blob_is_renewed_once_before_rows_are_emitted()
    {
        Initial(1, 1, External(0, 1, null, suffix: "rejected"));
        _storage.Given(Request.Create().WithPath("/chunk-0rejected").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(403));
        Chunk(0, External(0, 1, null));
        Blob(0, """[["renewed"]]""");
        (await ExportRows()).Count.ShouldBe(2);
        _storage.LogEntries.Count.ShouldBe(2);
    }

    [Theory]
    [InlineData(2, 1, null)]
    [InlineData(1, 2, null)]
    [InlineData(1, 1, 2)]
    public async Task Inconsistent_external_counts_or_continuation_fail_the_export(int totalRows, int rowCount, int? next)
    {
        Initial(totalRows, 1, External(0, rowCount, next));
        Blob(0, """[["one"]]""");
        await Should.ThrowAsync<InvalidDataException>(ExportRows);
    }

    [Fact]
    public async Task A_failed_later_blob_does_not_end_as_a_successful_partial_export()
    {
        Initial(2, 2, External(0, 1, 1));
        Blob(0, """[["one"]]""");
        Chunk(1, External(1, 1, null));
        _storage.Given(Request.Create().WithPath("/chunk-1").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("private-response"));
        var error = await Should.ThrowAsync<HttpRequestException>(ExportRows);
        error.Message.ShouldNotContain("private-response");
        error.Message.ShouldNotContain("chunk-1");
    }

    private void Initial(long totalRows, int chunks, object? result, bool truncated = false) =>
        _workspace.Given(Request.Create().WithPath("/api/2.0/sql/statements").UsingPost())
            .RespondWith(Json(new
            {
                statement_id = "statement-1",
                status = new { state = "SUCCEEDED" },
                manifest = new
                {
                    format = "JSON_ARRAY",
                    schema = new { columns = new[] { new { name = "value" } } },
                    total_row_count = totalRows,
                    total_chunk_count = chunks,
                    truncated
                },
                result
            }));

    private void Chunk(int index, object result) =>
        _workspace.Given(Request.Create().WithPath($"/api/2.0/sql/statements/statement-1/result/chunks/{index}").UsingGet())
            .RespondWith(Json(result));

    private void Blob(int index, string body) =>
        _storage.Given(Request.Create().WithPath($"/chunk-{index}").UsingGet())
            .RespondWith(Response.Create().WithHeader("Content-Type", "application/json").WithBody(body));

    private object External(int index, long rowCount, int? next, DateTimeOffset? expiration = null, string suffix = "") =>
        new
        {
            external_links = new[] { new { chunk_index = index, row_offset = index, row_count = rowCount,
            next_chunk_index = next, external_link = $"{_storage.Urls[0]}/chunk-{index}{suffix}",
            expiration = expiration ?? DateTimeOffset.UtcNow.AddMinutes(10) } }
        };

    private static object Inline(int index, string value, int? next) =>
        new { chunk_index = index, row_offset = index, row_count = 1, data_array = WireRows(value), next_chunk_index = next };

    private static string[][] WireRows(string value) => [[value]];

    private static IResponseBuilder Json(object body) => Response.Create().WithHeader("Content-Type", "application/json")
        .WithBody(JsonSerializer.Serialize(body, JsonOptions));

    private DatabricksStatementExecutor Executor() => new(Client(), Options.Create(new DatabricksOptions { WarehouseId = "warehouse" }),
        NullLogger<DatabricksStatementExecutor>.Instance);

    private DatabricksClient Client() => DatabricksClient.CreateClient(_workspace.Urls[0], new Credential());

    private static TenantScopedStatement Statement() => TenantScopedStatement.Create(
        TenantContextFactory.ForTenant(TenantId.New(), "analytics"), "SELECT value FROM results");

    private async Task<List<ExportRow>> ExportRows()
    {
        using var http = new HttpClient();
        var exporter = new DatabricksTenantScopedExport(Client(), Options.Create(new DatabricksOptions { WarehouseId = "warehouse" }),
            http, NullLogger<DatabricksTenantScopedExport>.Instance);
        var rows = new List<ExportRow>();
        await foreach (var row in exporter.StreamAsync(Statement(), TestContext.Current.CancellationToken)) { rows.Add(row); }
        return rows;
    }

    public void Dispose() { _workspace.Dispose(); _storage.Dispose(); }

    private sealed class Credential : TokenCredential
    {
        public override AccessToken GetToken(TokenRequestContext requestContext, CancellationToken cancellationToken) => new("synthetic", DateTimeOffset.UtcNow.AddHours(1));
        public override ValueTask<AccessToken> GetTokenAsync(TokenRequestContext requestContext, CancellationToken cancellationToken) => ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }
}

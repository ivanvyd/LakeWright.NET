using System.Diagnostics.Metrics;
using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using Microsoft.Azure.Databricks.Client.Models;
using Microsoft.Extensions.Options;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// Tests for the chunked export surface: a stream of <see cref="ExportRow"/>s that walks
/// a statement's presigned external-links chunks without buffering the whole result.
/// </summary>
/// <remarks>
/// <para>
/// The unit tests in this file exercise the export's chunk-fetch path against a fake
/// presigned server. The fake mirrors the workspace's chunk shape — a JSON envelope
/// carrying a <c>data_array</c> — so the parser is tested with the same payload format
/// the real warehouse returns.
/// </para>
/// <para>
/// The Databricks client itself is exercised by <c>LiveDatabricksTests</c>, which need
/// a real workspace and a tenant. This file proves the streaming and parsing logic;
/// that suite proves the request shape.
/// </para>
/// </remarks>
[Trait("Category", "TenantIsolation")]
#pragma warning disable CA1861 // Constant array assertions in Shouldly are fine — they're test fixtures, not hot-path allocations.
public class TenantScopedExportTests : IDisposable
{
    private readonly WireMockServer _workspace = WireMockServer.Start();
    private readonly WireMockServer _chunks = WireMockServer.Start();

    public void Dispose()
    {
        _workspace.Stop();
        _workspace.Dispose();
        _chunks.Stop();
        _chunks.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task The_export_streams_the_column_header_first()
    {
        // Arrange — the fake workspace answers a successful statement with one chunk link,
        // and the chunk server answers with two rows.
        StubStatementExecution(
            chunks: [$"{_chunks.Urls[0]}/chunk-0.json"],
            columns: ["id", "name"]);
        StubChunk("""{"data_array":[[1,"alpha"],[2,"beta"]]}""");

        var export = NewExport();

        // Act
        var rows = new List<ExportRow>();
        await foreach (var row in export.StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        // Assert
        rows.Count.ShouldBe(3);
        rows[0].Column.ShouldNotBeNull();
        rows[0].Column!.Columns.ShouldBe(new[] { "id", "name" });
        rows[0].Values.ShouldBeEmpty();
    }

    [Fact]
    public async Task The_export_yields_every_row_from_every_chunk_in_order()
    {
        // Arrange — two chunks, two rows each. The order is the order of links in the response.
        StubStatementExecution(
            chunks: [$"{_chunks.Urls[0]}/chunk-0.json", $"{_chunks.Urls[0]}/chunk-1.json"],
            columns: ["n"]);
        _chunks
            .Given(Request.Create().WithPath("/chunk-0.json").UsingGet())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"data_array":[[1],[2]]}"""));
        _chunks
            .Given(Request.Create().WithPath("/chunk-1.json").UsingGet())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody("""{"data_array":[[3],[4]]}"""));

        var export = NewExport();

        // Act
        var rows = new List<ExportRow>();
        await foreach (var row in export.StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        // Assert — header + 4 rows.
        rows.Count.ShouldBe(5);
        var data = rows.Skip(1).Select(r => r.Values[0]).ToArray();
        data.ShouldBe(new[] { "1", "2", "3", "4" });
    }

    [Fact]
    public async Task The_export_handles_null_cells()
    {
        // Arrange — nulls come through as JSON null, and the export's cell-mapper turns them
        // into C# nulls.
        StubStatementExecution(
            chunks: [$"{_chunks.Urls[0]}/chunk-0.json"],
            columns: ["id", "note"]);
        StubChunk("""{"data_array":[[1,null],[2,"x"]]}""");

        var export = NewExport();

        // Act
        var rows = new List<ExportRow>();
        await foreach (var row in export.StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        // Assert
        rows[1].Values[0].ShouldBe("1");
        rows[1].Values[1].ShouldBeNull();
        rows[2].Values[1].ShouldBe("x");
    }

    [Fact]
    public async Task The_export_handles_a_chunk_without_data_array_as_empty()
    {
        // Arrange — the chunk envelope can be missing the data_array key (e.g. an empty
        // chunk). The export must not crash.
        StubStatementExecution(
            chunks: [$"{_chunks.Urls[0]}/chunk-0.json"],
            columns: ["id"]);
        StubChunk("""{}""");

        var export = NewExport();

        // Act
        var rows = new List<ExportRow>();
        await foreach (var row in export.StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        // Assert — header only.
        rows.Count.ShouldBe(1);
        rows[0].Column.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_export_yields_only_the_header_when_there_are_no_rows()
    {
        StubStatementExecution(
            chunks: [$"{_chunks.Urls[0]}/chunk-0.json"],
            columns: ["id"]);
        StubChunk("""{"data_array":[]}""");

        var export = NewExport();

        // Act
        var rows = new List<ExportRow>();
        await foreach (var row in export.StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        // Assert
        rows.Count.ShouldBe(1);
        rows[0].Column.ShouldNotBeNull();
    }

    [Fact]
    public async Task The_export_propagates_typed_cell_values_as_strings()
    {
        // Arrange — booleans and numbers come back as their JSON forms. The export is
        // string-typed because the goal is a stream of CSV-shaped rows; the caller
        // knows which columns are numbers from the header.
        StubStatementExecution(
            chunks: [$"{_chunks.Urls[0]}/chunk-0.json"],
            columns: ["ok", "n"]);
        StubChunk("""{"data_array":[[true,42],[false,0]]}""");

        var export = NewExport();

        // Act
        var rows = new List<ExportRow>();
        await foreach (var row in export.StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        // Assert
        rows[1].Values[0].ShouldBe("true");
        rows[1].Values[1].ShouldBe("42");
        rows[2].Values[0].ShouldBe("false");
        rows[2].Values[1].ShouldBe("0");
    }

    [Fact]
    public async Task The_export_throws_when_a_chunk_fetch_fails()
    {
        // Arrange — the chunk server answers 500. The export must surface this as an
        // HttpRequestException so the caller knows the export is incomplete.
        StubStatementExecution(
            chunks: [$"{_chunks.Urls[0]}/chunk-0.json"],
            columns: ["id"]);
        _chunks
            .Given(Request.Create().WithPath("/chunk-0.json").UsingGet())
            .RespondWith(Response.Create().WithStatusCode(500).WithBody("boom"));

        var export = NewExport();

        // Act / Assert
        await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in export.StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
            {
                // drain
            }
        });
    }

    [Fact]
    public async Task The_export_throws_when_the_statement_is_rejected()
    {
        // Arrange — the workspace itself returns 401 (no token, etc). The export surfaces
        // an HttpRequestException carrying the status code.
        _workspace
            .Given(Request.Create().WithPath("/api/2.0/sql/statements").UsingPost())
            .RespondWith(Response.Create().WithStatusCode(401).WithBody("no auth"));

        var export = NewExport();

        // Act / Assert
        var ex = await Should.ThrowAsync<HttpRequestException>(async () =>
        {
            await foreach (var _ in export.StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
            {
                // drain
            }
        });
        ex.StatusCode.ShouldNotBeNull();
    }

    [Fact]
    public async Task A_pending_export_is_polled_to_external_links_within_its_budget()
    {
        StubChunk("""{"data_array":[[1]]}""");
        var session = new SequencedSession(
            new StatementOutcome.Pending("statement-1"),
            new StatementOutcome.LargeResult(
                ["id"],
                [new Uri($"{_chunks.Urls[0]}/chunk-0.json")],
                1,
                "statement-1"));
        var export = NewExport(session);
        var statement = TenantScopedStatement.Create(
            TenantContextFactory.ForTenant(TenantId.New(), "analytics"),
            "SELECT id FROM customers",
            new StatementOptions
            {
                PollInterval = TimeSpan.FromMilliseconds(1),
                TotalBudget = TimeSpan.FromSeconds(1),
            });

        var rows = new List<ExportRow>();
        await foreach (var row in export.StreamAsync(statement, TestContext.Current.CancellationToken))
        {
            rows.Add(row);
        }

        session.GetCalls.ShouldBe(1);
        session.Request!.OnWaitTimeout.ShouldBe(SqlStatementOnWaitTimeout.CONTINUE);
        rows.Select(row => row.Values.Count == 0 ? null : row.Values[0]).ShouldBe([null, "1"]);
    }

    [Fact]
    public async Task Streaming_an_export_emits_row_and_response_byte_metrics()
    {
        StubStatementExecution(
            chunks: [$"{_chunks.Urls[0]}/chunk-0.json"],
            columns: ["id"]);
        StubChunk("""{"data_array":[[1],[2]]}""");
        var measurements = new List<(string Name, long Value)>();
        using var listener = new MeterListener();
        listener.InstrumentPublished = (instrument, meterListener) =>
        {
            if (instrument.Meter.Name == LakeWrightDatabricksTelemetry.MeterName
                && instrument.Name.StartsWith("lakewright.exports.", StringComparison.Ordinal))
            {
                meterListener.EnableMeasurementEvents(instrument);
            }
        };
        listener.SetMeasurementEventCallback<long>((instrument, measurement, _, _) =>
            measurements.Add((instrument.Name, measurement)));
        listener.Start();

        await foreach (var _ in NewExport().StreamAsync(SampleStatement(), TestContext.Current.CancellationToken))
        {
            // Drain the stream so each row and its source chunk reaches the instrumentation.
        }
        listener.Dispose();

        measurements.Where(measurement => measurement.Name == "lakewright.exports.rows")
            .Sum(measurement => measurement.Value).ShouldBeGreaterThanOrEqualTo(2);
        measurements.Where(measurement => measurement.Name == "lakewright.exports.bytes")
            .Sum(measurement => measurement.Value).ShouldBeGreaterThan(0);
    }

    private void StubStatementExecution(IReadOnlyList<string> chunks, IReadOnlyList<string> columns)
    {
        var manifest = new
        {
            schema = new { columns = columns.Select(c => new { name = c }).ToArray() },
            total_row_count = 0L,
            truncated = false
        };
        var result = new
        {
            external_links = chunks.Select((uri, i) => new
            {
                external_link = uri,
                chunk_index = i,
                row_count = 0L,
                byte_count = 0L
            }).ToArray()
        };
        var status = new
        {
            state = "SUCCEEDED",
            error = (object?)null
        };
        var statementId = $"stmt-{Guid.NewGuid():N}";
        _workspace
            .Given(Request.Create().WithPath("/api/2.0/sql/statements").UsingPost())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody(System.Text.Json.JsonSerializer.Serialize(new
                {
                    statement_id = statementId,
                    status,
                    manifest,
                    result
                })));
    }

    private void StubChunk(string body)
    {
        _chunks
            .Given(Request.Create().WithPath("/chunk-0.json").UsingGet())
            .RespondWith(Response.Create()
                .WithHeader("Content-Type", "application/json")
                .WithBody(body));
    }

    private DatabricksTenantScopedExport NewExport(IDatabricksStatementSession? session = null)
    {
        // The Databricks client talks to the fake workspace. The HttpClient talks to
        // the fake chunk server. Their BaseAddress pins the requests to the right place.
        var client = Microsoft.Azure.Databricks.Client.DatabricksClient.CreateClient(
            _workspace.Urls[0],
            new DummyCredential());
        var options = Options.Create(new DatabricksOptions
        {
            WorkspaceUrl = _workspace.Urls[0],
            WarehouseId = "warehouse-1",
            WaitTimeout = "30s",
            Disposition = SqlStatementDisposition.EXTERNAL_LINKS,
            InlineRowLimit = 10_000,
        });
        // The export is a typed HttpClient — DI would normally supply the typed client. We
        // build one directly so the test stands on its own.
        var chunkHttp = new HttpClient();
        var logger = Microsoft.Extensions.Logging.Abstractions.NullLogger<DatabricksTenantScopedExport>.Instance;
        return session is null
            ? new DatabricksTenantScopedExport(client, options, chunkHttp, logger)
            : new DatabricksTenantScopedExport(session, options.Value, chunkHttp, logger);
    }

    private static TenantScopedStatement SampleStatement()
    {
        // The export only reads statement.Sql/Parameters/Tenant. A real call site has a
        // resolved TenantContext; here we build one through the internal factory the
        // resolvers use.
        var tenant = TenantContextFactory.ForTenant(TenantId.New(), "analytics");
        return TenantScopedStatement.Create(tenant, "SELECT id, name FROM customers");
    }

    private sealed class DummyCredential : Azure.Core.TokenCredential
    {
        public override Azure.Core.AccessToken GetToken(Azure.Core.TokenRequestContext requestContext, System.Threading.CancellationToken cancellationToken) =>
            new("dummy-token", DateTimeOffset.UtcNow.AddHours(1));

        public override async ValueTask<Azure.Core.AccessToken> GetTokenAsync(Azure.Core.TokenRequestContext requestContext, System.Threading.CancellationToken cancellationToken) =>
            await ValueTask.FromResult(GetToken(requestContext, cancellationToken));
    }

    private sealed class SequencedSession(params StatementOutcome[] outcomes) : IDatabricksStatementSession
    {
        private readonly Queue<StatementOutcome> _outcomes = new(outcomes);

        public int GetCalls { get; private set; }

        public SqlStatement? Request { get; private set; }

        public Task<StatementOutcome> ExecuteAsync(SqlStatement request, TenantId tenantId, CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(_outcomes.Dequeue());
        }

        public Task<StatementOutcome> GetAsync(TenantId tenantId, string statementId, CancellationToken cancellationToken)
        {
            GetCalls++;
            return Task.FromResult(_outcomes.Dequeue());
        }

        public Task CancelAsync(string statementId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}

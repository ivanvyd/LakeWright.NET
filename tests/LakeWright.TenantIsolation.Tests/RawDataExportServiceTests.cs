using LakeWright.Core.Tenancy;
using LakeWright.Databricks;
using LakeWright.Databricks.RawData;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class RawDataExportServiceTests
{
    [Fact]
    public async Task Returns_a_bounded_inline_csv_with_formula_safe_text_values()
    {
        var executor = new FakeExecutor(new StatementOutcome.Success(["name", "amount"], [["=danger", "42"]], 1, "statement-1"));
        var stream = new FakeExport();
        var service = Service(executor, stream, new RawDataOptions { ExportInlineRowCap = 2 });

        var result = await service.StartAsync(Tenant(), "owner-1", "export-1", Source(), new RawDataRequest(), TestContext.Current.CancellationToken);

        result.Mode.ShouldBe(RawDataExportMode.Inline);
        result.InlineCsv.ShouldBe(["\"name\",\"amount\"", "\"'=danger\",\"42\""]);
        stream.Calls.ShouldBe(0);
    }

    [Fact]
    public async Task Streams_large_exports_only_after_tenant_and_owner_authorization()
    {
        var tenant = Tenant();
        var executor = new FakeExecutor(new StatementOutcome.Success(["name"], [["one"], ["two"], ["three"]], 3, "statement-1"));
        var stream = new FakeExport();
        var service = Service(executor, stream, new RawDataOptions { ExportInlineRowCap = 2 });

        var start = await service.StartAsync(tenant, "owner-1", "export-2", Source(), new RawDataRequest(), TestContext.Current.CancellationToken);
        var lines = new List<string>();
        await foreach (var line in service.StreamCsvAsync(tenant, "owner-1", start.OperationId, TestContext.Current.CancellationToken))
        {
            lines.Add(line);
        }

        start.Mode.ShouldBe(RawDataExportMode.ExternalLinks);
        lines.ShouldBe(["\"name\"", "\"'=formula\""]);
        stream.Calls.ShouldBe(1);
        stream.Statement.Tenant.ShouldBeSameAs(tenant);
        stream.Statement.Options!.Disposition.ShouldBe(Microsoft.Azure.Databricks.Client.Models.SqlStatementDisposition.EXTERNAL_LINKS);

        await Should.ThrowAsync<UnauthorizedAccessException>(async () =>
        {
            await foreach (var _ in service.StreamCsvAsync(Tenant(), "owner-1", start.OperationId, TestContext.Current.CancellationToken)) { }
        });
    }

    private static RawDataExportService Service(FakeExecutor executor, FakeExport export, RawDataOptions options) => new(
        executor,
        export,
        new MemoryRawDataExportOwnership(),
        options);

    private static TenantContext Tenant() => TenantContextFactory.ForTenant(TenantId.New(), "analytics");

    private static RawDataSource Source() => new()
    {
        Name = "orders",
        BaseView = "orders_view",
        Fields =
        [
            new RawDataField { Name = "name", Column = "name", DisplayName = "Name", Kind = RawDataKind.Text, Filterable = true, Sortable = true },
            new RawDataField { Name = "amount", Column = "amount", DisplayName = "Amount", Kind = RawDataKind.Number, Filterable = true, Sortable = true },
        ],
        DefaultOrder = new RawDataSort("name"),
    };

    private sealed class FakeExecutor(StatementOutcome outcome) : IStatementExecutor
    {
        public Task<StatementOutcome> ExecuteAsync(TenantScopedStatement statement, CancellationToken cancellationToken) => Task.FromResult(outcome);
        public Task<StatementOutcome> GetAsync(TenantContext tenant, string statementId, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CancelAsync(TenantContext tenant, string statementId, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class FakeExport : ITenantScopedExport
    {
        public int Calls { get; private set; }
        public TenantScopedStatement Statement { get; private set; }

        public async IAsyncEnumerable<ExportRow> StreamAsync(TenantScopedStatement statement, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken)
        {
            Calls++;
            Statement = statement;
            yield return new ExportRow(new ExportColumn(["name"]), []);
            yield return new ExportRow(null, ["=formula"]);
            await Task.CompletedTask;
        }
    }
}

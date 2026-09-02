using LakeWright.Embedding;
using LakeWright.Embedding.Ops;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class DashboardFilterBindingValidatorTests
{
    [Fact]
    public async Task Validates_named_and_legacy_query_parameters_in_the_published_artifact()
    {
        var catalog = new Catalog();
        var validator = new DashboardFilterBindingValidator(catalog, new Reader("""{"datasets":[{"query":"SELECT * FROM orders WHERE date >= :from AND date < :until AND region = {{ region }}"}]}"""));

        await validator.ValidatePublishedAsync("dash-1",
        [
            new FilterBinding("start", "from", FilterBindingDateRole.RangeStart),
            new FilterBinding("end", "until", FilterBindingDateRole.RangeEnd),
            new FilterBinding("region", "region"),
        ], TestContext.Current.CancellationToken);

        catalog.Calls.ShouldBe(1);
    }

    [Fact]
    public async Task Refuses_a_binding_not_used_by_the_published_dashboard()
    {
        var validator = new DashboardFilterBindingValidator(new Catalog(), new Reader("""{"datasets":[{"queryLines":["SELECT * FROM orders WHERE date >= :from"]}]}"""));

        var exception = await Should.ThrowAsync<DashboardFilterBindingValidationException>(() => validator.ValidatePublishedAsync(
            "dash-1", [new FilterBinding("end", "until")], TestContext.Current.CancellationToken));

        exception.Message.ShouldContain("until");
    }

    private sealed class Catalog : IDashboardMetadataCatalog
    {
        public int Calls { get; private set; }

        public Task<DashboardDraftMetadata> GetDraftAsync(string dashboardId, CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<DashboardPublishedMetadata> GetPublishedAsync(string dashboardId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(new DashboardPublishedMetadata(dashboardId, "Dashboard", "warehouse", false, null));
        }

        public Task<IReadOnlyList<DashboardSummary>> ListAllAsync(CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class Reader(string serialized) : IPublishedDashboardDefinitionReader
    {
        public Task<string?> ReadAsync(string dashboardId, CancellationToken cancellationToken = default) => Task.FromResult<string?>(serialized);
    }
}

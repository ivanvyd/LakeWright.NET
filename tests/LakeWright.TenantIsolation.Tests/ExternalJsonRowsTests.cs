using System.Text;
using System.Text.Json;
using LakeWright.Databricks;

namespace LakeWright.TenantIsolation.Tests;

[Trait("Category", "TenantIsolation")]
public sealed class ExternalJsonRowsTests
{
    [Theory]
    [InlineData("{}")]
    [InlineData("{\"data_array\":[[\"one\"]]}")]
    [InlineData("[[1]]")]
    [InlineData("[[true]]")]
    [InlineData("[[{}]]")]
    [InlineData("[[\"one\"]")]
    public async Task Malformed_or_non_string_wire_cells_are_rejected(string json)
    {
        await Should.ThrowAsync<JsonException>(() => ReadAsync(json));
    }

    [Theory]
    [InlineData("[null]")]
    [InlineData("[[]]")]
    [InlineData("[[\"one\",\"two\"]]")]
    public async Task Rows_must_match_the_manifest_schema(string json)
    {
        await Should.ThrowAsync<InvalidDataException>(() => ReadAsync(json));
    }

    [Fact]
    public async Task A_large_single_row_hits_the_read_limit_before_unbounded_buffer_growth()
    {
        await Should.ThrowAsync<InvalidDataException>(() => ReadAsync("[[\"" + new string('x', 2 * 1024 * 1024) + "\"]]"));
    }

    [Fact]
    public async Task Many_small_rows_larger_than_the_read_limit_still_stream_completely()
    {
        var json = "[" + string.Join(',', Enumerable.Repeat("[\"0123456789\"]", 100_000)) + "]";
        (await ReadAsync(json)).ShouldBe(100_000);
    }

    private static async Task<int> ReadAsync(string json)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(json));
        var count = 0;
        await foreach (var row in ExternalJsonRows.ReadAsync(stream, 1, "test", TestContext.Current.CancellationToken)) { count++; }
        return count;
    }
}

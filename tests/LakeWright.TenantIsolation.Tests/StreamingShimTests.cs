using System.Text;
using System.Text.Json;
using LakeWright.AI;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The repair the streaming shim performs, checked against a chunk Databricks actually sent.
/// </summary>
/// <remarks>
/// The payloads below are copied from `databricks-claude-sonnet-5` on 2026-08-01, not invented.
/// A shim tested against a fixture someone wrote from the bug report tests the bug report.
/// </remarks>
public class StreamingShimTests
{
    // Captured verbatim. Every chunk but the last carries usage with completion_tokens null, which
    // is what the OpenAI deserialiser refuses.
    private const string IncompleteChunk =
        """data: {"id":"c1","choices":[{"delta":{"content":"hi"},"index":0}],"usage":{"cache_read_input_tokens":0,"completion_tokens":null,"prompt_tokens":9,"total_tokens":null,"cache_creation_input_tokens":0}}""";

    private const string FinalChunk =
        """data: {"id":"c1","choices":[{"delta":{},"index":0,"finish_reason":"stop"}],"usage":{"cache_read_input_tokens":0,"completion_tokens":16,"prompt_tokens":9,"total_tokens":25,"cache_creation_input_tokens":0}}""";

    private static string Repair(string line) =>
        StreamingUsageRepairPolicy.RepairLine(line);

    [Fact]
    public void An_incomplete_usage_object_is_removed_rather_than_zeroed()
    {
        // Arrange — zeroing would deserialise and then lie: a caller summing chunks would add
        // several zeros and land on the last chunk's number by accident.

        // Act
        var repaired = Repair(IncompleteChunk);

        // Assert
        var chunk = JsonDocument.Parse(repaired["data: ".Length..]).RootElement;
        chunk.TryGetProperty("usage", out _).ShouldBeFalse();
        chunk.GetProperty("choices")[0].GetProperty("delta").GetProperty("content").GetString().ShouldBe("hi");
    }

    [Fact]
    public void The_final_chunk_keeps_its_real_numbers()
    {
        // Arrange — the whole point of stripping rather than zeroing is that metering still works.

        // Act
        var repaired = Repair(FinalChunk);

        // Assert
        var usage = JsonDocument.Parse(repaired["data: ".Length..]).RootElement.GetProperty("usage");
        usage.GetProperty("completion_tokens").GetInt32().ShouldBe(16);
        usage.GetProperty("total_tokens").GetInt32().ShouldBe(25);
    }

    [Theory]
    [InlineData("data: [DONE]")]
    [InlineData("")]
    [InlineData(": keep-alive comment")]
    [InlineData("data: not json at all")]
    public void Lines_that_are_not_repairable_pass_through_unchanged(string line)
    {
        // Arrange — a shim that mangles the terminator, a comment or a malformed body turns a
        // clear client error into a corrupted stream, which is harder to diagnose than the bug it
        // was fixing.

        // Act
        var repaired = Repair(line);

        // Assert
        repaired.ShouldBe(line);
    }

    [Fact]
    public void A_chunk_with_no_usage_at_all_is_untouched()
    {
        // Arrange — OpenAI itself omits usage unless asked, so this is the shape a compliant
        // server sends and the shim must not disturb it.
        const string Line = """data: {"id":"c1","choices":[{"delta":{"content":"x"},"index":0}]}""";

        // Act & Assert
        Repair(Line).ShouldBe(Line);
    }
}

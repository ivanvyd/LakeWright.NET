using System.Text;
using LakeWright.AI;

namespace LakeWright.TenantIsolation.Tests;

/// <summary>
/// The stream the shim actually installs, rather than the line transform it calls.
/// </summary>
/// <remarks>
/// `StreamingShimTests` covered `RepairLine`, and everything around it — the buffering, the
/// offsets, the cancellation — was reachable only from `Category=Live`, which CI does not run. A
/// dropped `CancellationToken` in the async read path shipped through that gap and was found by
/// review rather than by a test.
/// </remarks>
public class SseRepairStreamTests
{
    private const string Incomplete =
        """data: {"choices":[{"delta":{"content":"hi"}}],"usage":{"completion_tokens":null,"prompt_tokens":9}}""";

    private const string Final =
        """data: {"choices":[{"delta":{}}],"usage":{"completion_tokens":16,"prompt_tokens":9,"total_tokens":25}}""";

    private static MemoryStream Source(params string[] lines) =>
        new MemoryStream(Encoding.UTF8.GetBytes(string.Join("\n", lines) + "\n"));

    private static async Task<string> ReadAllAsync(Stream stream, int bufferSize)
    {
        var output = new MemoryStream();
        var buffer = new byte[bufferSize];
        int read;

        while ((read = await stream.ReadAsync(buffer, TestContext.Current.CancellationToken)) > 0)
        {
            output.Write(buffer, 0, read);
        }

        return Encoding.UTF8.GetString(output.ToArray());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(7)]
    [InlineData(4096)]
    public async Task The_repair_is_the_same_however_small_the_callers_buffer(int bufferSize)
    {
        // Arrange — a one-byte buffer forces a line to be handed back across many reads, which is
        // where an offset bug would show. The client picks the size, not us.
        await using var stream = new SseUsageRepairStream(Source(Incomplete, Final, "data: [DONE]"));

        // Act
        var result = await ReadAllAsync(stream, bufferSize);

        // Assert
        result.ShouldNotContain("\"completion_tokens\":null");
        result.ShouldContain("\"completion_tokens\":16");
        result.ShouldContain("data: [DONE]");
        result.ShouldContain("\"content\":\"hi\"");
    }

    [Fact]
    public async Task Every_line_ends_with_a_single_line_feed()
    {
        // Arrange — SSE framing is defined in terms of LF. Emitting CRLF on Windows would change
        // the wire format the client parses.
        await using var stream = new SseUsageRepairStream(Source(Incomplete, Final));

        // Act
        var result = await ReadAllAsync(stream, 64);

        // Assert
        result.ShouldNotContain("\r");
        result.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(2);
    }

    [Fact]
    public async Task A_cancelled_read_stops_rather_than_running_to_completion()
    {
        // Arrange — the token reached the public signature and stopped there: EnsurePendingAsync
        // took none and called the parameterless ReadLineAsync, so a caller cancelling a streaming
        // completion was not observed by the read that actually blocks.
        await using var stream = new SseUsageRepairStream(Source(Incomplete, Final));
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        // Act
        var read = async () => await stream.ReadAsync(new byte[64], cancelled.Token);

        // Assert
        await read.ShouldThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Reading_past_the_end_returns_zero_rather_than_looping()
    {
        // Arrange
        await using var stream = new SseUsageRepairStream(Source(Final));
        await ReadAllAsync(stream, 1024);

        // Act
        var afterEnd = await stream.ReadAsync(new byte[16], TestContext.Current.CancellationToken);

        // Assert
        afterEnd.ShouldBe(0);
    }

    [Fact]
    public void The_synchronous_path_repairs_identically()
    {
        // Arrange — the transport picks sync or async, so both have to agree.
        using var stream = new SseUsageRepairStream(Source(Incomplete, Final));
        using var reader = new StreamReader(stream);

        // Act
        var result = reader.ReadToEnd();

        // Assert
        result.ShouldNotContain("\"completion_tokens\":null");
        result.ShouldContain("\"completion_tokens\":16");
    }

    [Fact]
    public void Disposing_the_wrapper_disposes_what_it_wrapped()
    {
        // Arrange — the wrapper owns the content stream once the policy hands it over; leaking a
        // network stream per response would be a slow leak nobody attributes to this.
        var inner = Source(Final);
        var stream = new SseUsageRepairStream(inner);

        // Act
        stream.Dispose();

        // Assert
        Should.Throw<ObjectDisposedException>(() => inner.ReadByte());
    }
}

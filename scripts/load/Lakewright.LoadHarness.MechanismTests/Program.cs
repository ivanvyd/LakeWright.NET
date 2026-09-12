using System.Net;
using Lakewright.LoadHarness;

internal static class Program
{
    public static async Task<int> Main()
    {
        await ThrowsAreCountedAtTheEndpoint();
        await ASlowServerDropsBoundedArrivals();
        await CancellationIsCounted();
        await PhaseDeadlineClassifiesEveryIssuedRequest();
        Console.WriteLine("Load harness mechanism tests passed.");
        return 0;
    }

    private static async Task ThrowsAreCountedAtTheEndpoint()
    {
        var handler = new FakeHandler(request => request.Method == HttpMethod.Post
            ? throw new HttpRequestException("operations transport failure")
            : Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)));
        var result = await Run(handler, queue: 16, connections: 2, rps: 40);
        Require(result.OperationsPostCount > 0 && result.ErrorRateOperationsPost > 0, "operation transport failures must be counted at operations");
        Require(result.CostGetCount > 0 && result.ErrorRateCostGet == 0, "cost successes must retain endpoint identity");
        Require(!result.AllGatesPassed, "partial transport failure must fail the gate");
    }

    private static async Task ASlowServerDropsBoundedArrivals()
    {
        var handler = new FakeHandler(async _ => { await Task.Delay(100); return new HttpResponseMessage(HttpStatusCode.OK); });
        var result = await Run(handler, queue: 1, connections: 1, rps: 200);
        Require(result.DroppedRequests > 0, "slow server must fill the bounded queue");
        Require(!result.ThroughputPassed, "under-delivered offered load must fail throughput");
    }

    private static async Task CancellationIsCounted()
    {
        var handler = new FakeHandler(_ => throw new TaskCanceledException("request timeout"));
        var result = await Run(handler, queue: 16, connections: 2, rps: 40);
        Require(result.CombinedErrorRate > 0 && !result.AllGatesPassed, "cancellation must be a failed issued request");
    }

    private static async Task PhaseDeadlineClassifiesEveryIssuedRequest()
    {
        using var client = new HttpClient(new DeadlineHandler()) { BaseAddress = new Uri("http://localhost") };
        var options = new HarnessOptions { RequestsPerSecond = 40, MaxConnections = 2, MaxQueuedRequests = 8 };
        var harness = new Harness(client, null, options, Guid.NewGuid(), "test");
        var measured = await harness.RunPhaseForTestsAsync(TimeSpan.FromMilliseconds(200));
        Require(measured.IssuedRequests > 0 && measured.CompletedRequests == 0, "the fixture must hold issued requests until the phase deadline");
        Require(measured.InFlightAtDeadline == measured.IssuedRequests, "every unfinished issued request must remain classified after synchronous cancellation");
        Require(measured.CensoredRequests == measured.InFlightAtDeadline + measured.QueuedAtDeadline, "cancellation order must not lose a censored request");
        Require(measured.ScheduledRequests == measured.CensoredRequests + measured.DroppedRequests, "all arrivals must reconcile after the deadline");
    }

    private static async Task<Verdict> Run(HttpMessageHandler handler, int queue, int connections, int rps)
    {
        var options = new HarnessOptions
        {
            RequestsPerSecond = rps, MaxQueuedRequests = queue, MaxConnections = connections,
            MinimumAchievedRate = .95, MinimumSamplesPerEndpoint = 1, ErrorRateSlo = .001,
            OperationsPostP99SloMs = 1_000, CostGetP99SloMs = 1_000, PostgresMaxConnections = 200
        };
        using var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost") };
        var harness = new Harness(client, new PostgresSampler(), options, Guid.NewGuid(), "test");
        return SloGate.Evaluate(await harness.RunPhaseForTestsAsync(TimeSpan.FromSeconds(1)), options);
    }

    private static void Require(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    private sealed class FakeHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request);
    }

    private sealed class DeadlineHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            // Deliberately allow synchronous continuations: HTTP cancellation can finish a
            // worker before an earlier phase-cancellation callback snapshots its active count.
            var completion = new TaskCompletionSource<HttpResponseMessage>();
            using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
            return await completion.Task.ConfigureAwait(false);
        }
    }
}

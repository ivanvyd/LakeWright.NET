namespace LakeWright.Databricks;

internal sealed class StatementDeadline : IDisposable
{
    private readonly TimeProvider _time;
    private readonly long _startedAt;
    private readonly TimeSpan _budget;
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _linked;
    private string _statementId = string.Empty;

    public StatementDeadline(TimeProvider time, TimeSpan budget, CancellationToken caller)
    {
        _time = time;
        _startedAt = time.GetTimestamp();
        _budget = budget;
        _caller = caller;
        _timeout = new CancellationTokenSource(budget, time);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(caller, _timeout.Token);
    }

    public CancellationToken Token => _linked.Token;

    public void Observe(StatementOutcome outcome)
    {
        _statementId = outcome switch
        {
            StatementOutcome.Pending pending => pending.StatementId,
            StatementOutcome.Success success => success.StatementId,
            StatementOutcome.LargeResult large => large.StatementId,
            StatementOutcome.Failure failure => failure.StatementId ?? _statementId,
            _ => _statementId,
        };
    }

    public void Check()
    {
        _caller.ThrowIfCancellationRequested();
        if (_timeout.IsCancellationRequested || _time.GetElapsedTime(_startedAt) >= _budget)
        {
            throw new StatementBudgetExceededException(_statementId, _budget);
        }
    }

    public async Task<T> RunAsync<T>(Func<CancellationToken, Task<T>> action)
    {
        Check();
        try
        {
            var result = await action(Token).ConfigureAwait(false);
            if (result is StatementOutcome outcome) { Observe(outcome); }
            Check();
            return result;
        }
        catch (OperationCanceledException)
        {
            Check();
            throw;
        }
    }

    public void Dispose()
    {
        _linked.Dispose();
        _timeout.Dispose();
    }
}

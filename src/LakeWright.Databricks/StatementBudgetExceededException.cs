using LakeWright.Core;

namespace LakeWright.Databricks;

/// <summary>The local statement or export deadline expired. This does not confirm remote cancellation.</summary>
public sealed class StatementBudgetExceededException(string statementId, TimeSpan budget)
    : LakeWrightException($"The statement did not complete within the {budget} local budget.")
{
    /// <summary>The accepted statement id, or an empty string if submission did not return an id.</summary>
    public string StatementId { get; } = statementId;

    public TimeSpan Budget { get; } = budget;
}

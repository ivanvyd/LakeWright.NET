using LakeWright.Core;

namespace LakeWright.Databricks;

/// <summary>A statement remained pending after its configured local polling budget expired.</summary>
public sealed class StatementBudgetExceededException(string statementId, TimeSpan budget)
    : LakeWrightException($"Statement '{statementId}' did not complete within the {budget} polling budget.")
{
    public string StatementId { get; } = statementId;

    public TimeSpan Budget { get; } = budget;
}

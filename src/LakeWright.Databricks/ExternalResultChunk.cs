namespace LakeWright.Databricks;

/// <summary>One external result chunk. Its signed link is a credential and must not be logged.</summary>
public sealed record ExternalResultChunk(
    int Index,
    long RowOffset,
    long RowCount,
    Uri Link,
    DateTimeOffset? ExpiresAt,
    int? NextChunkIndex)
{
    public override string ToString() => $"Chunk {Index}, rows {RowCount}, offset {RowOffset}";
}

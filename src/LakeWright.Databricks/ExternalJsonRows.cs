using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LakeWright.Databricks;

internal static class ExternalJsonRows
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultBufferSize = 16 * 1024, MaxDepth = 4 };

    public static async IAsyncEnumerable<IReadOnlyList<string?>> ReadAsync(
        Stream stream, int columnCount, string kind, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        using var bounded = new RowReadStream(stream, kind);
        await foreach (var row in JsonSerializer.DeserializeAsyncEnumerable<string?[]>(bounded, JsonOptions, cancellationToken).ConfigureAwait(false))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row is null || row.Length != columnCount)
            {
                throw new InvalidDataException("The external result row does not match its column schema.");
            }
            bounded.NextRow();
            LakeWrightDatabricksTelemetry.ExportRows.Add(1, new TagList { { "statement.kind", kind } });
            yield return row;
        }
    }

    // The serializer streams the root array, but must buffer one row. Bound reads between rows
    // so a malformed or oversized element cannot grow that buffer without limit.
    private sealed class RowReadStream(Stream inner, string kind) : Stream
    {
        private const int MaxReadBetweenRows = 1024 * 1024;
        private int _sinceRow;
        public void NextRow() => _sinceRow = 0;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_sinceRow >= MaxReadBetweenRows) { throw new InvalidDataException("An external result row exceeds the streaming read limit."); }
            var read = await inner.ReadAsync(buffer[..Math.Min(buffer.Length, MaxReadBetweenRows - _sinceRow)], cancellationToken).ConfigureAwait(false);
            _sinceRow += read;
            LakeWrightDatabricksTelemetry.ExportBytes.Add(read, new TagList { { "statement.kind", kind } });
            return read;
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}

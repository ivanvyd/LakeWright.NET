using System.Text;

namespace LakeWright.AI;

/// <summary>
/// Rewrites an SSE stream line by line as it is read, stripping incomplete <c>usage</c> objects.
/// </summary>
/// <remarks>
/// A filter rather than a buffer. The first version of this shim read the whole response into a
/// <see cref="MemoryStream"/> and swapped it in, which gave up the latency streaming exists for
/// and, more immediately, broke the transport: the client buffers the response after the policy
/// returns and rejects a content stream that is not at position zero, which reading it leaves it
/// at. Filtering keeps the original stream in the client's hands and holds one line at a time.
/// </remarks>
internal sealed class SseUsageRepairStream(Stream inner) : Stream
{
    private readonly StreamReader _reader = new(inner, Encoding.UTF8, leaveOpen: false);
    private byte[] _pending = [];
    private int _offset;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        // The transport asks before buffering. Answering zero is honest for a stream that has
        // never been sought and cannot be.
        get => 0;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        if (!EnsurePending()) { return 0; }

        var taken = Math.Min(count, _pending.Length - _offset);
        Array.Copy(_pending, _offset, buffer, offset, taken);
        _offset += taken;
        return taken;
    }

    public override async ValueTask<int> ReadAsync(
        Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!await EnsurePendingAsync().ConfigureAwait(false)) { return 0; }

        var taken = Math.Min(buffer.Length, _pending.Length - _offset);
        _pending.AsMemory(_offset, taken).CopyTo(buffer);
        _offset += taken;
        return taken;
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    private bool EnsurePending()
    {
        while (_offset >= _pending.Length)
        {
            if (_reader.ReadLine() is not { } line) { return false; }
            Load(line);
        }

        return true;
    }

    private async ValueTask<bool> EnsurePendingAsync()
    {
        while (_offset >= _pending.Length)
        {
            if (await _reader.ReadLineAsync().ConfigureAwait(false) is not { } line) { return false; }
            Load(line);
        }

        return true;
    }

    /// <remarks>
    /// "\n" rather than Environment.NewLine: SSE framing is defined in terms of LF, and emitting
    /// CRLF on Windows would change the wire format the client parses.
    /// </remarks>
    private void Load(string line)
    {
        _pending = Encoding.UTF8.GetBytes(StreamingUsageRepairPolicy.RepairLine(line) + "\n");
        _offset = 0;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { _reader.Dispose(); }
        base.Dispose(disposing);
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}

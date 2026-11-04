namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// A write-only <see cref="Stream"/> that appends into a <see cref="PooledBufferWriter"/>, so
/// text written through a <see cref="StreamWriter"/> is encoded straight into pooled UTF-8
/// buffers instead of an intermediate <see cref="System.Text.StringBuilder"/> (which doubles the
/// large-document allocation profile of <see cref="StringWriter"/>).
/// </summary>
internal sealed class PooledBufferStream(PooledBufferWriter writer) : Stream
{
    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;

    public override long Length => writer.WrittenSpan.Length;

    public override long Position
    {
        get => writer.WrittenSpan.Length;
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) =>
        Write(buffer.AsSpan(offset, count));

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        var span = writer.GetSpan(buffer.Length);
        buffer.CopyTo(span);
        writer.Advance(buffer.Length);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Write(buffer.Span);
        return ValueTask.CompletedTask;
    }

    public override void WriteByte(byte value)
    {
        var span = writer.GetSpan(1);
        span[0] = value;
        writer.Advance(1);
    }
}

using System.Buffers;

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>Buffers small writes and forwards committed UTF-8 chunks to a caller-owned stream.</summary>
internal sealed class StreamBufferWriter(Stream stream) : IFlushableBufferWriter, IDisposable
{
    // Hold typical documents (including the benchmark's 64 KB scalar) in one pooled chunk. This
    // avoids repeated writes and geometric growth in destinations such as MemoryStream while
    // keeping memory bounded for arbitrarily large documents.
    private const int InitialSize = 128 * 1024;

    private readonly Stream _stream = stream ?? throw new ArgumentNullException(nameof(stream));
    private byte[] _buffer = ArrayPool<byte>.Shared.Rent(InitialSize);
    private int _written;
    private bool _disposed;

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(StreamBufferWriter));
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        if (count > _buffer.Length - _written)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _written += count;
        if (_written == _buffer.Length)
        {
            Flush();
        }
    }

    public Memory<byte> GetMemory(int sizeHint = 0) => GetBuffer(sizeHint).AsMemory(_written);

    public Span<byte> GetSpan(int sizeHint = 0) => GetBuffer(sizeHint).AsSpan(_written);

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(StreamBufferWriter));

        if (_written == 0)
        {
            return;
        }

        _stream.Write(_buffer.AsSpan(0, _written));
        _written = 0;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = [];
        _written = 0;
        _disposed = true;
    }

    private byte[] GetBuffer(int sizeHint)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(StreamBufferWriter));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint, nameof(sizeHint));

        if (sizeHint == 0)
        {
            sizeHint = 1;
        }

        if (_written + sizeHint <= _buffer.Length)
        {
            return _buffer;
        }

        Flush();

        if (sizeHint <= _buffer.Length)
        {
            return _buffer;
        }

        var newBuffer = ArrayPool<byte>.Shared.Rent(sizeHint);
        ArrayPool<byte>.Shared.Return(_buffer);
        _buffer = newBuffer;
        return _buffer;
    }
}

using System.Buffers;

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// Buffers small payloads in pooled memory and switches to a streaming writer once the configured
/// limit is reached.
/// </summary>
internal sealed class ThresholdBufferWriter : IFlushableBufferWriter, IDisposable
{
    private const int InitialSize = 1024;
    private const int SpillCopySize = 16 * 1024;

    private readonly int _maximumBufferedBytes;
    private readonly Func<PipeBufferWriter> _createPipeWriter;
    private byte[]? _buffer;
    private int _written;
    private PipeBufferWriter? _pipeWriter;
    private int _available;
    private bool _disposed;

    public ThresholdBufferWriter(int maximumBufferedBytes, Func<PipeBufferWriter> createPipeWriter)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBufferedBytes);
        _maximumBufferedBytes = maximumBufferedBytes;
        _createPipeWriter = createPipeWriter ?? throw new ArgumentNullException(nameof(createPipeWriter));
        _buffer = ArrayPool<byte>.Shared.Rent(Math.Min(InitialSize, maximumBufferedBytes));
    }

    public bool IsPiped => _pipeWriter is not null;

    public ReadOnlyMemory<byte> WrittenMemory =>
        _buffer?.AsMemory(0, _written) ?? throw new InvalidOperationException("The payload has spilled to the pipe.");

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ThresholdBufferWriter));

        if (_pipeWriter is not null)
        {
            _pipeWriter.Advance(count);
            return;
        }

        ArgumentOutOfRangeException.ThrowIfNegative(count);
        if (count > _available)
        {
            throw new ArgumentOutOfRangeException(nameof(count));
        }

        _written += count;
        _available = 0;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ThresholdBufferWriter));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        if (_pipeWriter is not null)
        {
            return _pipeWriter.GetMemory(sizeHint);
        }

        var requiredSize = Math.Max(sizeHint, 1);
        var remaining = _maximumBufferedBytes - _written;

        if (requiredSize > remaining)
        {
            Spill();
            return _pipeWriter!.GetMemory(sizeHint);
        }

        EnsureCapacity(requiredSize);
        _available = Math.Min(_buffer!.Length - _written, remaining);
        return _buffer.AsMemory(_written, _available);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ThresholdBufferWriter));
        ArgumentOutOfRangeException.ThrowIfNegative(sizeHint);

        if (_pipeWriter is not null)
        {
            return _pipeWriter.GetSpan(sizeHint);
        }

        var requiredSize = Math.Max(sizeHint, 1);
        var remaining = _maximumBufferedBytes - _written;

        if (requiredSize > remaining)
        {
            Spill();
            return _pipeWriter!.GetSpan(sizeHint);
        }

        EnsureCapacity(requiredSize);
        _available = Math.Min(_buffer!.Length - _written, remaining);
        return _buffer.AsSpan(_written, _available);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(ThresholdBufferWriter));
        _pipeWriter?.Flush();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _pipeWriter?.Dispose();
        if (_buffer is not null)
        {
            ArrayPool<byte>.Shared.Return(_buffer);
        }

        _pipeWriter = null;
        _buffer = null;
        _written = 0;
        _available = 0;
        _disposed = true;
    }

    private void Spill()
    {
        var pipeWriter = _createPipeWriter();
        _pipeWriter = pipeWriter;

        var buffer = _buffer!;
        var buffered = buffer.AsSpan(0, _written);
        var offset = 0;

        while (offset < buffered.Length)
        {
            var count = Math.Min(SpillCopySize, buffered.Length - offset);
            buffered.Slice(offset, count).CopyTo(pipeWriter.GetSpan(count));
            pipeWriter.Advance(count);
            offset += count;
        }

        ArrayPool<byte>.Shared.Return(buffer);
        _buffer = null;
        _written = 0;
        _available = 0;
    }

    private void EnsureCapacity(int sizeHint)
    {
        var buffer = _buffer!;
        if (_written + sizeHint <= buffer.Length)
        {
            return;
        }

        var newSize = Math.Min(
            _maximumBufferedBytes,
            Math.Max(_written + sizeHint, buffer.Length * 2));
        var newBuffer = ArrayPool<byte>.Shared.Rent(newSize);
        buffer.AsSpan(0, _written).CopyTo(newBuffer);
        ArrayPool<byte>.Shared.Return(buffer);
        _buffer = newBuffer;
    }
}

using System.Buffers;
using System.IO.Pipelines;

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// Adapts a bounded <see cref="PipeWriter"/> to <see cref="IBufferWriter{T}"/> and applies
/// backpressure after each configured output threshold.
/// </summary>
internal sealed class PipeBufferWriter(PipeWriter writer, CancellationToken cancellationToken = default) : IFlushableBufferWriter, IDisposable
{
    private const int FlushThreshold = 64 * 1024;

    private int _unflushed;
    private bool _disposed;

    public void Advance(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PipeBufferWriter));
        ArgumentOutOfRangeException.ThrowIfNegative(count);

        writer.Advance(count);
        _unflushed = checked(_unflushed + count);

        if (_unflushed >= FlushThreshold)
        {
            Flush();
        }
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PipeBufferWriter));
        cancellationToken.ThrowIfCancellationRequested();
        return writer.GetMemory(sizeHint);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PipeBufferWriter));
        cancellationToken.ThrowIfCancellationRequested();
        return writer.GetSpan(sizeHint);
    }

    public void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, nameof(PipeBufferWriter));
        cancellationToken.ThrowIfCancellationRequested();

        if (_unflushed == 0)
        {
            return;
        }

        var pendingFlush = writer.FlushAsync(cancellationToken);
        var result = pendingFlush.IsCompleted
            ? pendingFlush.GetAwaiter().GetResult()
            : pendingFlush.AsTask().GetAwaiter().GetResult();
        _unflushed = 0;

        if (result.IsCanceled)
        {
            throw new OperationCanceledException(cancellationToken);
        }

        if (result.IsCompleted)
        {
            throw new IOException("The pipe reader completed before the writer finished.");
        }
    }

    public void Dispose() => _disposed = true;
}

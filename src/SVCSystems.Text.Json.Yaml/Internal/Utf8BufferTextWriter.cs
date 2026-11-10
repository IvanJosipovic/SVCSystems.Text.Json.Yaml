using System.Buffers;
using System.Text;

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// A <see cref="TextWriter"/> that UTF-8 encodes characters into a small pooled byte buffer before
/// committing them to an <see cref="IBufferWriter{T}"/>. It does not allocate a char buffer.
/// </summary>
/// <remarks>
/// Unpaired surrogates are rejected. A high surrogate held back at the end of one write is
/// correctly paired with the low surrogate at the start of the next one.
/// <para>
/// <see cref="Flush"/> and <see cref="Dispose"/> reject a pending unpaired surrogate and commit
/// buffered bytes. The writer does not close or dispose the underlying <see cref="IBufferWriter{T}"/>.
/// </para>
/// </remarks>
internal sealed class Utf8BufferTextWriter(IBufferWriter<byte> writer) : TextWriter
{
    private const int BufferSize = 4096;

    private readonly IBufferWriter<byte> _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private byte[]? _buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
    private int _written;
    private char _pendingHighSurrogate;
    private bool _disposed;

    public override Encoding Encoding => JsonToYamlConverter.Utf8NoBom;

    public override void Write(char value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pendingHighSurrogate != '\0')
        {
            if (char.IsLowSurrogate(value))
            {
                EncodePair(_pendingHighSurrogate, value);
            }
            else
            {
                _pendingHighSurrogate = '\0';
                throw InvalidSurrogate();
            }

            _pendingHighSurrogate = '\0';
            return;
        }

        if (value < 0x80)
        {
            _buffer![_written++] = (byte)value;
            if (_written == _buffer.Length)
            {
                FlushBuffer();
            }
        }
        else if (char.IsHighSurrogate(value))
        {
            _pendingHighSurrogate = value;
        }
        else if (char.IsLowSurrogate(value))
        {
            throw InvalidSurrogate();
        }
        else
        {
            WriteCore(stackalloc char[] { value });
        }
    }

    public override void Write(string? value)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (value is not null && value.Length > 0)
        {
            WriteCore(value.AsSpan());
        }
    }

    public override void Write(char[]? buffer, int index, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + count, buffer.Length);

        if (count > 0)
        {
            WriteCore(buffer.AsSpan(index, count));
        }
    }

    public override void Write(ReadOnlySpan<char> buffer)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        WriteCore(buffer);
    }

    public override void Flush()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pendingHighSurrogate != '\0')
        {
            _pendingHighSurrogate = '\0';
            throw InvalidSurrogate();
        }

        FlushBuffer();
    }

    protected override void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            try
            {
                if (disposing)
                {
                    Flush();
                }
            }
            finally
            {
                if (_buffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_buffer);
                    _buffer = null;
                }

                _written = 0;
                _disposed = true;
            }
        }

        base.Dispose(disposing);
    }

    private void WriteCore(ReadOnlySpan<char> value)
    {
        if (value.IsEmpty)
        {
            return;
        }

        if (_pendingHighSurrogate != '\0')
        {
            if (value.Length > 0 && char.IsLowSurrogate(value[0]))
            {
                EncodePair(_pendingHighSurrogate, value[0]);
                value = value[1..];
            }
            else
            {
                _pendingHighSurrogate = '\0';
                throw InvalidSurrogate();
            }

            _pendingHighSurrogate = '\0';
        }

        if (value.IsEmpty)
        {
            return;
        }

        if (Ascii.IsValid(value))
        {
            if (value.Length >= _buffer!.Length)
            {
                FlushBuffer();
                var destination = _writer.GetSpan(value.Length);
                Ascii.FromUtf16(value, destination, out var directWritten);
                _writer.Advance(directWritten);
                return;
            }

            if (_buffer.Length - _written < value.Length)
            {
                FlushBuffer();
            }

            var bufferedDestination = _buffer.AsSpan(_written);
            Ascii.FromUtf16(value, bufferedDestination, out var written);
            _written += written;
            return;
        }

        // A trailing high surrogate is an incomplete pair within this chunk; hold it back so a
        // pair split across Write calls still encodes as one rune (the same as an
        // Encoding.UTF8 Encoder created with flush: false).
        if (char.IsHighSurrogate(value[^1]))
        {
            EncodeCore(value[..^1]);
            _pendingHighSurrogate = value[^1];
            return;
        }

        EncodeCore(value);
    }

    private void EncodeCore(ReadOnlySpan<char> value)
    {
        var maxByteCount = JsonToYamlConverter.Utf8NoBom.GetMaxByteCount(value.Length);
        if (maxByteCount >= _buffer!.Length)
        {
            FlushBuffer();
            var destination = _writer.GetSpan(maxByteCount);
            var directWritten = JsonToYamlConverter.Utf8NoBom.GetBytes(value, destination);
            _writer.Advance(directWritten);
            return;
        }

        if (_buffer.Length - _written < maxByteCount)
        {
            FlushBuffer();
        }

        var bufferedDestination = _buffer.AsSpan(_written);
        _written += JsonToYamlConverter.Utf8NoBom.GetBytes(value, bufferedDestination);
    }

    private void EncodePair(char high, char low)
    {
        Span<char> pair = [high, low];
        EncodeCore(pair);
    }

    private void FlushBuffer()
    {
        if (_written == 0)
        {
            return;
        }

        var destination = _writer.GetSpan(_written);
        _buffer!.AsSpan(0, _written).CopyTo(destination);
        _writer.Advance(_written);
        _written = 0;
    }

    private static EncoderFallbackException InvalidSurrogate() =>
        new("The YAML output contains an unpaired UTF-16 surrogate, which is not a valid Unicode scalar value.");
}

using System.Buffers;
using System.Text;

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// A <see cref="TextWriter"/> that UTF-8 encodes characters directly into an
/// <see cref="IBufferWriter{T}"/> as they are written, with no intermediate char or byte buffer.
/// </summary>
/// <remarks>
/// Unpaired surrogates are rejected. A high surrogate held back at the end of one write is
/// correctly paired with the low surrogate at the start of the next one.
/// <para>
/// <see cref="Flush"/> and <see cref="Dispose"/> reject a pending unpaired surrogate. The writer
/// does not close or dispose the underlying <see cref="IBufferWriter{T}"/>.
/// </para>
/// </remarks>
internal sealed class Utf8BufferTextWriter(IBufferWriter<byte> writer) : TextWriter
{
    private readonly IBufferWriter<byte> _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    private char _pendingHighSurrogate;

    public override Encoding Encoding => JsonToYamlConverter.Utf8NoBom;

    public override void Write(char value)
    {
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
            var span = _writer.GetSpan(1);
            span[0] = (byte)value;
            _writer.Advance(1);
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
        if (value is not null && value.Length > 0)
        {
            WriteCore(value.AsSpan());
        }
    }

    public override void Write(char[]? buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + count, buffer.Length);

        if (count > 0)
        {
            WriteCore(buffer.AsSpan(index, count));
        }
    }

    public override void Write(ReadOnlySpan<char> buffer) => WriteCore(buffer);

    public override void Flush()
    {
        if (_pendingHighSurrogate != '\0')
        {
            _pendingHighSurrogate = '\0';
            throw InvalidSurrogate();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Flush();
        }

        base.Dispose(disposing);
    }

    private void WriteCore(ReadOnlySpan<char> value)
    {
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
            var destination = _writer.GetSpan(value.Length);
            Ascii.FromUtf16(value, destination, out var written);
            _writer.Advance(written);
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
        var destination = _writer.GetSpan(JsonToYamlConverter.Utf8NoBom.GetMaxByteCount(value.Length));
        var written = JsonToYamlConverter.Utf8NoBom.GetBytes(value, destination);
        _writer.Advance(written);
    }

    private void EncodePair(char high, char low)
    {
        Span<char> pair = [high, low];
        var destination = _writer.GetSpan(4);
        var written = JsonToYamlConverter.Utf8NoBom.GetBytes(pair, destination);
        _writer.Advance(written);
    }

    private static EncoderFallbackException InvalidSurrogate() =>
        new("The YAML output contains an unpaired UTF-16 surrogate, which is not a valid Unicode scalar value.");
}

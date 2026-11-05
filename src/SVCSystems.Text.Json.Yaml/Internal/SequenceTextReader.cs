using System.Buffers;
using System.Text;

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// A pull based <see cref="TextReader"/> over UTF-8 bytes held in a
/// <see cref="ReadOnlySequence{T}"/>.
/// </summary>
/// <remarks>
/// Bytes are decoded incrementally into an internal char buffer on demand, so no whole input
/// UTF-16 string is materialized. Malformed and incomplete UTF-8 is rejected. A UTF-8 byte order
/// mark (<c>EF BB BF</c>), when present, is skipped.
/// <para>
/// The optional <see cref="CancellationToken"/> is checked at every buffer refill, which is the
/// only place input is pulled; for in memory sequences the token is checked at the first read.
/// </para>
/// </remarks>
internal sealed class SequenceTextReader : TextReader
{
    private const int CharBufferSize = 1024;

    private readonly char[] _buffer = new char[CharBufferSize];
    private readonly Decoder _decoder = JsonToYamlConverter.Utf8NoBom.GetDecoder();
    private readonly CancellationToken _cancellationToken;
    private ReadOnlySequence<byte> _sequence;
    private bool _endOfInput;
    private int _start;
    private int _end;

    public SequenceTextReader(ReadOnlySequence<byte> utf8, CancellationToken cancellationToken = default)
    {
        _sequence = SkipByteOrderMark(utf8);
        _cancellationToken = cancellationToken;
    }

    public SequenceTextReader(ReadOnlyMemory<byte> utf8, CancellationToken cancellationToken = default)
        : this(new ReadOnlySequence<byte>(utf8), cancellationToken)
    {
    }

    private static ReadOnlySequence<byte> SkipByteOrderMark(ReadOnlySequence<byte> sequence)
    {
        if (sequence.Length >= 3)
        {
            Span<byte> prefix = stackalloc byte[3];
            sequence.Slice(0, 3).CopyTo(prefix);

            if (prefix[0] == 0xEF && prefix[1] == 0xBB && prefix[2] == 0xBF)
            {
                return sequence.Slice(3);
            }
        }

        return sequence;
    }

    public override int Peek() => EnsureBuffered() ? _buffer[_start] : -1;

    public override int Read() => EnsureBuffered() ? _buffer[_start++] : -1;

    public override int Read(char[] buffer, int index, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        ArgumentOutOfRangeException.ThrowIfNegative(index);
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(index + count, buffer.Length);

        var read = 0;

        while (read < count && EnsureBuffered())
        {
            var available = Math.Min(count - read, _end - _start);

            _buffer.AsSpan(_start, available).CopyTo(buffer.AsSpan(index + read, available));

            _start += available;
            read += available;
        }

        return read;
    }

    private bool EnsureBuffered()
    {
        if (_start >= _end)
        {
            FillBuffer();
        }

        return _start < _end;
    }

    /// <summary>
    /// Decodes the next chunk of bytes into the char buffer. Each call leaves the buffer either
    /// holding at least one character or drained (end of input).
    /// </summary>
    private void FillBuffer()
    {
        if (_endOfInput)
        {
            return;
        }

        _start = _end = 0;

        // Keep at least two UTF-16 slots available when asking the decoder for more input; a
        // supplementary Unicode scalar must be emitted as an indivisible surrogate pair.
        while (_end < _buffer.Length - 1 && !_endOfInput)
        {
            _cancellationToken.ThrowIfCancellationRequested();

            if (_sequence.IsEmpty)
            {
                // Flush the decoder so an incomplete trailing rune is rejected.
                _decoder.Convert(default(ReadOnlySpan<byte>), _buffer.AsSpan(_end), flush: true, out _, out var flushedChars, out _);
                _end += flushedChars;
                _endOfInput = true;
                break;
            }

            var span = _sequence.IsSingleSegment ? _sequence.FirstSpan : _sequence.First.Span;

            _decoder.Convert(span, _buffer.AsSpan(_end), flush: false, out var bytesUsed, out var charsUsed, out _);

            if (bytesUsed > 0)
            {
                _sequence = _sequence.Slice(bytesUsed);
            }

            _end += charsUsed;

            if (bytesUsed == 0 && charsUsed == 0)
            {
                if (_sequence.IsSingleSegment)
                {
                    // An incomplete rune terminates the input; flush so the strict decoder rejects it.
                    _decoder.Convert(span, _buffer.AsSpan(_end), flush: true, out _, out charsUsed, out _);
                    _end += charsUsed;
                    _sequence = _sequence.Slice(span.Length);
                }
                else
                {
                    // The rune continues in the next segment; the decoder holds its state.
                    _sequence = _sequence.Slice(_sequence.First.Length);
                }
            }
        }
    }
}

using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using YamlDotNet.Core;
using YamlDotNet.Core.Events;

// Portions of this file are adapted from YamlDotNet.System.Text.Json
// (https://github.com/IvanJosipovic/YamlDotNet.System.Text.Json), MIT licensed.

namespace SVCSystems.Text.Json.Yaml.Internal;

/// <summary>
/// Converts a JSON token stream into YAML events written to a YamlDotNet emitter.
/// </summary>
/// <remarks>
/// Property order, numeric precision and string values are preserved. Strings that a YAML parser
/// could misinterpret as numbers, booleans or null are quoted so that a subsequent YAML parse
/// produces the original JSON value. The quoting decision is schema aware: under the core schema,
/// hexadecimal and octal forms also resolve to numbers and are therefore quoted.
/// </remarks>
internal static class JsonToYamlConverter
{
    internal static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    /// <summary>Emitter configuration captured once per conversion.</summary>
    private sealed class EmitterContext
    {
        public EmitterContext(YamlSerializerOptions? options)
        {
            MappingStyle = (options?.UseFlowStyle ?? false) ? MappingStyle.Flow : MappingStyle.Block;
            SequenceStyle = (options?.UseFlowStyle ?? false) ? SequenceStyle.Flow : SequenceStyle.Block;
            Schema = options?.ScalarSchema ?? YamlScalarSchema.Core;
            StringStyle = options?.StringStyle ?? YamlStringStyle.Auto;
        }

        public MappingStyle MappingStyle { get; }
        public SequenceStyle SequenceStyle { get; }
        public YamlScalarSchema Schema { get; }
        public YamlStringStyle StringStyle { get; }
    }

    public static string ConvertToString(ReadOnlySpan<byte> utf8Json, YamlSerializerOptions? options = null)
    {
        // The emitter writes into pooled UTF-8 buffers; a StringWriter/StringBuilder would double
        // the allocation profile of every emitted document.
        var buffer = new PooledBufferWriter();

        try
        {
            Convert(utf8Json, buffer, options);

            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public static byte[] ConvertToUtf8Bytes(ReadOnlySpan<byte> utf8Json, YamlSerializerOptions? options = null)
    {
        var buffer = new PooledBufferWriter();

        try
        {
            Convert(utf8Json, buffer, options);

            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Dispose();
        }
    }

    public static void Convert(ReadOnlySpan<byte> utf8Json, IBufferWriter<byte> output, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        using var writer = new StreamingWriter(output, options);

        while (!utf8Json.IsEmpty)
        {
            var count = Math.Min(utf8Json.Length, 4096);
            utf8Json[..count].CopyTo(writer.GetSpan(count));
            writer.Advance(count);
            utf8Json = utf8Json[count..];
        }

        writer.Complete();
    }

    /// <summary>Serializes a value to YAML without staging its complete JSON representation.</summary>
    public static void Serialize<T>(T value, JsonTypeInfo<T> jsonTypeInfo, IBufferWriter<byte> output, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        ArgumentNullException.ThrowIfNull(output);

        using var yamlWriter = new StreamingWriter(output, options);
        using (var jsonWriter = new Utf8JsonWriter(yamlWriter))
        {
            JsonSerializer.Serialize(jsonWriter, value, jsonTypeInfo);
        }

        yamlWriter.Complete();
    }

    /// <summary>Serializes a value to YAML without staging its complete JSON representation.</summary>
    [RequiresUnreferencedCode("JSON serialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON serialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static void Serialize<T>(T value, JsonSerializerOptions? jsonOptions, IBufferWriter<byte> output, YamlSerializerOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(output);

        using var yamlWriter = new StreamingWriter(output, options);
        using (var jsonWriter = new Utf8JsonWriter(yamlWriter))
        {
            JsonSerializer.Serialize(jsonWriter, value, jsonOptions);
        }

        yamlWriter.Complete();
    }

    /// <summary>
    /// Receives arbitrary UTF-8 chunks from <see cref="Utf8JsonWriter"/>, incrementally parses
    /// complete JSON tokens, and emits matching YAML events. Only an incomplete trailing JSON
    /// token is retained between writes.
    /// </summary>
    private sealed class StreamingWriter : IBufferWriter<byte>, IDisposable
    {
        private const int InitialInputSize = 4096;

        private readonly IBufferWriter<byte> _output;
        private readonly TextWriter _textWriter;
        private readonly Emitter _emitter;
        private readonly EmitterContext _context;
        private readonly List<HashSet<string>?> _collections = [];
        private byte[] _input = ArrayPool<byte>.Shared.Rent(InitialInputSize);
        private int _inputLength;
        private JsonReaderState _readerState;
        private bool _sawRootValue;
        private bool _completed;
        private bool _disposed;

        public StreamingWriter(IBufferWriter<byte> output, YamlSerializerOptions? options)
        {
            _output = output ?? throw new ArgumentNullException(nameof(output));
            _textWriter = new Utf8BufferTextWriter(output);
            _emitter = new Emitter(_textWriter, CreateEmitterSettings(options));
            _context = new EmitterContext(options);

            _emitter.Emit(new StreamStart());
            _emitter.Emit(new DocumentStart(null, null, options is null || !options.EmitDocumentStart, Mark.Empty, Mark.Empty));
        }

        public void Advance(int count)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(StreamingWriter));
            ObjectDisposedException.ThrowIf(_completed, nameof(StreamingWriter));
            ArgumentOutOfRangeException.ThrowIfNegative(count);

            if (count > _input.Length - _inputLength)
            {
                throw new ArgumentOutOfRangeException(nameof(count));
            }

            _inputLength += count;
            ProcessInput(isFinalBlock: false);
        }

        public Memory<byte> GetMemory(int sizeHint = 0) => GetBuffer(sizeHint).AsMemory(_inputLength);

        public Span<byte> GetSpan(int sizeHint = 0) => GetBuffer(sizeHint).AsSpan(_inputLength);

        public void Complete()
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(StreamingWriter));
            ObjectDisposedException.ThrowIf(_completed, nameof(StreamingWriter));

            ProcessInput(isFinalBlock: true);

            if (!_sawRootValue)
            {
                throw new InvalidOperationException("The JSON payload produced by System.Text.Json is empty.");
            }

            if (_collections.Count != 0)
            {
                throw new JsonException("The JSON payload ended before all collections were closed.");
            }

            _emitter.Emit(new DocumentEnd(true, Mark.Empty, Mark.Empty));
            _emitter.Emit(new StreamEnd());
            _textWriter.Flush();

            if (_output is IFlushableBufferWriter flushable)
            {
                flushable.Flush();
            }

            _completed = true;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _textWriter.Dispose();
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(_input);
                _input = [];
                _inputLength = 0;
                _disposed = true;
            }
        }

        private void ProcessInput(bool isFinalBlock)
        {
            var reader = new Utf8JsonReader(_input.AsSpan(0, _inputLength), isFinalBlock, _readerState);

            while (reader.Read())
            {
                ConvertToken(ref reader);
            }

            var consumed = checked((int)reader.BytesConsumed);
            _readerState = reader.CurrentState;

            if (consumed > 0)
            {
                var remaining = _inputLength - consumed;
                if (remaining > 0)
                {
                    Buffer.BlockCopy(_input, consumed, _input, 0, remaining);
                }

                _inputLength = remaining;
            }

            if (isFinalBlock && _inputLength != 0)
            {
                throw new JsonException("The JSON payload ended in the middle of a token.");
            }
        }

        private void ConvertToken(ref Utf8JsonReader reader)
        {
            switch (reader.TokenType)
            {
                case JsonTokenType.StartObject:
                    _sawRootValue = true;
                    _emitter.Emit(new MappingStart(default, default, true, _context.MappingStyle));
                    _collections.Add(new HashSet<string>(StringComparer.Ordinal));
                    return;

                case JsonTokenType.EndObject:
                    if (_collections.Count == 0 || _collections[^1] is null)
                    {
                        throw new JsonException("Unexpected JSON object terminator.");
                    }

                    _collections.RemoveAt(_collections.Count - 1);
                    _emitter.Emit(new MappingEnd());
                    return;

                case JsonTokenType.StartArray:
                    _sawRootValue = true;
                    _emitter.Emit(new SequenceStart(default, default, true, _context.SequenceStyle));
                    _collections.Add(null);
                    return;

                case JsonTokenType.EndArray:
                    if (_collections.Count == 0 || _collections[^1] is not null)
                    {
                        throw new JsonException("Unexpected JSON array terminator.");
                    }

                    _collections.RemoveAt(_collections.Count - 1);
                    _emitter.Emit(new SequenceEnd());
                    return;

                case JsonTokenType.PropertyName:
                {
                    if (_collections.Count == 0 || _collections[^1] is not { } names)
                    {
                        throw new JsonException("Unexpected JSON property name outside an object.");
                    }

                    var name = reader.GetString()!;
                    if (!names.Add(name))
                    {
                        throw new JsonException($"The JSON object contains the duplicate property name '{name}', which cannot be serialized as a YAML mapping.");
                    }

                    WriteKeyScalar(_emitter, name, _context);
                    return;
                }

                case JsonTokenType.String:
                    _sawRootValue = true;
                    WriteScalar(_emitter, reader.GetString()!, _context);
                    return;

                case JsonTokenType.Number:
                    _sawRootValue = true;
                    _emitter.Emit(new Scalar(GetRawText(ref reader)));
                    return;

                case JsonTokenType.True:
                    _sawRootValue = true;
                    _emitter.Emit(new Scalar("true"));
                    return;

                case JsonTokenType.False:
                    _sawRootValue = true;
                    _emitter.Emit(new Scalar("false"));
                    return;

                case JsonTokenType.Null:
                    _sawRootValue = true;
                    _emitter.Emit(new Scalar("null"));
                    return;

                default:
                    throw new JsonException($"Unexpected JSON token '{reader.TokenType}'.");
            }
        }

        private byte[] GetBuffer(int sizeHint)
        {
            ObjectDisposedException.ThrowIf(_disposed, nameof(StreamingWriter));
            ObjectDisposedException.ThrowIf(_completed, nameof(StreamingWriter));
            ArgumentOutOfRangeException.ThrowIfNegative(sizeHint, nameof(sizeHint));

            if (sizeHint == 0)
            {
                sizeHint = 1;
            }

            if (_inputLength + sizeHint <= _input.Length)
            {
                return _input;
            }

            var newSize = Math.Max(_inputLength + sizeHint, _input.Length * 2);
            var newInput = ArrayPool<byte>.Shared.Rent(newSize);
            Buffer.BlockCopy(_input, 0, newInput, 0, _inputLength);
            ArrayPool<byte>.Shared.Return(_input);
            _input = newInput;
            return _input;
        }
    }

    internal static EmitterSettings CreateEmitterSettings(YamlSerializerOptions? options)
    {
        var settings = EmitterSettings.Default.WithBestIndent(options?.IndentSize ?? YamlSerializerOptions.DefaultIndentSize);

        if (options is null)
        {
            return settings;
        }

        settings = settings
            .WithBestWidth(options.Width)
            .WithNewLine(options.NewLine)
            .WithMaxSimpleKeyLength(options.MaxSimpleKeyLength);

        return options.IndentedSequences ? settings.WithIndentedSequences() : settings;
    }

    /// <summary>
    /// Writes a mapping key. Simple keys must be single line, so multi line keys are always double
    /// quoted instead of using a block style, and the <see cref="YamlStringStyle.Literal"/>
    /// preference does not apply to keys.
    /// </summary>
    private static void WriteKeyScalar(IEmitter emitter, string value, EmitterContext context)
    {
        if (value == "<<"
            || value.Contains('\n')
            || value.Contains('\r')
            || context.StringStyle == YamlStringStyle.DoubleQuoted)
        {
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.DoubleQuoted, false, true));
        }
        else if (JsonScalarResolver.NeedsQuoting(value, context.Schema))
        {
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.SingleQuoted, false, true));
        }
        else
        {
            emitter.Emit(new Scalar(value));
        }
    }

    private static void WriteScalar(IEmitter emitter, string value, EmitterContext context)
    {
        if (value.Contains('\r'))
        {
            // YAML normalizes literal line breaks; escape CR characters to preserve JSON text.
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.DoubleQuoted, false, true));
        }
        else if (value.Contains('\n') && context.StringStyle != YamlStringStyle.DoubleQuoted)
        {
            // Force a multi line literal block so embedded newlines round-trip exactly.
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.Literal, true, true));
        }
        else if (context.StringStyle == YamlStringStyle.DoubleQuoted)
        {
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.DoubleQuoted, false, true));
        }
        else if (context.StringStyle == YamlStringStyle.Literal)
        {
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.Literal, true, true));
        }
        else if (JsonScalarResolver.NeedsQuoting(value, context.Schema))
        {
            // The value could be misread as null, a boolean or a number; quote it to preserve the string.
            emitter.Emit(new Scalar(default, default, value, ScalarStyle.SingleQuoted, false, true));
        }
        else
        {
            emitter.Emit(new Scalar(value));
        }
    }

    private static string GetRawText(ref Utf8JsonReader reader)
    {
        if (reader.HasValueSequence)
        {
            var sequence = reader.ValueSequence;
            var buffer = ArrayPool<byte>.Shared.Rent((int)sequence.Length);

            try
            {
                sequence.CopyTo(buffer);
                return Encoding.UTF8.GetString(buffer, 0, (int)sequence.Length);
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }

        return Encoding.UTF8.GetString(reader.ValueSpan.ToArray());
    }
}

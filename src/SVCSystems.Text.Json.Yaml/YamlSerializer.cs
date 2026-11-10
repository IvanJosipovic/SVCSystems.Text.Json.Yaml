using System.Buffers;
using System.Diagnostics.CodeAnalysis;
using System.IO.Pipelines;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SVCSystems.Text.Json.Yaml.Internal;
using YamlDotNet.Core;

namespace SVCSystems.Text.Json.Yaml;

/// <summary>
/// Serializes and deserializes YAML using the System.Text.Json serialization infrastructure.
/// </summary>
/// <remarks>
/// The <see cref="YamlSerializer"/> reuses the existing System.Text.Json serialization engine for
/// all .NET object mapping. Serialization goes through <see cref="JsonSerializer"/> (either
/// reflection based with <see cref="JsonSerializerOptions"/>, or metadata based with a
/// <see cref="JsonTypeInfo{T}"/> from a source generated <see cref="JsonSerializerContext"/>),
/// and the resulting JSON token stream is converted to and from YAML events. YamlDotNet is used
/// internally to parse and emit YAML but is otherwise an implementation detail and does not leak
/// into the public API.
/// <para>
/// The supported YAML surface is the YAML 1.2.2 JSON-representable profile: Core and JSON scalar
/// schemas, scalar mapping keys, supported standard tags, multiple documents (via the dedicated
/// APIs), and acyclic anchors and aliases. Merge keys, custom tags, complex mapping keys, and
/// cyclic aliases are rejected because they are not part of this profile or cannot be represented
/// by JSON.
/// </para>
/// <para>
/// The reflection based overloads (those accepting <see cref="JsonSerializerOptions"/>) require
/// unrestricted reflection and are not compatible with Native AOT or trimming. Use the overloads
/// accepting a <see cref="JsonTypeInfo{T}"/> for Native AOT.
/// </para>
/// All methods are thread safe and do not maintain any internal state.
/// </remarks>
/// <example>
/// <code>
/// var person = new Person { Name = "John", Age = 30 };
/// string yaml = YamlSerializer.Serialize(person);
/// Person? result = YamlSerializer.Deserialize&lt;Person&gt;(yaml);
/// </code>
/// </example>
public static class YamlSerializer
{
    /// <summary>A non-seekable UTF-8 prefix adapter that leaves its source stream open.</summary>
    private sealed class PrefixStream(Stream inner, byte[] prefix) : Stream
    {
        private int _prefixOffset;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_prefixOffset < prefix.Length)
            {
                var length = Math.Min(count, prefix.Length - _prefixOffset);
                prefix.AsSpan(_prefixOffset, length).CopyTo(buffer.AsSpan(offset, length));
                _prefixOffset += length;
                return length;
            }

            return inner.Read(buffer, offset, count);
        }

        public override int Read(Span<byte> buffer)
        {
            if (_prefixOffset < prefix.Length)
            {
                var length = Math.Min(buffer.Length, prefix.Length - _prefixOffset);
                prefix.AsSpan(_prefixOffset, length).CopyTo(buffer);
                _prefixOffset += length;
                return length;
            }

            return inner.Read(buffer);
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (_prefixOffset < prefix.Length)
            {
                var length = Math.Min(buffer.Length, prefix.Length - _prefixOffset);
                prefix.AsMemory(_prefixOffset, length).CopyTo(buffer);
                _prefixOffset += length;
                return ValueTask.FromResult(length);
            }

            return inner.ReadAsync(buffer, cancellationToken);
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            // The serializer does not own the caller's stream.
            base.Dispose(disposing);
        }
    }

    #region Serialization

    /// <summary>
    /// Serializes the specified value to a YAML string.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object serialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <returns>A YAML representation of the value.</returns>
    [RequiresUnreferencedCode("JSON serialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON serialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static string Serialize<T>(T value, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null)
    {
        var buffer = new PooledBufferWriter();

        try
        {
            JsonToYamlConverter.Serialize(value, jsonOptions, buffer, yamlOptions);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Serializes the specified value to a YAML string using the provided System.Text.Json contract.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <returns>A YAML representation of the value.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="jsonTypeInfo"/> is null.</exception>
    public static string Serialize<T>(T value, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var buffer = new PooledBufferWriter();

        try
        {
            JsonToYamlConverter.Serialize(value, jsonTypeInfo, buffer, yamlOptions);
            return Encoding.UTF8.GetString(buffer.WrittenSpan);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Serializes the specified value to UTF-8 encoded YAML bytes.
    /// </summary>
    /// <remarks>
    /// Mirrors <see cref="JsonSerializer.SerializeToUtf8Bytes{TValue}(TValue, JsonSerializerOptions)"/> for YAML.
    /// </remarks>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object serialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <returns>A UTF-8 encoded YAML representation of the value.</returns>
    [RequiresUnreferencedCode("JSON serialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON serialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null)
    {
        var buffer = new PooledBufferWriter();

        try
        {
            JsonToYamlConverter.Serialize(value, jsonOptions, buffer, yamlOptions);
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Serializes the specified value to UTF-8 encoded YAML bytes using the provided System.Text.Json contract.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <returns>A UTF-8 encoded YAML representation of the value.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="jsonTypeInfo"/> is null.</exception>
    public static byte[] SerializeToUtf8Bytes<T>(T value, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var buffer = new PooledBufferWriter();

        try
        {
            JsonToYamlConverter.Serialize(value, jsonTypeInfo, buffer, yamlOptions);
            return buffer.WrittenSpan.ToArray();
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Serializes the specified value as YAML to the provided stream using the provided System.Text.Json contract.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="stream">The stream to write YAML to. The stream is not closed or disposed.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    public static void Serialize<T>(Stream stream, T value, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        using var output = new StreamBufferWriter(stream);
        JsonToYamlConverter.Serialize(value, jsonTypeInfo, output, yamlOptions);
    }

    /// <summary>
    /// Serializes the specified value as YAML to the provided stream.
    /// </summary>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="stream">The stream to write YAML to. The stream is not closed or disposed.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object serialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    [RequiresUnreferencedCode("JSON serialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON serialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static void Serialize<T>(Stream stream, T value, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        using var output = new StreamBufferWriter(stream);
        JsonToYamlConverter.Serialize(value, jsonOptions, output, yamlOptions);
    }

    /// <summary>
    /// Serializes the specified value as YAML into the provided <see cref="IBufferWriter{T}"/>
    /// using the provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// The YAML is UTF-8 encoded directly into the buffers supplied by the writer; the writer is
    /// neither closed nor disposed, and is left positioned after the written bytes. JSON tokens
    /// are converted incrementally, so no complete intermediate JSON document is staged.
    /// </remarks>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="writer">The buffer writer to write UTF-8 encoded YAML to.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    public static void Serialize<T>(IBufferWriter<byte> writer, T value, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        JsonToYamlConverter.Serialize(value, jsonTypeInfo, writer, yamlOptions);
    }

    /// <summary>
    /// Serializes multiple values as a multi document YAML stream. Every document is emitted with
    /// an explicit document start marker (<c>---</c>) so the stream unambiguously contains one
    /// document per value.
    /// </summary>
    /// <typeparam name="T">The type of the values to serialize.</typeparam>
    /// <param name="values">The values to serialize, in stream order.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting. <see cref="YamlSerializerOptions.EmitDocumentStart"/> is forced to <see langword="true"/>.</param>
    /// <returns>A multi document YAML stream.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="values"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    public static string SerializeDocuments<T>(IEnumerable<T> values, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(values);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var documentOptions = (yamlOptions ?? new YamlSerializerOptions()).Clone();
        documentOptions.EmitDocumentStart = true;

        var result = new StringBuilder();

        foreach (var value in values)
        {
            var buffer = new PooledBufferWriter();

            try
            {
                JsonToYamlConverter.Serialize(value, jsonTypeInfo, buffer, documentOptions);
                result.Append(Encoding.UTF8.GetString(buffer.WrittenSpan));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        return result.ToString();
    }

    #endregion

    #region Deserialization

    /// <summary>
    /// Deserializes a YAML string into an instance of type <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="yaml">The YAML string to deserialize.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object deserialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    [RequiresUnreferencedCode("JSON deserialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON deserialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static T? Deserialize<T>(string yaml, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);

        var buffer = new PooledBufferWriter();

        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                var parser = CreateParser(yaml, yamlOptions);
                YamlToJsonConverter.Convert(parser, writer, yamlOptions);
            }

            return JsonSerializer.Deserialize<T>(buffer.WrittenSpan, jsonOptions);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Deserializes a YAML string into an instance of type <typeparamref name="T"/> using the provided System.Text.Json contract.
    /// </summary>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="yaml">The YAML string to deserialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    public static T? Deserialize<T>(string yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var buffer = new PooledBufferWriter();

        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                var parser = CreateParser(yaml, yamlOptions);
                YamlToJsonConverter.Convert(parser, writer, yamlOptions);
            }

            return JsonSerializer.Deserialize(buffer.WrittenSpan, jsonTypeInfo);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Deserializes the first document of a YAML stream into an instance of type
    /// <typeparamref name="T"/>. Any further documents in the stream are ignored.
    /// </summary>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="yaml">The YAML stream to deserialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the first document.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid or uses unsupported YAML features.</exception>
    public static T? DeserializeFirstDocument<T>(string yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var buffer = new PooledBufferWriter();

        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                var parser = CreateParser(yaml, yamlOptions);

                if (!YamlToJsonConverter.ConvertNextDocument(parser, writer, yamlOptions))
                {
                    throw new YamlException("The YAML input does not contain a document.");
                }
            }

            return JsonSerializer.Deserialize(buffer.WrittenSpan, jsonTypeInfo);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Deserializes a YAML stream into an instance of type <typeparamref name="T"/> using the provided System.Text.Json contract.
    /// </summary>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="stream">The stream to read YAML from. The stream is not closed or disposed.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    /// <exception cref="DecoderFallbackException">Thrown when the stream contains malformed UTF-8.</exception>
    public static T? Deserialize<T>(Stream stream, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        return DeserializeFromPipe(
            output =>
            {
                using (var writer = new Utf8JsonWriter(output))
                {
                    using var reader = CreateStrictUtf8StreamReader(stream);
                    var parser = CreateParser(reader, yamlOptions);
                    YamlToJsonConverter.Convert(parser, writer, yamlOptions);
                }
            },
            json => JsonSerializer.Deserialize(json.Span, jsonTypeInfo),
            jsonStream => JsonSerializer.Deserialize(jsonStream, jsonTypeInfo));
    }

    /// <summary>
    /// Deserializes a YAML stream read from the provided stream into an instance of type
    /// <typeparamref name="T"/>.
    /// </summary>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="stream">The stream to read YAML from. The stream is not closed or disposed.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object deserialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    /// <exception cref="DecoderFallbackException">Thrown when the stream contains malformed UTF-8.</exception>
    [RequiresUnreferencedCode("JSON deserialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON deserialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static T? Deserialize<T>(Stream stream, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);

        return DeserializeFromPipe(
            output =>
            {
                using (var writer = new Utf8JsonWriter(output))
                {
                    using var reader = CreateStrictUtf8StreamReader(stream);
                    var parser = CreateParser(reader, yamlOptions);
                    YamlToJsonConverter.Convert(parser, writer, yamlOptions);
                }
            },
            json => JsonSerializer.Deserialize<T>(json.Span, jsonOptions),
            jsonStream => JsonSerializer.Deserialize<T>(jsonStream, jsonOptions));
    }

    /// <summary>
    /// Deserializes UTF-8 encoded YAML bytes into an instance of type <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// The bytes are decoded incrementally into characters as the YAML parser pulls them, so no
    /// whole input UTF-16 string is materialized. A leading UTF-8 byte order mark is skipped;
    /// malformed UTF-8 is rejected.
    /// </remarks>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="utf8Yaml">The UTF-8 encoded YAML to deserialize. The input is not modified.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object deserialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    /// <exception cref="DecoderFallbackException">Thrown when the input contains malformed UTF-8.</exception>
    [RequiresUnreferencedCode("JSON deserialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON deserialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static T? Deserialize<T>(ReadOnlyMemory<byte> utf8Yaml, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null)
    {
        return Deserialize<T>(utf8Yaml.Span, jsonOptions, yamlOptions);
    }

    /// <summary>
    /// Deserializes UTF-8 encoded YAML bytes into an instance of type <typeparamref name="T"/>
    /// using the provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// The bytes are decoded incrementally into characters as the YAML parser pulls them, so no
    /// whole input UTF-16 string is materialized. A leading UTF-8 byte order mark is skipped;
    /// malformed UTF-8 is rejected.
    /// </remarks>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="utf8Yaml">The UTF-8 encoded YAML to deserialize. The input is not modified.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    /// <exception cref="DecoderFallbackException">Thrown when the input contains malformed UTF-8.</exception>
    public static T? Deserialize<T>(ReadOnlyMemory<byte> utf8Yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        return Deserialize(new ReadOnlySequence<byte>(utf8Yaml), jsonTypeInfo, yamlOptions);
    }

    /// <summary>
    /// Deserializes UTF-8 encoded YAML bytes into an instance of type <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// The bytes are decoded incrementally into characters as the YAML parser pulls them, so no
    /// whole input UTF-16 string is materialized. A leading UTF-8 byte order mark is skipped;
    /// malformed UTF-8 is rejected.
    /// </remarks>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="utf8Yaml">The UTF-8 encoded YAML to deserialize. The input is not modified.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object deserialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    /// <exception cref="DecoderFallbackException">Thrown when the input contains malformed UTF-8.</exception>
    [RequiresUnreferencedCode("JSON deserialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON deserialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static T? Deserialize<T>(ReadOnlySpan<byte> utf8Yaml, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null)
    {
        // The input span is staged in a pooled buffer (the YAML scanner reads the input more than
        // once, and a span cannot be re-read), then decoded incrementally.
        var staging = new PooledBufferWriter();
        var buffer = new PooledBufferWriter();

        try
        {
            var span = staging.GetSpan(utf8Yaml.Length);
            utf8Yaml.CopyTo(span);
            staging.Advance(utf8Yaml.Length);

            using (var writer = new Utf8JsonWriter(buffer))
            {
                using var reader = new SequenceTextReader(staging.WrittenMemory);
                var parser = CreateParser(reader, yamlOptions);
                YamlToJsonConverter.Convert(parser, writer, yamlOptions);
            }

            return JsonSerializer.Deserialize<T>(buffer.WrittenSpan, jsonOptions);
        }
        finally
        {
            staging.Dispose();
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Deserializes UTF-8 encoded YAML bytes into an instance of type <typeparamref name="T"/>
    /// using the provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// The bytes are decoded incrementally into characters as the YAML parser pulls them, so no
    /// whole input UTF-16 string is materialized. A leading UTF-8 byte order mark is skipped.
    /// </remarks>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="utf8Yaml">The UTF-8 encoded YAML to deserialize. The input is not modified.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>An instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    public static T? Deserialize<T>(ReadOnlySequence<byte> utf8Yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var buffer = new PooledBufferWriter();

        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                using var reader = new SequenceTextReader(utf8Yaml);
                var parser = CreateParser(reader, yamlOptions);
                YamlToJsonConverter.Convert(parser, writer, yamlOptions);
            }

            return JsonSerializer.Deserialize(buffer.WrittenSpan, jsonTypeInfo);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    /// <summary>
    /// Deserializes all documents of a YAML stream. One .NET value is produced per document.
    /// </summary>
    /// <typeparam name="T">The type of the values to deserialize.</typeparam>
    /// <param name="yaml">The YAML stream to deserialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <returns>The deserialized documents, in stream order. An empty stream produces an empty list.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid or uses unsupported YAML features.</exception>
    public static List<T?> DeserializeDocuments<T>(string yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var results = new List<T?>();

        var parser = CreateParser(yaml, yamlOptions);

        while (true)
        {
            var buffer = new PooledBufferWriter();

            try
            {
                bool hasDocument;

                using (var writer = new Utf8JsonWriter(buffer))
                {
                    hasDocument = YamlToJsonConverter.ConvertNextDocument(parser, writer, yamlOptions);
                }

                if (!hasDocument)
                {
                    break;
                }

                results.Add(JsonSerializer.Deserialize(buffer.WrittenSpan, jsonTypeInfo));
            }
            finally
            {
                buffer.Dispose();
            }
        }

        return results;
    }

    #endregion

    #region Asynchronous APIs

    /// <summary>
    /// Serializes the specified value as YAML to the provided stream asynchronously using the
    /// provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// Small YAML results are staged in pooled memory; results exceeding the bounded staging
    /// limit spill incrementally through a pipe. The stream write is asynchronous and honors
    /// <paramref name="cancellationToken"/>; object serialization and YAML conversion are
    /// synchronous CPU work subject to destination backpressure.
    /// </remarks>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="stream">The stream to write YAML to. The stream is not closed or disposed.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <param name="cancellationToken">A token that cancels the stream write.</param>
    /// <returns>A task that completes when the YAML has been written.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    public static async Task SerializeAsync<T>(Stream stream, T value, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        await SerializeToPipeAsync(
            stream,
            output => JsonToYamlConverter.Serialize(value, jsonTypeInfo, output, yamlOptions),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes the specified value as YAML to the provided stream asynchronously.
    /// </summary>
    /// <remarks>
    /// Small YAML results are staged in pooled memory; results exceeding the bounded staging
    /// limit spill incrementally through a pipe. The stream write is asynchronous and honors
    /// <paramref name="cancellationToken"/>; object serialization and YAML conversion are
    /// synchronous CPU work subject to destination backpressure.
    /// </remarks>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="stream">The stream to write YAML to. The stream is not closed or disposed.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object serialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <param name="cancellationToken">A token that cancels the stream write.</param>
    /// <returns>A task that completes when the YAML has been written.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    [RequiresUnreferencedCode("JSON serialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON serialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static async Task SerializeAsync<T>(Stream stream, T value, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        await SerializeToPipeAsync(
            stream,
            output => JsonToYamlConverter.Serialize(value, jsonOptions, output, yamlOptions),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Serializes the specified value as YAML into the provided <see cref="IBufferWriter{T}"/>
    /// asynchronously using the provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// Writing to an <see cref="IBufferWriter{T}"/> is synchronous CPU work; the method completes
    /// synchronously (the returned task is already completed) and
    /// <paramref name="cancellationToken"/> is checked before serialization begins.
    /// </remarks>
    /// <typeparam name="T">The type of the value to serialize.</typeparam>
    /// <param name="writer">The buffer writer to write UTF-8 encoded YAML to.</param>
    /// <param name="value">The value to serialize.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML formatting.</param>
    /// <param name="cancellationToken">A token checked before serialization begins.</param>
    /// <returns>A task that completes when the YAML has been written.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="writer"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    public static Task SerializeAsync<T>(IBufferWriter<byte> writer, T value, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Serialize(writer, value, jsonTypeInfo, yamlOptions);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Deserializes a YAML stream into an instance of type <typeparamref name="T"/> using the
    /// provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// The YAML input is staged in pooled UTF-8 buffers because YamlDotNet's parser is synchronous.
    /// Small converted JSON results are staged in pooled memory; larger results flow through a
    /// bounded pipe to JSON deserialization. Stream reads honor
    /// <paramref name="cancellationToken"/>.
    /// </remarks>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="stream">The stream to read YAML from. The stream is not closed or disposed.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <param name="cancellationToken">A token that cancels the stream read.</param>
    /// <returns>A task that completes with an instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    public static async Task<T?> DeserializeAsync<T>(Stream stream, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        // The UTF-8 input is staged in pooled buffers (the YAML scanner reads the input more than
        // once) and decoded incrementally into characters as the parser pulls them; no whole
        // input UTF-16 string is materialized. A leading UTF-8 byte order mark is skipped by the
        // reader, matching the previous decoded-BOM handling.
        var yamlBuffer = new PooledBufferWriter();

        try
        {
            await stream.CopyToAsync(new PooledBufferStream(yamlBuffer), cancellationToken).ConfigureAwait(false);

            return await DeserializeFromPipeAsync(
                output =>
                {
                    using (var writer = new Utf8JsonWriter(output))
                    {
                        using var reader = new SequenceTextReader(yamlBuffer.WrittenMemory, cancellationToken);
                        var parser = CreateParser(reader, yamlOptions);
                        YamlToJsonConverter.Convert(parser, writer, yamlOptions);
                    }
                },
                json => JsonSerializer.Deserialize(json.Span, jsonTypeInfo),
                (jsonStream, token) => JsonSerializer.DeserializeAsync(jsonStream, jsonTypeInfo, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            yamlBuffer.Dispose();
        }
    }

    /// <summary>
    /// Deserializes a YAML stream read from the provided stream asynchronously into an instance
    /// of type <typeparamref name="T"/>.
    /// </summary>
    /// <remarks>
    /// The YAML input is staged in pooled UTF-8 buffers because YamlDotNet's parser is synchronous.
    /// Small converted JSON results are staged in pooled memory; larger results flow through a
    /// bounded pipe to JSON deserialization. Stream reads honor
    /// <paramref name="cancellationToken"/>.
    /// </remarks>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="stream">The stream to read YAML from. The stream is not closed or disposed.</param>
    /// <param name="jsonOptions">The <see cref="JsonSerializerOptions"/> controlling .NET object deserialization.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <param name="cancellationToken">A token that cancels the stream read.</param>
    /// <returns>A task that completes with an instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="stream"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    [RequiresUnreferencedCode("JSON deserialization uses reflection and cannot be statically analyzed. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    [RequiresDynamicCode("JSON deserialization may require dynamic code. Use the overload accepting a JsonTypeInfo<T> for trimming and Native AOT support.")]
    public static async Task<T?> DeserializeAsync<T>(Stream stream, JsonSerializerOptions? jsonOptions = null, YamlSerializerOptions? yamlOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(stream);

        var yamlBuffer = new PooledBufferWriter();

        try
        {
            await stream.CopyToAsync(new PooledBufferStream(yamlBuffer), cancellationToken).ConfigureAwait(false);

            return await DeserializeFromPipeAsync(
                output =>
                {
                    using (var writer = new Utf8JsonWriter(output))
                    {
                        using var reader = new SequenceTextReader(yamlBuffer.WrittenMemory, cancellationToken);
                        var parser = CreateParser(reader, yamlOptions);
                        YamlToJsonConverter.Convert(parser, writer, yamlOptions);
                    }
                },
                json => JsonSerializer.Deserialize<T>(json.Span, jsonOptions),
                (jsonStream, token) => JsonSerializer.DeserializeAsync<T>(jsonStream, jsonOptions, token),
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            yamlBuffer.Dispose();
        }
    }

    /// <summary>
    /// Deserializes UTF-8 encoded YAML bytes into an instance of type <typeparamref name="T"/>
    /// using the provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// Parsing UTF-8 bytes is synchronous CPU work; the method completes synchronously (the
    /// returned task is already completed) and <paramref name="cancellationToken"/> is checked
    /// before parsing begins. See the synchronous overload for the byte input semantics.
    /// </remarks>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="utf8Yaml">The UTF-8 encoded YAML to deserialize. The input is not modified.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <param name="cancellationToken">A token checked before parsing begins.</param>
    /// <returns>A task that completes with an instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    public static Task<T?> DeserializeAsync<T>(ReadOnlyMemory<byte> utf8Yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Deserialize(utf8Yaml, jsonTypeInfo, yamlOptions));
    }

    /// <summary>
    /// Deserializes UTF-8 encoded YAML bytes into an instance of type <typeparamref name="T"/>
    /// using the provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// Parsing UTF-8 bytes is synchronous CPU work; the method completes synchronously (the
    /// returned task is already completed) and <paramref name="cancellationToken"/> is checked
    /// before parsing begins. See the synchronous overload for the byte input semantics.
    /// </remarks>
    /// <typeparam name="T">The type of the value to deserialize.</typeparam>
    /// <param name="utf8Yaml">The UTF-8 encoded YAML to deserialize. The input is not modified.</param>
    /// <param name="jsonTypeInfo">The <see cref="JsonTypeInfo{T}"/> contract, typically obtained from a source generated <see cref="JsonSerializerContext"/>.</param>
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing.</param>
    /// <param name="cancellationToken">A token checked before parsing begins.</param>
    /// <returns>A task that completes with an instance of <typeparamref name="T"/> populated from the YAML input.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains multiple documents.</exception>
    public static Task<T?> DeserializeAsync<T>(ReadOnlySequence<byte> utf8Yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return Task.FromResult(Deserialize(utf8Yaml, jsonTypeInfo, yamlOptions));
    }

    #endregion

    private const int InMemoryJsonThreshold = 128 * 1024;

    private static Pipe CreateBoundedPipe() => new(new PipeOptions(
        pauseWriterThreshold: 64 * 1024,
        resumeWriterThreshold: 32 * 1024,
        minimumSegmentSize: 4096,
        useSynchronizationContext: false));

    private static T? DeserializeFromPipe<T>(
        Action<IBufferWriter<byte>> convertYaml,
        Func<ReadOnlyMemory<byte>, T?> deserializeBufferedJson,
        Func<Stream, T?> deserializeJsonStream)
    {
        Pipe? pipe = null;
        Task<T?>? deserializeTask = null;
        var writerCompleted = false;

        using var output = new ThresholdBufferWriter(InMemoryJsonThreshold, () =>
        {
            pipe = CreateBoundedPipe();
            var currentPipe = pipe;
            deserializeTask = Task.Run(() =>
            {
                using var jsonStream = currentPipe.Reader.AsStream(leaveOpen: true);
                var result = deserializeJsonStream(jsonStream);
                DrainSynchronously(jsonStream);
                return result;
            });

            return new PipeBufferWriter(currentPipe.Writer);
        });

        try
        {
            try
            {
                convertYaml(output);
                output.Flush();
            }
            catch (Exception exception)
            {
                if (pipe is not null)
                {
                    pipe.Writer.CancelPendingFlush();
                    pipe.Writer.Complete(exception);
                    writerCompleted = true;

                    try
                    {
                        deserializeTask!.GetAwaiter().GetResult();
                    }
                    catch
                    {
                        // Preserve the YAML conversion failure, which precedes JSON deserialization.
                    }
                }

                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }

            if (pipe is null)
            {
                return deserializeBufferedJson(output.WrittenMemory);
            }

            pipe.Writer.Complete();
            writerCompleted = true;
            return deserializeTask!.GetAwaiter().GetResult();
        }
        finally
        {
            if (pipe is not null)
            {
                if (!writerCompleted)
                {
                    pipe.Writer.Complete();
                }

                pipe.Reader.Complete();
            }
        }
    }

    private static void DrainSynchronously(Stream stream)
    {
        Span<byte> buffer = stackalloc byte[4096];
        while (stream.Read(buffer) != 0)
        {
        }
    }

    private sealed class PipeCopyState
    {
        public Exception? DestinationException { get; set; }
    }

    private static async Task SerializeToPipeAsync(
        Stream destination,
        Action<IBufferWriter<byte>> serialize,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Pipe? pipe = null;
        PipeCopyState? copyState = null;
        Task? copyTask = null;
        var writerCompleted = false;
        using var output = new ThresholdBufferWriter(InMemoryJsonThreshold, () =>
        {
            pipe = CreateBoundedPipe();
            copyState = new PipeCopyState();
            copyTask = CopyPipeToStreamAsync(pipe.Reader, destination, cancellationToken, copyState);
            return new PipeBufferWriter(pipe.Writer, cancellationToken);
        });

        try
        {
            serialize(output);
            output.Flush();

            if (pipe is null)
            {
                await destination.WriteAsync(output.WrittenMemory, cancellationToken).ConfigureAwait(false);
                return;
            }

            pipe.Writer.Complete();
            writerCompleted = true;
            await copyTask!.ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            if (pipe is null)
            {
                throw;
            }

            pipe.Writer.CancelPendingFlush();

            if (!writerCompleted)
            {
                pipe.Writer.Complete(exception);
                writerCompleted = true;
            }

            try
            {
                await copyTask!.ConfigureAwait(false);
            }
            catch
            {
                // The original conversion or destination exception is rethrown below.
            }

            ExceptionDispatchInfo.Capture(copyState?.DestinationException ?? exception).Throw();
            throw;
        }
        finally
        {
            if (pipe is not null && !writerCompleted)
            {
                pipe.Writer.Complete();
            }
        }
    }

    private static async Task CopyPipeToStreamAsync(
        PipeReader reader,
        Stream destination,
        CancellationToken cancellationToken,
        PipeCopyState copyState)
    {
        Exception? completionException = null;

        try
        {
            while (true)
            {
                var readResult = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
                var buffer = readResult.Buffer;

                try
                {
                    if (readResult.IsCanceled)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        throw new OperationCanceledException(cancellationToken);
                    }

                    foreach (var segment in buffer)
                    {
                        if (segment.IsEmpty)
                        {
                            continue;
                        }

                        try
                        {
                            await destination.WriteAsync(segment, cancellationToken).ConfigureAwait(false);
                        }
                        catch (Exception exception)
                        {
                            copyState.DestinationException = exception;
                            throw;
                        }
                    }
                }
                finally
                {
                    reader.AdvanceTo(buffer.End);
                }

                if (readResult.IsCompleted)
                {
                    return;
                }
            }
        }
        catch (Exception exception)
        {
            completionException = exception;
            throw;
        }
        finally
        {
            reader.Complete(completionException);
        }
    }

    private static async Task<T?> DeserializeFromPipeAsync<T>(
        Action<IBufferWriter<byte>> convertYaml,
        Func<ReadOnlyMemory<byte>, T?> deserializeBufferedJson,
        Func<Stream, CancellationToken, ValueTask<T?>> deserializeJson,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        Pipe? pipe = null;
        Task<T?>? deserializeTask = null;
        var writerCompleted = false;
        using var output = new ThresholdBufferWriter(InMemoryJsonThreshold, () =>
        {
            pipe = CreateBoundedPipe();
            deserializeTask = DeserializeAndDrainAsync(pipe.Reader, deserializeJson, cancellationToken);
            return new PipeBufferWriter(pipe.Writer, cancellationToken);
        });

        try
        {
            try
            {
                convertYaml(output);
                output.Flush();
            }
            catch (Exception exception)
            {
                if (pipe is not null)
                {
                    pipe.Writer.CancelPendingFlush();
                    pipe.Writer.Complete(exception);
                    writerCompleted = true;

                    try
                    {
                        await deserializeTask!.ConfigureAwait(false);
                    }
                    catch
                    {
                        // Preserve the YAML conversion failure, which precedes JSON deserialization.
                    }
                }

                ExceptionDispatchInfo.Capture(exception).Throw();
                throw;
            }

            if (pipe is null)
            {
                cancellationToken.ThrowIfCancellationRequested();
                return deserializeBufferedJson(output.WrittenMemory);
            }

            pipe.Writer.Complete();
            writerCompleted = true;
            return await deserializeTask!.ConfigureAwait(false);
        }
        finally
        {
            if (pipe is not null)
            {
                if (!writerCompleted)
                {
                    pipe.Writer.Complete();
                }

                pipe.Reader.Complete();
            }
        }
    }

    private static async Task<T?> DeserializeAndDrainAsync<T>(
        PipeReader reader,
        Func<Stream, CancellationToken, ValueTask<T?>> deserializeJson,
        CancellationToken cancellationToken)
    {
        using var jsonStream = reader.AsStream(leaveOpen: true);
        T? result;

        try
        {
            result = await deserializeJson(jsonStream, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            try
            {
                await DrainAsync(jsonStream, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // Keep the original JSON deserialization exception. YAML producer errors are
                // surfaced independently by DeserializeFromPipeAsync.
            }

            throw;
        }

        await DrainAsync(jsonStream, cancellationToken).ConfigureAwait(false);
        return result;
    }

    private static async Task DrainAsync(Stream stream, CancellationToken cancellationToken)
    {
        var buffer = ArrayPool<byte>.Shared.Rent(4096);

        try
        {
            while (await stream.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false) != 0)
            {
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static StreamReader CreateStrictUtf8StreamReader(Stream stream)
    {
        Span<byte> prefix = stackalloc byte[3];
        var bytesRead = 0;

        while (bytesRead < prefix.Length)
        {
            var read = stream.Read(prefix[bytesRead..]);
            if (read == 0) break;
            bytesRead += read;
        }

        var hasBom = bytesRead == 3
            && prefix[0] == 0xEF
            && prefix[1] == 0xBB
            && prefix[2] == 0xBF;
        var replay = hasBom ? [] : prefix[..bytesRead].ToArray();
        var prefixedStream = new PrefixStream(stream, replay);

        return new StreamReader(
            prefixedStream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: false);
    }

    private static IParser CreateParser(string yaml, YamlSerializerOptions? yamlOptions)
    {
        return CreateParser(new StringReader(yaml), yamlOptions);
    }

    private static IParser CreateParser(TextReader reader, YamlSerializerOptions? yamlOptions)
    {
        var parser = new Parser(reader);

        return new AliasExpandingParser(
            parser,
            yamlOptions?.MaxAliasExpansionEvents ?? YamlSerializerOptions.DefaultMaxAliasExpansionEvents);
    }
}

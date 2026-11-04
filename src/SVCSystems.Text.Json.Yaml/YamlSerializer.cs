using System.Diagnostics.CodeAnalysis;
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
/// The supported YAML surface is the JSON compatible subset plus the structural features of
/// YAML 1.2 that map losslessly into the JSON data model: multiple documents (via the dedicated
/// APIs), anchors and aliases, merge keys, explicit keys and the standard YAML tags. See the
/// project README and <see cref="YamlSerializerOptions"/> for the supported scalar semantics and
/// configurable policies.
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
            using (var writer = new Utf8JsonWriter(buffer))
            {
                JsonSerializer.Serialize(writer, value, jsonOptions);
            }

            return JsonToYamlConverter.ConvertToString(buffer.WrittenSpan, yamlOptions);
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
            using (var writer = new Utf8JsonWriter(buffer))
            {
                JsonSerializer.Serialize(writer, value, jsonTypeInfo);
            }

            return JsonToYamlConverter.ConvertToString(buffer.WrittenSpan, yamlOptions);
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

        // The YAML document is encoded into pooled UTF-8 buffers and written with a single call,
        // mirroring SerializeAsync (batched write) instead of chunked streaming.
        var jsonBuffer = new PooledBufferWriter();

        try
        {
            using (var writer = new Utf8JsonWriter(jsonBuffer))
            {
                JsonSerializer.Serialize(writer, value, jsonTypeInfo);
            }

            var yamlBuffer = new PooledBufferWriter();

            try
            {
                using (var streamWriter = new StreamWriter(new PooledBufferStream(yamlBuffer), JsonToYamlConverter.Utf8NoBom, bufferSize: 1024, leaveOpen: true))
                {
                    JsonToYamlConverter.Convert(jsonBuffer.WrittenSpan, streamWriter, yamlOptions);
                }

                stream.Write(yamlBuffer.WrittenSpan);
            }
            finally
            {
                yamlBuffer.Dispose();
            }
        }
        finally
        {
            jsonBuffer.Dispose();
        }
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
                using (var writer = new Utf8JsonWriter(buffer))
                {
                    JsonSerializer.Serialize(writer, value, jsonTypeInfo);
                }

                result.Append(JsonToYamlConverter.ConvertToString(buffer.WrittenSpan, documentOptions));
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
        ValidateInputLength(yaml, yamlOptions);

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
        ValidateInputLength(yaml, yamlOptions);

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
        ValidateInputLength(yaml, yamlOptions);

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
    public static T? Deserialize<T>(Stream stream, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var buffer = new PooledBufferWriter();

        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                using var reader = new StreamReader(stream, System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 1024, leaveOpen: true);
                var input = new LengthCheckingTextReader(reader, yamlOptions?.MaxInputLength ?? 0);
                var parser = CreateParser(input, yamlOptions);
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
    /// <param name="yamlOptions">The <see cref="YamlSerializerOptions"/> controlling YAML parsing. <see cref="YamlSerializerOptions.MaxDocuments"/> bounds the number of documents.</param>
    /// <returns>The deserialized documents, in stream order. An empty stream produces an empty list.</returns>
    /// <exception cref="ArgumentNullException">Thrown when <paramref name="yaml"/> or <paramref name="jsonTypeInfo"/> is null.</exception>
    /// <exception cref="YamlException">Thrown when the YAML is invalid, uses unsupported YAML features, or contains more than <see cref="YamlSerializerOptions.MaxDocuments"/> documents.</exception>
    public static List<T?> DeserializeDocuments<T>(string yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);
        ValidateInputLength(yaml, yamlOptions);

        var maxDocuments = yamlOptions?.MaxDocuments ?? YamlSerializerOptions.DefaultMaxDocuments;
        var results = new List<T?>();

        var parser = CreateParser(yaml, yamlOptions);

        while (true)
        {
            if (results.Count >= maxDocuments)
            {
                throw new YamlException(
                    $"The YAML stream contains more than {maxDocuments} documents. Adjust {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.MaxDocuments)} to allow more.");
            }

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
    /// The stream write is asynchronous and honors <paramref name="cancellationToken"/>. Object
    /// serialization and YAML conversion are synchronous CPU work.
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

        var jsonBuffer = new PooledBufferWriter();

        try
        {
            using (var writer = new Utf8JsonWriter(jsonBuffer))
            {
                JsonSerializer.Serialize(writer, value, jsonTypeInfo);
            }

            var yamlBuffer = new PooledBufferWriter();

            try
            {
                using (var streamWriter = new StreamWriter(new PooledBufferStream(yamlBuffer), JsonToYamlConverter.Utf8NoBom, bufferSize: 1024, leaveOpen: true))
                {
                    JsonToYamlConverter.Convert(jsonBuffer.WrittenSpan, streamWriter, yamlOptions);
                }

                await stream.WriteAsync(yamlBuffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                yamlBuffer.Dispose();
            }
        }
        finally
        {
            jsonBuffer.Dispose();
        }
    }

    /// <summary>
    /// Deserializes a YAML stream into an instance of type <typeparamref name="T"/> using the
    /// provided System.Text.Json contract.
    /// </summary>
    /// <remarks>
    /// The stream read is asynchronous and honors <paramref name="cancellationToken"/>. YAML
    /// parsing and object deserialization are synchronous CPU work performed after the stream has
    /// been read.
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

        var yamlBuffer = new PooledBufferWriter();

        try
        {
            await stream.CopyToAsync(new PooledBufferStream(yamlBuffer), cancellationToken).ConfigureAwait(false);

            var yaml = Encoding.UTF8.GetString(yamlBuffer.WrittenSpan);

            if (yaml.Length > 0 && yaml[0] == '\uFEFF')
            {
                yaml = yaml[1..];
            }

            ValidateInputLength(yaml, yamlOptions);

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
        finally
        {
            yamlBuffer.Dispose();
        }
    }

    #endregion

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

    private static void ValidateInputLength(string yaml, YamlSerializerOptions? yamlOptions)
    {
        var maxInputLength = yamlOptions?.MaxInputLength ?? 0;

        if (maxInputLength > 0 && yaml.Length > maxInputLength)
        {
            throw new YamlException(
                $"The YAML input of {yaml.Length} characters exceeds the maximum input length of {maxInputLength}. Adjust {nameof(YamlSerializerOptions)}.{nameof(YamlSerializerOptions.MaxInputLength)} to allow larger inputs.");
        }
    }
}

using System.Diagnostics.CodeAnalysis;
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
/// A JSON compatible subset of YAML is supported; see the project README for the supported scalar
/// semantics and unsupported features.
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
    /// <exception cref="YamlException">Thrown when the YAML is invalid or uses unsupported YAML features.</exception>
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
                var parser = new Parser(new StringReader(yaml));
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
    /// <exception cref="YamlException">Thrown when the YAML is invalid or uses unsupported YAML features.</exception>
    public static T? Deserialize<T>(string yaml, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)
    {
        ArgumentNullException.ThrowIfNull(yaml);
        ArgumentNullException.ThrowIfNull(jsonTypeInfo);

        var buffer = new PooledBufferWriter();

        try
        {
            using (var writer = new Utf8JsonWriter(buffer))
            {
                var parser = new Parser(new StringReader(yaml));
                YamlToJsonConverter.Convert(parser, writer, yamlOptions);
            }

            return JsonSerializer.Deserialize(buffer.WrittenSpan, jsonTypeInfo);
        }
        finally
        {
            buffer.Dispose();
        }
    }

    #endregion
}

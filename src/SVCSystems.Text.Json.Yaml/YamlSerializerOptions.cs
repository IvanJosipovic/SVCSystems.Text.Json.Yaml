namespace SVCSystems.Text.Json.Yaml;

/// <summary>
/// YAML specific serialization settings.
/// </summary>
/// <remarks>
/// This type owns only YAML formatting concerns. Everything that concerns .NET object mapping
/// (property naming, converters, polymorphism, reference handling, null handling, number
/// handling and so on) is owned by <see cref="System.Text.Json.JsonSerializerOptions"/> and is
/// intentionally not duplicated here.
/// <para>
/// Instances are not thread safe for mutation, but all properties may be read concurrently once
/// the instance is no longer modified.
/// </para>
/// </remarks>
public sealed class YamlSerializerOptions
{
    /// <summary>The default indentation applied when no options are supplied.</summary>
    internal const int DefaultIndentation = 2;

    private int _indentation = DefaultIndentation;
    private YamlScalarSchema _scalarSchema = YamlScalarSchema.Json;

    /// <summary>
    /// Gets or sets the number of spaces used to indent nested block collections. Valid values are
    /// 1 to 9, matching the YAML specification's maximum block indentation.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not between 1 and 9.</exception>
    public int Indentation
    {
        get => _indentation;
        set
        {
            if (value is < 1 or > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "YAML indentation must be between 1 and 9.");
            }

            _indentation = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether an explicit document start marker (<c>---</c>) is
    /// emitted before the YAML document.
    /// </summary>
    public bool EmitDocumentStart { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether mappings and sequences are emitted in flow style
    /// (<c>{a: 1}</c>, <c>[1, 2]</c>) instead of block style.
    /// </summary>
    public bool UseFlowStyle { get; set; }

    /// <summary>
    /// Gets or sets the schema used to resolve unquoted YAML scalars when deserializing.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not a defined <see cref="YamlScalarSchema"/>.</exception>
    public YamlScalarSchema ScalarSchema
    {
        get => _scalarSchema;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The scalar schema is not valid.");
            }

            _scalarSchema = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether duplicate mapping keys are allowed when
    /// deserializing. When <see langword="false"/> (the default), a duplicate key produces a
    /// <see cref="YamlDotNet.Core.YamlException"/>. When <see langword="true"/>, the last value
    /// wins, matching the behavior of common YAML loaders.
    /// </summary>
    public bool AllowDuplicateKeys { get; set; }
}

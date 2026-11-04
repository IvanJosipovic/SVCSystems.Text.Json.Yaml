namespace SVCSystems.Text.Json.Yaml;

/// <summary>
/// YAML specific serialization settings.
/// </summary>
/// <remarks>
/// This type owns only YAML formatting and parsing concerns. Everything that concerns .NET
/// object mapping (property naming, converters, polymorphism, reference handling, null
/// handling, number handling and so on) is owned by
/// <see cref="System.Text.Json.JsonSerializerOptions"/> and is intentionally not duplicated
/// here.
/// <para>
/// Instances are not thread safe for mutation, but all properties may be read concurrently
/// once the instance is no longer modified.
/// </para>
/// </remarks>
public sealed class YamlSerializerOptions
{
    /// <summary>The default indentation applied when no options are supplied.</summary>
    internal const int DefaultIndentation = 2;

    /// <summary>The default maximum nesting depth.</summary>
    internal const int DefaultMaxDepth = 64;

    /// <summary>The default alias expansion budget, in events.</summary>
    internal const int DefaultMaxAliasExpansionEvents = 1_000_000;

    /// <summary>The default maximum number of documents per call.</summary>
    internal const int DefaultMaxDocuments = 16;

    private int _indentation = DefaultIndentation;
    private YamlScalarSchema _scalarSchema = YamlScalarSchema.Core;
    private int _maxDepth = DefaultMaxDepth;
    private int _maxInputLength;
    private int _maxScalarLength;
    private int _maxAliasExpansionEvents = DefaultMaxAliasExpansionEvents;
    private int _maxDocuments = DefaultMaxDocuments;

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

    /// <summary>
    /// Gets or sets a value indicating whether merge keys (<c>&lt;&lt;</c>) are applied when
    /// deserializing. When <see langword="true"/> (the default), the merged mappings are spliced
    /// into the containing mapping following the YAML specification: keys of the containing
    /// mapping win, and among a merge key sequence the first source wins. When
    /// <see langword="false"/>, a merge key produces a <see cref="YamlDotNet.Core.YamlException"/>.
    /// </summary>
    public bool AllowMergeKeys { get; set; } = true;

    /// <summary>
    /// Gets or sets the behavior for YAML special floats (<c>.inf</c>, <c>-.inf</c>,
    /// <c>.nan</c>) during deserialization. The default is
    /// <see cref="SpecialFloatBehavior.Reject"/> because they have no JSON representation.
    /// </summary>
    public SpecialFloatBehavior SpecialFloats { get; set; }

    /// <summary>
    /// Gets or sets the behavior for unknown (non standard) YAML tags. The default is
    /// <see cref="UnknownTagBehavior.Reject"/>. Tags never cause .NET type construction
    /// regardless of this setting.
    /// </summary>
    public UnknownTagBehavior UnknownTags { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the standard <c>!!binary</c> tag is accepted,
    /// in which case the base64 content is passed through as a string. The default is
    /// <see langword="false"/>.
    /// </summary>
    public bool AllowBinaryTag { get; set; }

    /// <summary>
    /// Gets or sets a value indicating whether the standard <c>!!timestamp</c> tag is accepted,
    /// in which case the timestamp is passed through as an ISO-8601 string. The default is
    /// <see langword="false"/>.
    /// </summary>
    public bool AllowTimestampTag { get; set; }

    /// <summary>
    /// Gets or sets the maximum nesting depth accepted when deserializing. The default of 64
    /// matches the default limit of <see cref="System.Text.Json.JsonSerializer"/>. Must be
    /// between 1 and 1000.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not between 1 and 1000.</exception>
    public int MaxDepth
    {
        get => _maxDepth;
        set
        {
            if (value is < 1 or > 1000)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxDepth must be between 1 and 1000.");
            }

            _maxDepth = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum input length in characters accepted when deserializing a YAML
    /// string. Zero (the default) means unlimited. Must be zero or positive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int MaxInputLength
    {
        get => _maxInputLength;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(value));
            _maxInputLength = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum length in characters of a single scalar. Zero (the default)
    /// means unlimited. Must be zero or positive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is negative.</exception>
    public int MaxScalarLength
    {
        get => _maxScalarLength;
        set
        {
            ArgumentOutOfRangeException.ThrowIfNegative(value, nameof(value));
            _maxScalarLength = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of events materialized through alias expansion per
    /// document. This bounds the "billion laughs" expansion attack. Must be positive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive.</exception>
    public int MaxAliasExpansionEvents
    {
        get => _maxAliasExpansionEvents;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxAliasExpansionEvents must be positive.");
            }

            _maxAliasExpansionEvents = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum number of documents accepted in a YAML stream when using the
    /// multi document APIs. Must be positive.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not positive.</exception>
    public int MaxDocuments
    {
        get => _maxDocuments;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxDocuments must be positive.");
            }

            _maxDocuments = value;
        }
    }

    /// <summary>
    /// Creates a copy of the options with the same values.
    /// </summary>
    internal YamlSerializerOptions Clone()
    {
        return new YamlSerializerOptions
        {
            Indentation = Indentation,
            EmitDocumentStart = EmitDocumentStart,
            UseFlowStyle = UseFlowStyle,
            ScalarSchema = ScalarSchema,
            AllowDuplicateKeys = AllowDuplicateKeys,
            AllowMergeKeys = AllowMergeKeys,
            SpecialFloats = SpecialFloats,
            UnknownTags = UnknownTags,
            AllowBinaryTag = AllowBinaryTag,
            AllowTimestampTag = AllowTimestampTag,
            MaxDepth = MaxDepth,
            MaxInputLength = MaxInputLength,
            MaxScalarLength = MaxScalarLength,
            MaxAliasExpansionEvents = MaxAliasExpansionEvents,
            MaxDocuments = MaxDocuments,
        };
    }
}

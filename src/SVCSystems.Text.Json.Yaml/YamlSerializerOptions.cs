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
    internal const int DefaultIndentSize = 2;

    /// <summary>The default maximum nesting depth.</summary>
    internal const int DefaultMaxDepth = 64;

    /// <summary>The default alias expansion budget, in events.</summary>
    internal const int DefaultMaxAliasExpansionEvents = 1_000_000;

    /// <summary>The default preferred line width of the emitter.</summary>
    internal const int DefaultWidth = 80;

    /// <summary>The default maximum length of a simple (single line) mapping key.</summary>
    internal const int DefaultMaxSimpleKeyLength = 128;

    private int _indentation = DefaultIndentSize;
    private YamlScalarSchema _scalarSchema = YamlScalarSchema.Core;
    private int _maxDepth = DefaultMaxDepth;
    private int _maxScalarLength;
    private int _maxAliasExpansionEvents = DefaultMaxAliasExpansionEvents;
    private int _width = DefaultWidth;
    private int _maxSimpleKeyLength = DefaultMaxSimpleKeyLength;
    private string _newLine = Environment.NewLine;

    /// <summary>
    /// Gets or sets the number of spaces used to indent nested block collections, mirroring
    /// <see cref="System.Text.Json.JsonSerializerOptions.IndentSize"/>. Valid values are 1 to 9,
    /// a limit imposed by this library. YAML 1.2.2's single-digit indentation indicator grammar
    /// does not cap block collection indentation. There is intentionally no <c>IndentCharacter</c>
    /// option: YAML forbids tab characters for indentation, so indentation is always spaces.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not between 1 and 9.</exception>
    public int IndentSize
    {
        get => _indentation;
        set
        {
            if (value is < 1 or > 9)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "YAML indent size must be between 1 and 9.");
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
    /// Gets or sets the maximum accepted length in characters of a single scalar after the YAML
    /// parser has scanned it. Zero (the default) means unlimited. This is not an input-size or
    /// scanner-memory limit; bound untrusted input at the hosting layer.
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
    /// Gets or sets the preferred line width in characters before the emitter folds long
    /// scalars. Must be at least 1. The default is 80.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than 1.</exception>
    public int Width
    {
        get => _width;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "Width must be at least 1.");
            }

            _width = value;
        }
    }

    /// <summary>
    /// Gets or sets the maximum length of a mapping key that may be written as a simple (single
    /// line) key; longer keys are written as explicit <c>? key</c> entries. Must be at least 1.
    /// The default is 128.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is less than 1.</exception>
    public int MaxSimpleKeyLength
    {
        get => _maxSimpleKeyLength;
        set
        {
            if (value < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "MaxSimpleKeyLength must be at least 1.");
            }

            _maxSimpleKeyLength = value;
        }
    }

    /// <summary>
    /// Gets or sets the line break sequence used by the emitter. The default is
    /// <see cref="Environment.NewLine"/>, matching <see cref="System.Text.Json.JsonWriterOptions.NewLine"/>.
    /// Only <c>"\n"</c> and <c>"\r\n"</c> are valid, exactly like System.Text.Json; both are
    /// valid YAML line breaks.
    /// </summary>
    /// <exception cref="ArgumentNullException">Thrown when the value is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not <c>"\n"</c> or <c>"\r\n"</c>.</exception>
    public string NewLine
    {
        get => _newLine;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (value is not ("\n" or "\r\n"))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The newline sequence must be \"\\n\" or \"\\r\\n\".");
            }

            _newLine = value;
        }
    }

    /// <summary>
    /// Gets or sets a value indicating whether block sequence entries are indented relative to
    /// their parent mapping key. The default is <see langword="false"/> (YamlDotNet's "indentless"
    /// sequences, which are valid YAML but visually aligned with the key).
    /// </summary>
    public bool IndentedSequences { get; set; }

    private YamlStringStyle _stringStyle;

    /// <summary>
    /// Gets or sets the preferred style for emitted string scalars. The default is
    /// <see cref="YamlStringStyle.Auto"/>.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is not a defined <see cref="YamlStringStyle"/>.</exception>
    public YamlStringStyle StringStyle
    {
        get => _stringStyle;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentOutOfRangeException(nameof(value), value, "The string style is not valid.");
            }

            _stringStyle = value;
        }
    }

    /// <summary>
    /// Creates a copy of the options with the same values.
    /// </summary>
    internal YamlSerializerOptions Clone()
    {
        return new YamlSerializerOptions
        {
            IndentSize = IndentSize,
            EmitDocumentStart = EmitDocumentStart,
            UseFlowStyle = UseFlowStyle,
            ScalarSchema = ScalarSchema,
            MaxDepth = MaxDepth,
            MaxScalarLength = MaxScalarLength,
            MaxAliasExpansionEvents = MaxAliasExpansionEvents,
            Width = Width,
            MaxSimpleKeyLength = MaxSimpleKeyLength,
            NewLine = NewLine,
            IndentedSequences = IndentedSequences,
            StringStyle = StringStyle,
        };
    }
}

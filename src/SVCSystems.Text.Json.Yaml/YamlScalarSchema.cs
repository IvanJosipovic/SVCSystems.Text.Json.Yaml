namespace SVCSystems.Text.Json.Yaml;

/// <summary>
/// Defines how unquoted YAML scalars are resolved into typed JSON values.
/// </summary>
public enum YamlScalarSchema
{
    /// <summary>
    /// The YAML 1.2 core schema (section 10.3 of the specification). This is the default.
    /// <para>
    /// Unquoted scalars resolve to <c>null</c> (empty, <c>null</c>/<c>Null</c>/<c>NULL</c>/<c>~</c>),
    /// booleans (<c>true</c>/<c>false</c> in any capitalization), integers (decimal,
    /// hexadecimal <c>0x1A</c>, octal <c>0o17</c>) and floats (including forms such as
    /// <c>.5</c> and <c>1.</c>). Timestamps and other non-number forms remain strings. Quoted
    /// scalars are always strings. All numeric parsing is culture invariant.
    /// </para>
    /// </summary>
    Core = 0,

    /// <summary>
    /// A JSON restricted resolution: the core schema limited to JSON number forms.
    /// <para>
    /// Hexadecimal (<c>0x1A</c>) and octal (<c>0o17</c>) integers remain strings because they
    /// are not valid JSON numbers. Everything else resolves like <see cref="Core"/>.
    /// </para>
    /// </summary>
    Json = 1,
}

/// <summary>
/// Defines how YAML special floats (<c>.inf</c>, <c>-.inf</c>, <c>.nan</c>) are handled during
/// deserialization. The JSON data model has no representation for these values.
/// </summary>
public enum SpecialFloatBehavior
{
    /// <summary>
    /// Reject special floats with a <see cref="YamlDotNet.Core.YamlException"/> (the default).
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Resolve special floats to their source text as strings (<c>".inf"</c>, <c>"-.inf"</c>,
    /// <c>".nan"</c>). This is lossless in the sense that the text is preserved, but the value
    /// becomes a string in the JSON data model.
    /// </summary>
    String = 1,
}

/// <summary>
/// Defines how unknown (non standard) YAML tags are handled during deserialization.
/// </summary>
public enum UnknownTagBehavior
{
    /// <summary>
    /// Reject scalars and collections carrying tags other than the standard YAML tags with a
    /// <see cref="YamlDotNet.Core.YamlException"/> (the default).
    /// </summary>
    Reject = 0,

    /// <summary>
    /// Ignore unrecognized tags and resolve the node as if it were untagged. Tags never cause
    /// .NET type construction regardless of this setting.
    /// </summary>
    Ignore = 1,
}

/// <summary>
/// Defines the preferred style for emitted string scalars.
/// </summary>
public enum YamlStringStyle
{
    /// <summary>
    /// The emitter decides: multi line strings become literal blocks, strings that a YAML parser
    /// could misread as numbers, booleans or null are quoted, and everything else is plain
    /// (the default).
    /// </summary>
    Auto = 0,

    /// <summary>
    /// String values are emitted as literal block scalars (<c>|</c>), which preserves their exact
    /// content including line breaks. Mapping keys remain single line and use the
    /// <see cref="Auto"/> rules.
    /// </summary>
    Literal = 1,

    /// <summary>
    /// All strings (values and keys) are emitted double quoted, producing a JSON-like appearance.
    /// </summary>
    DoubleQuoted = 2,
}

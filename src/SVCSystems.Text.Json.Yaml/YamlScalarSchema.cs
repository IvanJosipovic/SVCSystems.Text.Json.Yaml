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
    /// booleans (<c>true</c>/<c>True</c>/<c>TRUE</c> and corresponding false forms), integers (decimal,
    /// hexadecimal <c>0x1A</c>, octal <c>0o17</c>) and floats (including forms such as
    /// <c>.5</c> and <c>1.</c>). Timestamps and other non-number forms remain strings. Quoted
    /// scalars are strings unless an explicit standard tag specifies another type. Numeric parsing
    /// is culture invariant.
    /// </para>
    /// </summary>
    Core = 0,

    /// <summary>
    /// The YAML 1.2.2 JSON schema. Plain scalars resolve only from the lowercase JSON literals
    /// (<c>null</c>, <c>true</c>, <c>false</c>) and its integer/floating-point patterns; a plain
    /// scalar that matches none of these patterns is an error, not an implicit string.
    /// <para>
    /// For example, <c>True</c>, <c>~</c>, <c>+42</c>, <c>007</c>, <c>0x1A</c>, and <c>.5</c>
    /// are errors unless quoted or tagged <c>!!str</c>; <c>5.</c> resolves as a float. Quoted
    /// scalars are strings unless an explicit standard tag specifies another type.
    /// </para>
    /// </summary>
    Json = 1,
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

namespace SVCSystems.Text.Json.Yaml;

/// <summary>
/// Defines how unquoted YAML scalars are resolved into typed JSON values.
/// </summary>
public enum YamlScalarSchema
{
    /// <summary>
    /// The JSON compatible scalar schema, following the YAML 1.2 core schema.
    /// <para>
    /// Unquoted scalars resolve to <c>null</c> (empty, <c>null</c>/<c>Null</c>/<c>NULL</c>/<c>~</c>),
    /// booleans (<c>true</c>/<c>false</c> in any capitalization), integers, and decimal floats.
    /// Values such as <c>yes</c>, <c>no</c>, <c>on</c>, <c>off</c>, timestamps and hexadecimal
    /// numbers remain strings. Quoted scalars are always strings. The special floats <c>.nan</c>,
    /// <c>.inf</c> and <c>-.inf</c> have no JSON representation and are rejected. All numeric
    /// parsing is culture invariant.
    /// </para>
    /// </summary>
    Json = 0,
}

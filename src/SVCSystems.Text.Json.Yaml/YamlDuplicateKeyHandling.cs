namespace SVCSystems.Text.Json.Yaml;

/// <summary>Controls how duplicate YAML mapping keys are handled when projecting to JSON.</summary>
public enum YamlDuplicateKeyHandling
{
    /// <summary>Reject duplicate keys and keys that collide after JSON name projection.</summary>
    Reject,

    /// <summary>Keep the last value for a duplicate projected JSON property name.</summary>
    LastWins,
}

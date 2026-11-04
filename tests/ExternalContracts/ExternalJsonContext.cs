using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExternalContracts;

/// <summary>
/// A source generated System.Text.Json context owned by a separate assembly, simulating a third
/// party contract. It must work with <c>YamlSerializer</c> without modification or regeneration.
/// </summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(ExternalModel))]
[JsonSerializable(typeof(List<ExternalModel>))]
[JsonSerializable(typeof(ExternalVersionedModel))]
public partial class ExternalJsonContext : JsonSerializerContext
{
}

public sealed class ExternalModel
{
    [JsonPropertyName("display-name")]
    public string Name { get; set; } = string.Empty;

    public int Replicas { get; set; }

    public string? Description { get; set; }
}

/// <summary>
/// A model serialized through a custom <see cref="JsonConverter"/> that stores a
/// <see cref="Version"/> as its compact string representation.
/// </summary>
public sealed class ExternalVersionedModel
{
    public string Name { get; set; } = string.Empty;

    [JsonConverter(typeof(ExternalVersionConverter))]
    public Version Version { get; set; } = new(1, 0, 0);
}

/// <summary>
/// A custom JSON converter compiled into the external contract assembly.
/// </summary>
public sealed class ExternalVersionConverter : JsonConverter<Version>
{
    public override Version Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        return new Version(reader.GetString()!);
    }

    public override void Write(Utf8JsonWriter writer, Version value, JsonSerializerOptions options)
    {
        writer.WriteStringValue(value.ToString(fieldCount: 3));
    }
}

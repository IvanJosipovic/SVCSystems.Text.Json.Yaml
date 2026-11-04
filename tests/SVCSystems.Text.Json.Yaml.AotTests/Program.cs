using System.Text.Json;
using System.Text.Json.Serialization;
using ExternalContracts;
using SVCSystems.Text.Json.Yaml;

namespace SVCSystems.Text.Json.Yaml.AotTests;

/// <summary>
/// Native AOT validation app. Published with <c>dotnet publish -c Release -r linux-x64</c>:
/// <c>JsonSerializerIsReflectionEnabledByDefault</c> is disabled, so any reflection based JSON
/// serialization would fail at run time. All object mapping goes through source generated
/// contracts, including an external context with a custom converter.
/// </summary>
public static class Program
{
    public static int Main()
    {
        var failures = 0;

        void Check(bool condition, string name)
        {
            if (condition)
            {
                Console.WriteLine($"PASS {name}");
            }
            else
            {
                Console.WriteLine($"FAIL {name}");
                failures++;
            }
        }

        // Reflection mode is disabled in this app; every path must use generated metadata.
        var appYaml = YamlSerializer.Serialize(new AotModel { Name = "api", Replicas = 3, Level = LogLevel.High }, AppJsonContext.Default.AotModel);
        Check(appYaml.ReplaceLineEndings() == "name: api" + Environment.NewLine + "replicas: 3" + Environment.NewLine + "level: High" + Environment.NewLine, "serialize with local context");

        var appModel = YamlSerializer.Deserialize(appYaml, AppJsonContext.Default.AotModel);
        Check(appModel is { Name: "api", Replicas: 3, Level: LogLevel.High }, "deserialize with local context");

        // External source generated context with a custom converter.
        var externalYaml = YamlSerializer.Serialize(
            new ExternalVersionedModel { Name = "svc", Version = new Version(1, 2, 3) },
            ExternalJsonContext.Default.ExternalVersionedModel);
        Check(externalYaml.ReplaceLineEndings() == "name: svc" + Environment.NewLine + "version: 1.2.3" + Environment.NewLine, "serialize with external context and custom converter");

        var externalModel = YamlSerializer.Deserialize(externalYaml, ExternalJsonContext.Default.ExternalVersionedModel);
        Check(externalModel is { Name: "svc", Version: { Major: 1, Minor: 2, Build: 3 } }, "deserialize with external context and custom converter");

        // YAML formatting options.
        var formatted = YamlSerializer.Serialize(
            new Dictionary<string, int> { ["count"] = 42 },
            AppJsonContext.Default.DictionaryStringInt32,
            new YamlSerializerOptions { Indentation = 4, EmitDocumentStart = true });
        Check(formatted.ReplaceLineEndings() == "---" + Environment.NewLine + "count: 42" + Environment.NewLine, "yaml formatting options");

        // Scalar preservation.
        var scalars = YamlSerializer.Serialize(
            JsonSerializer.Deserialize("""{"version":"1.20","enabled":"true","count":3}""", AppJsonContext.Default.JsonElement),
            AppJsonContext.Default.JsonElement);
        Check(scalars.ReplaceLineEndings() == "version: '1.20'" + Environment.NewLine + "enabled: 'true'" + Environment.NewLine + "count: 3" + Environment.NewLine, "scalar preservation");

        var roundTripped = YamlSerializer.Deserialize(scalars, AppJsonContext.Default.JsonElement);
        Check(
            roundTripped.GetProperty("version").ValueKind == JsonValueKind.String
            && roundTripped.GetProperty("enabled").ValueKind == JsonValueKind.String
            && roundTripped.GetProperty("count").ValueKind == JsonValueKind.Number,
            "scalar round trip");

        return failures == 0 ? 0 : 1;
    }
}

public enum LogLevel
{
    Low,
    High,
}

public sealed class AotModel
{
    public string Name { get; set; } = "";

    public int Replicas { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter<LogLevel>))]
    public LogLevel Level { get; set; }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(AotModel))]
[JsonSerializable(typeof(Dictionary<string, int>))]
[JsonSerializable(typeof(JsonElement))]
internal sealed partial class AppJsonContext : JsonSerializerContext;

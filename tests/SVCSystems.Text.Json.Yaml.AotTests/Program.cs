using System.Buffers;
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
            new YamlSerializerOptions { IndentSize = 4, EmitDocumentStart = true });
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

        var jsonSchemaOptions = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json };
        var jsonSchemaYaml = YamlSerializer.Serialize(
            JsonSerializer.Deserialize("""{"value":"text","enabled":true}""", AppJsonContext.Default.JsonElement),
            AppJsonContext.Default.JsonElement,
            jsonSchemaOptions);
        var jsonSchemaRoundTripped = YamlSerializer.Deserialize(
            jsonSchemaYaml,
            AppJsonContext.Default.JsonElement,
            jsonSchemaOptions);
        Check(
            jsonSchemaRoundTripped.GetProperty("value").GetString() == "text"
            && jsonSchemaRoundTripped.GetProperty("enabled").GetBoolean(),
            "YAML JSON schema round trip with generated metadata");

        // Phase 7 completion: IBufferWriter sinks and UTF-8 byte deserialization, all through
        // generated metadata with reflection disabled.
        var sink = new ArrayBufferWriter<byte>();
        YamlSerializer.Serialize(sink, new AotModel { Name = "api", Replicas = 3, Level = LogLevel.High }, AppJsonContext.Default.AotModel);
        Check(sink.WrittenSpan.SequenceEqual("name: api\nreplicas: 3\nlevel: High\n"u8), "serialize to IBufferWriter sink");

        var sinkModel = YamlSerializer.Deserialize(sink.WrittenMemory, AppJsonContext.Default.AotModel);
        Check(sinkModel is { Name: "api", Replicas: 3, Level: LogLevel.High }, "deserialize from ReadOnlyMemory bytes");

        ReadOnlySequence<byte> segmented = SplitSegments(sink.WrittenSpan.ToArray(), 7);
        var segmentedModel = YamlSerializer.Deserialize(segmented, AppJsonContext.Default.AotModel);
        Check(segmentedModel is { Name: "api", Replicas: 3, Level: LogLevel.High }, "deserialize from segmented ReadOnlySequence bytes");

        var sinkBytes = new ArrayBufferWriter<byte>();
        YamlSerializer.Serialize(sinkBytes, new AotModel { Name = "ä", Replicas = 5, Level = LogLevel.Low }, AppJsonContext.Default.AotModel);
        var unicodeModel = YamlSerializer.Deserialize(sinkBytes.WrittenMemory, AppJsonContext.Default.AotModel);
        Check(unicodeModel is { Name: "ä", Replicas: 5, Level: LogLevel.Low }, "non-ASCII round trip through byte APIs");

        return failures == 0 ? 0 : 1;
    }

    /// <summary>
    /// Chains the input bytes into a segmented <see cref="ReadOnlySequence{T}"/> so the byte
    /// deserialization path exercises decoder state across segment boundaries.
    /// </summary>
    private static ReadOnlySequence<byte> SplitSegments(byte[] bytes, int segmentSize)
    {
        var first = new Segment(bytes.AsMemory(0, Math.Min(segmentSize, bytes.Length)), 0);
        var last = first;

        for (var offset = segmentSize; offset < bytes.Length; offset += segmentSize)
        {
            var length = Math.Min(segmentSize, bytes.Length - offset);
            last = last.Append(bytes.AsMemory(offset, length));
        }

        return new ReadOnlySequence<byte>(first, 0, last, last.Memory.Length);
    }

    private sealed class Segment : ReadOnlySequenceSegment<byte>
    {
        public Segment(ReadOnlyMemory<byte> memory, long runningIndex)
        {
            Memory = memory;
            RunningIndex = runningIndex;
        }

        public Segment Append(ReadOnlyMemory<byte> next)
        {
            var segment = new Segment(next, RunningIndex + Memory.Length);
            Next = segment;
            return segment;
        }
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

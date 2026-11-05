using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SVCSystems.Text.Json.Yaml.Internal;
using YamlDotNet.Core;

namespace SVCSystems.Text.Json.Yaml.Tests;

/// <summary>
/// Unit tests for the internal JSON to YAML token converter (plan phase 2).
/// </summary>
public partial class JsonToYamlConverterTests
{
    private static byte[] SerializeToJsonBytes(string json)
    {
        var element = JsonSerializer.Deserialize(json, JsonElementContext.Default.JsonElement);
        return JsonSerializer.SerializeToUtf8Bytes(element, JsonElementContext.Default.JsonElement);
    }

    [Theory]
    [InlineData("""{"a":1}""", "a: 1")]
    [InlineData("""{"a":null}""", "a: null")]
    [InlineData("""{"a":true,"b":false}""", "a: true" + "\n" + "b: false")]
    [InlineData("""{"a":[1,2]}""", "a:\n- 1\n- 2")]
    [InlineData("""{"a":{}}""", "a: {}")]
    [InlineData("""{"a":[]}""", "a: []")]
    [InlineData("""{"a":"1.20"}""", "a: '1.20'")]
    [InlineData("""{"a":"true"}""", "a: 'true'")]
    [InlineData("""{"a":"null"}""", "a: 'null'")]
    [InlineData("""{"a":""}""", "a: ''")]
    [InlineData("""{"a":"line\nbreak"}""", "a: |-\n  line\n  break")]
    [InlineData("""[{"a":1},"x"]""", "- a: 1\n- x")]
    [InlineData("""{"é":"🎉"}""", "é: \"\\U0001F389\"")]
    [InlineData("""{"a":1.25,"b":-0.5,"c":1e30}""", "a: 1.25\nb: -0.5\nc: 1e30")]
    public void ConvertsJsonTokensToYaml(string json, string expected)
    {
        var yaml = JsonToYamlConverter.ConvertToString(SerializeToJsonBytes(json));

        yaml.ReplaceLineEndings("\n").ShouldBe(expected + "\n");
    }

    [Fact]
    public void PreservesNumericFormatting()
    {
        var yaml = JsonToYamlConverter.ConvertToString("""{"int":42,"float":1.50,"exp":1e30}"""u8);

        yaml.ReplaceLineEndings("\n").ShouldBe("int: 42\nfloat: 1.50\nexp: 1e30\n");
    }

    [Fact]
    public void IndentationIsAppliedToNestedStructures()
    {
        var yaml = JsonToYamlConverter.ConvertToString("""{"a":{"b":[1,2]}}"""u8, new YamlSerializerOptions { IndentSize = 4 });

        yaml.ReplaceLineEndings("\n").ShouldBe("a:\n    b:\n    - 1\n    - 2\n");
    }

    [Fact]
    public void EmitDocumentStartWritesTheMarker()
    {
        var yaml = JsonToYamlConverter.ConvertToString("""{"a":1}"""u8, new YamlSerializerOptions { EmitDocumentStart = true });

        yaml.ReplaceLineEndings("\n").ShouldBe("---\na: 1\n");
    }

    [Fact]
    public void UseFlowStyleWritesFlowCollections()
    {
        var yaml = JsonToYamlConverter.ConvertToString("""{"a":[1,{"b":2}]}"""u8, new YamlSerializerOptions { UseFlowStyle = true });

        yaml.ReplaceLineEndings("\n").ShouldBe("{a: [1, {b: 2}]}\n");
    }

    [Fact]
    public void EmitsYamlThatRoundTripsValueTypes()
    {
        var json = SerializeToJsonBytes("""
            {
              "version": "1.20",
              "enabled": "true",
              "count": 3,
              "ratio": 1.25,
              "empty": null,
              "text": "null",
              "list": ["1", 2],
              "nested": {"a": {"b": ["x"]}}
            }
            """);

        var yaml = JsonToYamlConverter.ConvertToString(json);

        yaml.ShouldContain("version: '1.20'");
        yaml.ShouldContain("enabled: 'true'");
        yaml.ShouldContain("count: 3");
        yaml.ShouldContain("ratio: 1.25");
        yaml.ShouldContain("empty: null");
        yaml.ShouldContain("text: 'null'");
    }

    [Fact]
    public void QuotedValuesStayQuotedInYamlOutput()
    {
        var yaml = JsonToYamlConverter.ConvertToString("""{"version":"1.20","enabled":"true","count":"3"}"""u8);

        yaml.ShouldContain("version: '1.20'");
        yaml.ShouldContain("enabled: 'true'");
        yaml.ShouldContain("count: '3'");
    }

    [Fact]
    public void InvalidJsonProducesUsefulErrors()
    {
        Should.Throw<JsonException>(() => JsonToYamlConverter.ConvertToString("""{"a":}"""u8));
    }

    [Fact]
    public void RejectsDuplicateJsonObjectProperties()
    {
        Should.Throw<JsonException>(() => JsonToYamlConverter.ConvertToString("""{"a":1,"a":2}"""u8))
            .Message.ShouldContain("duplicate property name 'a'");
    }

    [Fact]
    public void RejectsNestedDuplicateJsonObjectProperties()
    {
        Should.Throw<JsonException>(() => JsonToYamlConverter.ConvertToString("""{"outer":{"a":1,"a":2}}"""u8))
            .Message.ShouldContain("duplicate property name 'a'");
    }

    [JsonSerializable(typeof(JsonElement))]
    internal sealed partial class JsonElementContext : JsonSerializerContext;
}

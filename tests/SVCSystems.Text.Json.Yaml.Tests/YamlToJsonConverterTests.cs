using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SVCSystems.Text.Json.Yaml;
using SVCSystems.Text.Json.Yaml.Internal;
using YamlDotNet.Core;

namespace SVCSystems.Text.Json.Yaml.Tests;

/// <summary>
/// Unit tests for the internal YAML to JSON token converter (plan phase 3): edge case scalar
/// resolution, tags, duplicate keys and unsupported constructs.
/// </summary>
public partial class YamlToJsonConverterTests
{
    [Theory]
    [InlineData("value: true", "true")]
    [InlineData("value: True", "true")]
    [InlineData("value: false", "false")]
    [InlineData("value: 42", "42")]
    [InlineData("value: -42", "-42")]
    [InlineData("value: +42", "42")]
    [InlineData("value: 007", "7")]
    [InlineData("value: 1.25", "1.25")]
    [InlineData("value: 1.20", "1.20")]
    [InlineData("value: 1e5", "100000")]
    [InlineData("value: \"1.20\"", "\"1.20\"")]
    [InlineData("value: '1.20'", "\"1.20\"")]
    [InlineData("value: null", "null")]
    [InlineData("value: ~", "null")]
    [InlineData("value:", "null")]
    [InlineData("value: \"null\"", "\"null\"")]
    [InlineData("value: y", "\"y\"")]
    [InlineData("value: 0x1A", "\"0x1A\"")]
    [InlineData("value: 2026-01-01", "\"2026-01-01\"")]
    [InlineData("value: !!str 123", "\"123\"")]
    [InlineData("value: !!int 3", "3")]
    [InlineData("value: !!bool true", "true")]
    [InlineData("value: 99999999999999999999", "99999999999999999999")]
    public void ResolvesScalarsPerTheJsonSchema(string yaml, string expectedJson)
    {
        var json = ConvertToJson(yaml);

        json.ShouldBe($"{{\"value\":{expectedJson}}}");
    }

    [Theory]
    [InlineData("value: 1,000", "\"1,000\"")]
    [InlineData("value: 1..2", "\"1..2\"")]
    [InlineData("value: 2026-01-01T00:00:00Z", "\"2026-01-01T00:00:00Z\"")]
    public void ResolvesNonNumericScalarsAsStrings(string yaml, string expectedJson)
    {
        ConvertToJson(yaml).ShouldBe($"{{\"value\":{expectedJson}}}");
    }

    [Theory]
    [InlineData("value: .inf")]
    [InlineData("value: -.inf")]
    [InlineData("value: .nan")]
    public void RejectsScalarsWithoutJsonRepresentation(string yaml)
    {
        Should.Throw<YamlException>(() => ConvertToJson(yaml));
    }

    [Theory]
    [InlineData("value: !!binary aGk=")]
    [InlineData("value: !Custom 123")]
    [InlineData("value: !!timestamp 2026-01-01T00:00:00Z")]
    [InlineData("value: !!int \"1.5\"")]
    [InlineData("value: !!bool \"yes\"")]
    public void RejectsUnsupportedTags(string yaml)
    {
        Should.Throw<YamlException>(() => ConvertToJson(yaml));
    }

    [Fact]
    public void RejectsDuplicateKeysByDefault()
    {
        var exception = Should.Throw<YamlException>(() => ConvertToJson("""
            a: 1
            b: 2
            a: 3
            """));

        exception.Message.ShouldContain("duplicate mapping key 'a'");
    }

    [Fact]
    public void AllowsDuplicateKeysWhenConfigured()
    {
        var json = ConvertToJson("""
            a: 1
            b: 2
            a: 3
            """, new YamlSerializerOptions { AllowDuplicateKeys = true });

        json.ShouldContain("\"a\":1");
        json.ShouldContain("\"b\":2");
        json.ShouldContain("\"a\":3");
    }

    [Fact]
    public void TracksDuplicateKeysPerMappingLevel()
    {
        // The same key in sibling mappings is not a duplicate.
        var json = ConvertToJson("""
            items:
            - name: a
              value: 1
            - name: b
              value: 2
            """);

        json.ShouldContain("\"name\":\"a\"");
        json.ShouldContain("\"name\":\"b\"");
    }

    [Fact]
    public void ScalarMappingKeysAreWrittenAsJsonStringKeys()
    {
        // Plain scalar keys keep their source text as the JSON property name, so typed
        // dictionary key converters in System.Text.Json can resolve them.
        ConvertToJson("1: a").ShouldBe("{\"1\":\"a\"}");
        ConvertToJson("true: a").ShouldBe("{\"true\":\"a\"}");

        // Complex keys (sequences or mappings used as keys) are rejected.
        Should.Throw<YamlException>(() => ConvertToJson("? [1, 2]\n: value")).Message.ShouldContain("Complex mapping keys");
    }

    [Fact]
    public void RejectsMultipleDocuments()
    {
        Should.Throw<YamlException>(() => ConvertToJson("""
            a: 1
            ---
            a: 2
            """)).Message.ShouldContain("Multiple YAML documents");
    }

    [Fact]
    public void RejectsAnchorsAndAliases()
    {
        Should.Throw<YamlException>(() => ConvertToJson("""
            base: &anchor
              x: 1
            copy: *anchor
            """)).Message.ShouldContain("anchors and aliases");
    }

    [Fact]
    public void SupportsBlockScalars()
    {
        var json = ConvertToJson("""
            text: |
              line one
              line two
            """);

        json.ShouldContain("\"text\":\"line one\\nline two\"");
    }

    [Fact]
    public void RejectsEmptyInput()
    {
        Should.Throw<YamlException>(() => ConvertToJson(""));
        Should.Throw<YamlException>(() => ConvertToJson("   \n"));
    }

    [Fact]
    public void HandlesEmptyDocumentsAsNull()
    {
        ConvertToJson("---").ShouldBe("null");
    }

    [Fact]
    public void HandlesDeepNestingUpToTheDefaultLimit()
    {
        const int depth = 60;
        var yaml = string.Concat(Enumerable.Repeat("- ", depth)) + "1";
        var element = YamlSerializer.Deserialize(yaml, TestJsonContext.Default.JsonElement);

        var current = element;
        for (var i = 0; i < depth; i++)
        {
            current = current[0];
        }

        current.GetInt32().ShouldBe(1);
    }

    [Fact]
    public void RejectsNestingBeyondTheDefaultLimit()
    {
        const int depth = 65;
        var yaml = string.Concat(Enumerable.Repeat("- ", depth)) + "1";

        Should.Throw<JsonException>(() => YamlSerializer.Deserialize(yaml, TestJsonContext.Default.JsonElement));
    }

    private static string ConvertToJson(string yaml, YamlSerializerOptions? options = null)
    {
        var parser = new Parser(new StringReader(yaml));
        var buffer = new System.Buffers.ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer))
        {
            YamlToJsonConverter.Convert(parser, writer, options);
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    [JsonSerializable(typeof(JsonElement))]
    internal sealed partial class TestJsonContext : JsonSerializerContext;
}

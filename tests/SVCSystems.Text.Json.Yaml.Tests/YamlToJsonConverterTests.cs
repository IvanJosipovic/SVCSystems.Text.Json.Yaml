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
    public void ExpandsAnchorsAndAliases()
    {
        var json = ConvertToJson("""
            base: &anchor
              x: 1
            copy: *anchor
            """);

        json.ShouldContain("\"base\":{\"x\":1}");
        json.ShouldContain("\"copy\":{\"x\":1}");
    }

    [Fact]
    public void ExpandsNestedAnchors()
    {
        var json = ConvertToJson("""
            outer: &outer
              inner: &inner
                x: 1
              alias: *inner
            replay: *outer
            """);

        json.ShouldContain("\"outer\":{\"inner\":{\"x\":1},\"alias\":{\"x\":1}}");
        json.ShouldContain("\"replay\":{\"inner\":{\"x\":1},\"alias\":{\"x\":1}}");
    }

    [Fact]
    public void RejectsUndefinedAliases()
    {
        Should.Throw<YamlException>(() => ConvertToJson("copy: *missing"))
            .Message.ShouldContain("missing");
    }

    [Fact]
    public void BoundsAliasExpansion()
    {
        var yaml = """
            a: &a [0, 1]
            b: &b [*a, *a]
            c: &c [*b, *b]
            d: &d [*c, *c]
            e: &e [*d, *d]
            f: &f [*e, *e]
            g: &g [*f, *f]
            h: &h [*g, *g]
            i: &i [*h, *h]
            j: &j [*i, *i]
            k: &k [*j, *j]
            l: &l [*k, *k]
            m: &m [*l, *l]
            n: &n [*m, *m]
            o: &o [*n, *n]
            p: &p [*o, *o]
            q: &q [*p, *p]
            r: &r [*q, *q]
            s: &s [*r, *r]
            t: &t [*s, *s]
            u: &u [*t, *t]
            v: &v [*u, *u]
            w: &w [*v, *v]
            x: &x [*w, *w]
            y: &y [*x, *x]
            z: [*y, *y]
            """;

        var yamlOptions = new YamlSerializerOptions { MaxAliasExpansionEvents = 1000 };

        Should.Throw<YamlException>(() => ConvertToJson(yaml, yamlOptions))
            .Message.ShouldContain("alias expansion limit");

        // A small chained document succeeds with the default budget.
        var small = """
            a: &a [0, 1]
            b: &b [*a, *a]
            c: &c [*b, *b]
            d: [*c, *c]
            """;
        var json = ConvertToJson(small, new YamlSerializerOptions());
        json.ShouldStartWith("{\"a\":[0,1]");
    }

    [Fact]
    public void RejectsNestingBeyondMaxDepth()
    {
        const int depth = 65;
        var yaml = string.Concat(Enumerable.Repeat("- ", depth)) + "1";

        Should.Throw<YamlException>(() => YamlSerializer.Deserialize(yaml, TestJsonContext.Default.JsonElement))
            .Message.ShouldContain("maximum nesting depth");

        // A custom depth below the failing level produces the error at the YAML layer.
        var yamlOptions = new YamlSerializerOptions { MaxDepth = 2 };

        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("- - - 1", TestJsonContext.Default.JsonElement, yamlOptions))
            .Message.ShouldContain("maximum nesting depth of 2");
    }

    [Fact]
    public void SiblingCollectionsDoNotAccumulateNestingDepth()
    {
        // Regression: mappings did not decrement the depth counter, so any document with more
        // than MaxDepth sibling collections (for example a large list of objects) was rejected.
        var builder = new System.Text.StringBuilder();
        for (var i = 0; i < 200; i++)
        {
            builder.Append("- id: ").Append(i).AppendLine();
            builder.AppendLine("  name: item");
        }

        var element = YamlSerializer.Deserialize(builder.ToString(), TestJsonContext.Default.JsonElement);

        element.GetArrayLength().ShouldBe(200);
        element[199].GetProperty("id").GetInt32().ShouldBe(199);
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
    public void CoreSchemaResolvesHexOctalAndLooseFloatForms()
    {
        // Core is the default schema.
        ConvertToJson("value: 0x1A").ShouldBe("{\"value\":26}");
        ConvertToJson("value: -0x1A").ShouldBe("{\"value\":-26}");
        ConvertToJson("value: 0o17").ShouldBe("{\"value\":15}");

        // The JSON restricted schema keeps hexadecimal and octal forms as strings.
        var jsonSchema = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json };

        ConvertToJson("value: 0x1A", jsonSchema).ShouldBe("{\"value\":\"0x1A\"}");
        ConvertToJson("value: 0o17", jsonSchema).ShouldBe("{\"value\":\"0o17\"}");
    }

    [Fact]
    public void LooseFloatFormsResolveUnderBothSchemas()
    {
        ConvertToJson("value: .5").ShouldBe("{\"value\":0.5}");
        ConvertToJson("value: 5.").ShouldBe("{\"value\":5}");
        ConvertToJson("value: -.5").ShouldBe("{\"value\":-0.5}");

        var yamlOptions = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Core };

        ConvertToJson("value: .5", yamlOptions).ShouldBe("{\"value\":0.5}");
        ConvertToJson("value: 5.", yamlOptions).ShouldBe("{\"value\":5}");
        ConvertToJson("value: -.5", yamlOptions).ShouldBe("{\"value\":-0.5}");
    }

    [Fact]
    public void CoreSchemaResolvesVeryLargeHexIntegersExactly()
    {
        var yamlOptions = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Core };

        ConvertToJson("value: 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", yamlOptions)
            .ShouldBe("{\"value\":340282366920938463463374607431768211455}");
    }

    [Fact]
    public void SpecialFloatsCanBeResolvedToStrings()
    {
        var yamlOptions = new YamlSerializerOptions { SpecialFloats = SpecialFloatBehavior.String };

        ConvertToJson("value: .inf", yamlOptions).ShouldBe("{\"value\":\".inf\"}");
        ConvertToJson("value: -.inf", yamlOptions).ShouldBe("{\"value\":\"-.inf\"}");
        ConvertToJson("value: .nan", yamlOptions).ShouldBe("{\"value\":\".nan\"}");

        // Rejection remains the default.
        Should.Throw<YamlException>(() => ConvertToJson("value: .inf"));
    }

    [Fact]
    public void UnknownTagsCanBeIgnored()
    {
        var yamlOptions = new YamlSerializerOptions { UnknownTags = UnknownTagBehavior.Ignore };

        // A custom tag asserts an application specific type with no JSON mapping; the scalar is
        // resolved conservatively to its exact text as a string.
        ConvertToJson("value: !Custom 123", yamlOptions).ShouldBe("{\"value\":\"123\"}");
        ConvertToJson("value: !Custom hello", yamlOptions).ShouldBe("{\"value\":\"hello\"}");

        // Rejection remains the default.
        Should.Throw<YamlException>(() => ConvertToJson("value: !Custom 123"))
            .Message.ShouldContain("not supported");
    }

    [Fact]
    public void BinaryAndTimestampTagsAreOptIn()
    {
        Should.Throw<YamlException>(() => ConvertToJson("value: !!binary aGVsbG8=")).Message.ShouldContain("not supported");
        Should.Throw<YamlException>(() => ConvertToJson("value: !!timestamp 2026-01-01T00:00:00Z")).Message.ShouldContain("not supported");

        var allow = new YamlSerializerOptions { AllowBinaryTag = true, AllowTimestampTag = true };

        ConvertToJson("value: !!binary aGVsbG8=", allow).ShouldBe("{\"value\":\"aGVsbG8=\"}");
        ConvertToJson("value: !!timestamp 2026-01-01T00:00:00Z", allow).ShouldBe("{\"value\":\"2026-01-01T00:00:00Z\"}");
    }

    [Fact]
    public void BoundsScalarLength()
    {
        var yamlOptions = new YamlSerializerOptions { MaxScalarLength = 4 };

        ConvertToJson("k: ab", yamlOptions).ShouldBe("{\"k\":\"ab\"}");
        Should.Throw<YamlException>(() => ConvertToJson("key: longer-value", yamlOptions))
            .Message.ShouldContain("maximum scalar length");
    }

    [Fact]
    public void BoundsInputLength()
    {
        var yamlOptions = new YamlSerializerOptions { MaxInputLength = 10 };

        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("a: a-longer-value", TestJsonContext.Default.JsonElement, yamlOptions))
            .Message.ShouldContain("maximum input length");
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

    private static string ConvertToJson(string yaml, YamlSerializerOptions? options = null)
    {
        var parser = new SVCSystems.Text.Json.Yaml.Internal.AliasExpandingParser(
            new Parser(new StringReader(yaml)),
            options?.MaxAliasExpansionEvents ?? YamlSerializerOptions.DefaultMaxAliasExpansionEvents);
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

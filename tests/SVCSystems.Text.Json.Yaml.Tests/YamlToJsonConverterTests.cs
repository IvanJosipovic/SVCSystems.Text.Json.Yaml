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
    [InlineData("value: 1e5", "1e5")]
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
        // Supported scalar keys are projected to canonical JSON property names.
        ConvertToJson("1: a").ShouldBe("{\"1\":\"a\"}");
        ConvertToJson("true: a").ShouldBe("{\"true\":\"a\"}");
        ConvertToJson("01: a").ShouldBe("{\"1\":\"a\"}");

        // Complex keys (sequences or mappings used as keys) are rejected.
        Should.Throw<YamlException>(() => ConvertToJson("? [1, 2]\n: value")).Message.ShouldContain("Complex mapping keys");
    }

    [Theory]
    [InlineData("1: a\n01: b")]
    [InlineData("true: a\nTrue: b")]
    [InlineData("1.0: a\n1e0: b")]
    public void RejectsDuplicateKeysByYamlTagAndCanonicalValue(string yaml)
    {
        Should.Throw<YamlException>(() => ConvertToJson(yaml)).Message.ShouldContain("duplicate mapping key");
    }

    [Fact]
    public void RejectsDifferentYamlKeysThatCollideAsJsonNames()
    {
        Should.Throw<YamlException>(() => ConvertToJson("true: boolean\n\"true\": string"))
            .Message.ShouldContain("collides with another key");
        Should.Throw<YamlException>(() => ConvertToJson("1: integer\n\"1\": string"))
            .Message.ShouldContain("collides with another key");
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
    public void ReplayedNestedAnchorDoesNotReplaceALaterSourceDeclaration()
    {
        var json = ConvertToJson("""
            root: &root
              nested: &value old
            latest: &value new
            copy: *root
            alias: *value
            """);

        json.ShouldContain("\"latest\":\"new\"");
        json.ShouldContain("\"copy\":{\"nested\":\"old\"}");
        json.ShouldContain("\"alias\":\"new\"");
    }

    [Fact]
    public void RejectsUndefinedAliases()
    {
        Should.Throw<YamlException>(() => ConvertToJson("copy: *missing"))
            .Message.ShouldContain("missing");
    }

    [Fact]
    public void RejectsCyclicAliasesAsUnrepresentableByJson()
    {
        Should.Throw<YamlException>(() => ConvertToJson("self: &node {next: *node}"))
            .Message.ShouldContain("cyclic YAML graph");
    }

    [Fact]
    public void ReusedAnchorNameBindsAliasesToItsMostRecentDefinition()
    {
        var json = ConvertToJson("""
            outer: &same
              first: one
              inner: &same
                value: two
              copy: *same
            after: *same
            """);

        json.ShouldContain("\"copy\":{\"value\":\"two\"}");
        json.ShouldContain("\"after\":{\"value\":\"two\"}");
    }

    [Fact]
    public void ExplicitScalarTagsApplyToQuotedValues()
    {
        ConvertToJson("value: !!int \"3\"").ShouldBe("{\"value\":3}");
        ConvertToJson("value: !!bool 'true'").ShouldBe("{\"value\":true}");
        ConvertToJson("value: !!null \"null\"").ShouldBe("{\"value\":null}");
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
    public void CoreAndJsonSchemasUseTheirYamlResolutionRules()
    {
        // Core is the default schema.
        ConvertToJson("value: 0x1A").ShouldBe("{\"value\":26}");
        ConvertToJson("value: 0o17").ShouldBe("{\"value\":15}");
        ConvertToJson("value: -0x1A").ShouldBe("{\"value\":\"-0x1A\"}");
        JsonDocument.Parse(ConvertToJson("value: +0o17")).RootElement.GetProperty("value").GetString().ShouldBe("+0o17");
        ConvertToJson("value: 0X1A").ShouldBe("{\"value\":\"0X1A\"}");
        ConvertToJson("value: 0O17").ShouldBe("{\"value\":\"0O17\"}");

        // JSON schema leaves no implicit plain-string fallback. Values not matching its scalar
        // patterns are errors; JSON strings must be quoted or explicitly tagged.
        var jsonSchema = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json };

        foreach (var value in new[] { "hello", "0x1A", "0o17", "True", "~", "+42", "007", ".5" })
        {
            Should.Throw<YamlException>(() => ConvertToJson($"'value': {value}", jsonSchema));
        }

        Should.Throw<YamlException>(() => ConvertToJson("plain: 'text'", jsonSchema))
            .Message.ShouldContain("does not match the YAML 1.2 JSON schema");

        ConvertToJson("'value': 'hello'", jsonSchema).ShouldBe("{\"value\":\"hello\"}");
        ConvertToJson("'value': !!str hello", jsonSchema).ShouldBe("{\"value\":\"hello\"}");
        ConvertToJson("'value': true", jsonSchema).ShouldBe("{\"value\":true}");
        ConvertToJson("'value': null", jsonSchema).ShouldBe("{\"value\":null}");
        ConvertToJson("'value': 5.", jsonSchema).ShouldBe("{\"value\":5.0}");
        ConvertToJson("'value': 1e3", jsonSchema).ShouldBe("{\"value\":1e3}");
        ConvertToJson("'value': -0.25e+2", jsonSchema).ShouldBe("{\"value\":-0.25e+2}");
    }

    [Fact]
    public void PlainMergeLookingScalarDependsOnSchemaAndExplicitStringFormDoesNot()
    {
        var jsonSchema = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json };

        ConvertToJson("plain: {<<: 'value'}").ShouldBe("{\"plain\":{\"\\u003C\\u003C\":\"value\"}}");
        Should.Throw<YamlException>(() => ConvertToJson("'plain': {<<: 'value'}", jsonSchema));
        ConvertToJson("'plain': {'<<': 'value'}", jsonSchema)
            .ShouldBe("{\"plain\":{\"\\u003C\\u003C\":\"value\"}}");
        ConvertToJson("'plain': {!!str <<: 'value'}", jsonSchema)
            .ShouldBe("{\"plain\":{\"\\u003C\\u003C\":\"value\"}}");
    }

    [Fact]
    public void LooseFloatFormsResolveOnlyUnderCoreSchema()
    {
        ConvertToJson("value: .5").ShouldBe("{\"value\":0.5}");
        ConvertToJson("value: 5.").ShouldBe("{\"value\":5.0}");
        ConvertToJson("value: -.5").ShouldBe("{\"value\":-0.5}");

        var yamlOptions = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json };

        Should.Throw<YamlException>(() => ConvertToJson("'value': .5", yamlOptions));
        ConvertToJson("'value': 5.", yamlOptions).ShouldBe("{\"value\":5.0}");
        Should.Throw<YamlException>(() => ConvertToJson("'value': -.5", yamlOptions));
    }

    [Fact]
    public void PreservesNegativeZeroWhenProjectingIntegerScalars()
    {
        ConvertToJson("value: -0").ShouldBe("{\"value\":-0}");
        ConvertToJson("value: -00").ShouldBe("{\"value\":-0}");
    }

    [Fact]
    public void CoreSchemaResolvesVeryLargeHexIntegersExactly()
    {
        var yamlOptions = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Core };

        ConvertToJson("value: 0xFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFFF", yamlOptions)
            .ShouldBe("{\"value\":340282366920938463463374607431768211455}");
    }

    [Fact]
    public void CoreSchemaResolvesLongRadixIntegersWithoutLosingPrecision()
    {
        var digits = new string('7', 4096);
        var yamlOptions = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Core };
        var expected = System.Numerics.BigInteger.Pow(8, digits.Length) - 1;

        ConvertToJson($"value: 0o{digits}", yamlOptions)
            .ShouldBe($"{{\"value\":{expected.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}");
    }

    [Fact]
    public void BothSchemasRejectSpecialFloats()
    {
        var jsonSchema = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json };
        foreach (var value in new[] { ".inf", "+.INF", "-.Inf", ".nan", "+.NaN", "-.NAN" })
        {
            Should.Throw<YamlException>(() => ConvertToJson($"value: {value}", jsonSchema));
        }

        foreach (var value in new[] { ".inf", "+.INF", "-.Inf", ".nan", ".NaN", ".NAN" })
        {
            Should.Throw<YamlException>(() => ConvertToJson($"value: {value}"));
        }

        foreach (var value in new[] { "+.NaN", "-.NAN" })
        {
            JsonDocument.Parse(ConvertToJson($"value: {value}")).RootElement.GetProperty("value").GetString().ShouldBe(value);
        }
    }

    [Fact]
    public void ExplicitNonSpecificTagForcesStringResolution()
    {
        ConvertToJson("'value': ! 12", new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json })
            .ShouldBe("{\"value\":\"12\"}");
    }

    [Theory]
    [InlineData("1e999999999999999999999999")]
    [InlineData("1e-999999999999999999999999")]
    [InlineData("0.123456789012345678901234567890123456789")]
    public void PreservesFloatMagnitudeAndPrecisionExactly(string yamlScalar)
    {
        ConvertToJson($"value: {yamlScalar}").ShouldBe($"{{\"value\":{yamlScalar}}}");
    }

    [Fact]
    public void UnsupportedTagsAreAlwaysRejected()
    {
        Should.Throw<YamlException>(() => ConvertToJson("value: !Custom 123"))
            .Message.ShouldContain("not supported");
        Should.Throw<YamlException>(() => ConvertToJson("value: !Custom [1, 2]"))
            .Message.ShouldContain("not supported");
    }

    [Fact]
    public void BinaryAndTimestampTagsAreUnsupported()
    {
        Should.Throw<YamlException>(() => ConvertToJson("value: !!binary aGVsbG8=")).Message.ShouldContain("not supported");
        Should.Throw<YamlException>(() => ConvertToJson("value: !!timestamp 2026-01-01T00:00:00Z")).Message.ShouldContain("not supported");
    }

    [Fact]
    public void ValidatesTagsOnCollectionsAndSupportsYamlTagDirectives()
    {
        Should.Throw<YamlException>(() => ConvertToJson("value: !!str [one, two]"))
            .Message.ShouldContain("not supported on a sequence");
        Should.Throw<YamlException>(() => ConvertToJson("value: !!set {one: null}"))
            .Message.ShouldContain("not supported on a mapping");

        ConvertToJson("%TAG !e! tag:yaml.org,2002:\n---\nvalue: !e!int 42").ShouldBe("{\"value\":42}");
        ConvertToJson("%YAML 1.2\n---\nvalue: true").ShouldBe("{\"value\":true}");
        Should.Throw<YamlException>(() => ConvertToJson("%YAML 1.1\n---\nvalue: true"))
            .Message.ShouldContain("YAML version 1.1 is not supported");
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

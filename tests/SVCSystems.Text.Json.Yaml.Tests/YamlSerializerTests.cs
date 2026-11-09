using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SVCSystems.Text.Json.Yaml;
using YamlDotNet.Core;

namespace SVCSystems.Text.Json.Yaml.Tests;

public partial class YamlSerializerTests
{
    public sealed class Person
    {
        public string Name { get; set; } = "";
        public int Age { get; set; }
    }

    [JsonSerializable(typeof(Person))]
    [JsonSerializable(typeof(List<Person>))]
    [JsonSerializable(typeof(Person[]))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    [JsonSerializable(typeof(Dictionary<string, int>))]
    [JsonSerializable(typeof(List<int>))]
    [JsonSerializable(typeof(List<string>))]
    [JsonSerializable(typeof(int))]
    [JsonSerializable(typeof(double))]
    [JsonSerializable(typeof(bool))]
    [JsonSerializable(typeof(string))]
    [JsonSerializable(typeof(JsonElement))]
    internal sealed partial class AppJsonContext : JsonSerializerContext;

    private static JsonTypeInfo<T> TypeInfo<T>(Func<AppJsonContext, JsonTypeInfo<T>> selector)
    {
        return selector(AppJsonContext.Default);
    }

    [Fact]
    public void SerializeProducesYamlFromSourceGeneratedContext()
    {
        var person = new Person { Name = "John", Age = 30 };

        var yaml = YamlSerializer.Serialize(person, AppJsonContext.Default.Person);

        yaml.ShouldBe("Name: John" + Environment.NewLine + "Age: 30" + Environment.NewLine);
    }

    [Fact]
    public void DeserializeProducesObjectFromSourceGeneratedContext()
    {
        var person = YamlSerializer.Deserialize("""
            Name: Jane
            Age: 42
            """, AppJsonContext.Default.Person);

        person.ShouldNotBeNull();
        person.Name.ShouldBe("Jane");
        person.Age.ShouldBe(42);
    }

    [Fact]
    public void SerializesWithDefaultJsonSerializerOptions()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var person = new Person { Name = "John", Age = 30 };

        var yaml = YamlSerializer.Serialize(person);

        yaml.ShouldBe("Name: John" + Environment.NewLine + "Age: 30" + Environment.NewLine);

        var result = YamlSerializer.Deserialize<Person>("Name: Jane" + Environment.NewLine + "Age: 42" + Environment.NewLine);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("Jane");
        result.Age.ShouldBe(42);
    }

    [Fact]
    public void SerializesWithBothConfigurations()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var yamlOptions = new YamlSerializerOptions { IndentSize = 4, EmitDocumentStart = true };

        var yaml = YamlSerializer.Serialize(new Person { Name = "John", Age = 30 }, jsonOptions, yamlOptions);

        yaml.ReplaceLineEndings().ShouldBe("---" + Environment.NewLine + "name: John" + Environment.NewLine + "age: 30" + Environment.NewLine);
    }

    [Fact]
    public void YamlAndJsonProduceEquivalentResults()
    {
        var person = new Person { Name = "John", Age = 30 };

        var json = JsonSerializer.Serialize(person, AppJsonContext.Default.Person);
        var yaml = YamlSerializer.Serialize(person, AppJsonContext.Default.Person);

        var fromJson = JsonSerializer.Deserialize(json, AppJsonContext.Default.Person);
        var fromYaml = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.Person);

        fromYaml.ShouldNotBeNull();
        fromYaml.Name.ShouldBe(fromJson!.Name);
        fromYaml.Age.ShouldBe(fromJson.Age);
    }

    [Fact]
    public void RoundTripsNestedCollectionsAndDictionaries()
    {
        var model = new Dictionary<string, string>
        {
            ["name"] = "api",
            ["version"] = "1.20",
        };

        var yaml = YamlSerializer.Serialize(model, AppJsonContext.Default.DictionaryStringString);
        var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["name"].ShouldBe("api");
        result["version"].ShouldBe("1.20");
    }

    [Fact]
    public void RoundTripsNestedStructures()
    {
        var model = new List<Person>
        {
            new() { Name = "a", Age = 1 },
            new() { Name = "b", Age = 2 },
        };

        var yaml = YamlSerializer.Serialize(model, AppJsonContext.Default.ListPerson);
        var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.ListPerson);

        result.ShouldNotBeNull();
        result.Count.ShouldBe(2);
        result[0].Name.ShouldBe("a");
        result[1].Age.ShouldBe(2);
    }

    [Fact]
    public void RoundTripsQuotedAmbiguousStrings()
    {
        var model = new Dictionary<string, string>
        {
            ["enabled"] = "true",
            ["version"] = "1.20",
            ["count"] = "3",
            ["empty"] = "",
            ["nothing"] = "null",
            ["tilde"] = "~",
            ["date"] = "2026-01-01",
            ["hex"] = "0x1A",
        };

        var yaml = YamlSerializer.Serialize(model, AppJsonContext.Default.DictionaryStringString);
        var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();

        foreach (var pair in model)
        {
            result[pair.Key].ShouldBe(pair.Value);
        }
    }

    [Fact]
    public void PreservesScalarTypesThroughARoundTrip()
    {
        var json = """
            {
              "version": "1.20",
              "enabled": "true",
              "count": 3
            }
            """;

        var yaml = YamlSerializer.Serialize(JsonSerializer.Deserialize(json, AppJsonContext.Default.JsonElement)!, AppJsonContext.Default.JsonElement);

        yaml.ReplaceLineEndings().ShouldBe("version: '1.20'" + Environment.NewLine + "enabled: 'true'" + Environment.NewLine + "count: 3" + Environment.NewLine);

        var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.JsonElement);

        result.GetProperty("version").ValueKind.ShouldBe(JsonValueKind.String);
        result.GetProperty("enabled").ValueKind.ShouldBe(JsonValueKind.String);
        result.GetProperty("count").ValueKind.ShouldBe(JsonValueKind.Number);
    }

    [Fact]
    public void EmptyMappingsAndSequencesRoundTrip()
    {
        YamlSerializer.Deserialize("{}", AppJsonContext.Default.DictionaryStringString).ShouldNotBeNull().ShouldBeEmpty();
        YamlSerializer.Deserialize("[]", AppJsonContext.Default.ListInt32).ShouldNotBeNull().ShouldBeEmpty();
        YamlSerializer.Serialize(new Dictionary<string, string>(), AppJsonContext.Default.DictionaryStringString).ShouldBe("{}" + Environment.NewLine);
        YamlSerializer.Serialize(new List<int>(), AppJsonContext.Default.ListInt32).ShouldBe("[]" + Environment.NewLine);
    }

    [Fact]
    public void SupportsRootScalarsAndSequences()
    {
        YamlSerializer.Serialize(42, AppJsonContext.Default.Int32).ShouldBe("42" + Environment.NewLine);
        YamlSerializer.Deserialize("42", AppJsonContext.Default.Int32).ShouldBe(42);
        YamlSerializer.Serialize("hello", AppJsonContext.Default.String).ShouldBe("hello" + Environment.NewLine);
        YamlSerializer.Serialize(true, AppJsonContext.Default.Boolean).ShouldBe("true" + Environment.NewLine);
        YamlSerializer.Serialize(1.5, AppJsonContext.Default.Double).ShouldBe("1.5" + Environment.NewLine);
        YamlSerializer.Deserialize("- 1" + Environment.NewLine + "- 2" + Environment.NewLine, AppJsonContext.Default.ListInt32)
            .ShouldNotBeNull().ShouldBe(new List<int> { 1, 2 });
    }

    [Fact]
    public void ResolvesPlainScalarsPerTheDefaultCoreSchema()
    {
        YamlScalarShouldBe("value: yes", JsonValueKind.String, "yes");
        YamlScalarShouldBe("value: on", JsonValueKind.String, "on");
        YamlScalarShouldBe("value: no", JsonValueKind.String, "no");
        YamlScalarShouldBe("value: off", JsonValueKind.String, "off");
        YamlScalarShouldBe("value: y", JsonValueKind.String, "y");
        YamlScalarShouldBe("value: 00123", JsonValueKind.Number, raw: "123");
        YamlScalarShouldBe("value: 1e3", JsonValueKind.Number, raw: "1e3");
        YamlScalarShouldBe("value: 1.20", JsonValueKind.Number, raw: "1.20");
        YamlScalarShouldBe("value: 0.1", JsonValueKind.Number, raw: "0.1");
        YamlScalarShouldBe("value: -7", JsonValueKind.Number, raw: "-7");
        YamlScalarShouldBe("value: 2026-01-01", JsonValueKind.String, "2026-01-01");
        YamlScalarShouldBe("value: \"true\"", JsonValueKind.String, "true");
        YamlScalarShouldBe("value: \"00123\"", JsonValueKind.String, "00123");
        YamlScalarShouldBe("value: 0x1A", JsonValueKind.Number, raw: "26");
        YamlScalarShouldBe("value: TRUE", JsonValueKind.True);
        YamlScalarShouldBe("value: False", JsonValueKind.False);
        YamlScalarShouldBe("value: ~", JsonValueKind.Null);
        YamlScalarShouldBe("value: NULL", JsonValueKind.Null);
        YamlScalarShouldBe("value:", JsonValueKind.Null);
        YamlScalarShouldBe("value: !!str 123", JsonValueKind.String, "123");
    }

    [Fact]
    public void ResolvesPlainScalarsPerTheYamlJsonSchema()
    {
        var options = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json };
        foreach (var value in new[] { "text", "True", "~", "+42", "007", "0x1A", ".5" })
        {
            Should.Throw<YamlException>(() => YamlSerializer.Deserialize($"'value': {value}", AppJsonContext.Default.JsonElement, options));
        }

        var quoted = YamlSerializer.Deserialize("'value': 'text'", AppJsonContext.Default.JsonElement, options).GetProperty("value");
        quoted.ValueKind.ShouldBe(JsonValueKind.String);
        quoted.GetString().ShouldBe("text");

        var explicitString = YamlSerializer.Deserialize("'value': !!str text", AppJsonContext.Default.JsonElement, options).GetProperty("value");
        explicitString.ValueKind.ShouldBe(JsonValueKind.String);

        var trueElement = YamlSerializer.Deserialize("'value': true", AppJsonContext.Default.JsonElement, options).GetProperty("value");
        trueElement.ValueKind.ShouldBe(JsonValueKind.True);

        var nullElement = YamlSerializer.Deserialize("'value': null", AppJsonContext.Default.JsonElement, options).GetProperty("value");
        nullElement.ValueKind.ShouldBe(JsonValueKind.Null);

        var numberElement = YamlSerializer.Deserialize("'value': -12.5e+2", AppJsonContext.Default.JsonElement, options).GetProperty("value");
        numberElement.GetRawText().ShouldBe("-12.5e+2");

        var trailingPoint = YamlSerializer.Deserialize("'value': 5.", AppJsonContext.Default.JsonElement, options).GetProperty("value");
        trailingPoint.GetRawText().ShouldBe("5.0");
    }

    [Fact]
    public void JsonSchemaEmissionQuotesJsonStringValuesAndKeys()
    {
        var schemaOptions = new YamlSerializerOptions { ScalarSchema = YamlScalarSchema.Json };
        var value = JsonSerializer.Deserialize("""{"plain":"hello","ambiguous":"true"}""", AppJsonContext.Default.JsonElement);

        var yaml = YamlSerializer.Serialize(value, AppJsonContext.Default.JsonElement, schemaOptions);
        yaml.ShouldContain("'plain'");
        yaml.ShouldContain("'hello'");
        yaml.ShouldContain("'true'");

        var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.JsonElement, schemaOptions);
        result.GetProperty("plain").GetString().ShouldBe("hello");
        result.GetProperty("ambiguous").GetString().ShouldBe("true");
    }

    [Fact]
    public void RejectsSpecialFloatScalars()
    {
        foreach (var value in new[] { ".nan", ".inf", "-.inf" })
        {
            Should.Throw<YamlException>(() => YamlSerializer.Deserialize($"value: {value}", AppJsonContext.Default.JsonElement))
                .Message.ShouldContain("not supported");
        }
    }

    [Fact]
    public void SupportsBlockScalarsAndMultilineStrings()
    {
        var literal = YamlSerializer.Deserialize("value: |" + Environment.NewLine + "  line one" + Environment.NewLine + "  line two" + Environment.NewLine, AppJsonContext.Default.JsonElement);
        literal.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.String);
        literal.GetProperty("value").GetString().ShouldBe("line one" + Environment.NewLine + "line two" + Environment.NewLine);

        var model = new Dictionary<string, string> { ["text"] = "alpha" + Environment.NewLine + "beta" };
        var yaml = YamlSerializer.Serialize(model, AppJsonContext.Default.DictionaryStringString);
        var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["text"].ShouldBe("alpha" + Environment.NewLine + "beta");
    }

    [Fact]
    public void IgnoresComments()
    {
        var result = YamlSerializer.Deserialize("""
            # leading comment
            Name: John # trailing comment
            # another comment
            Age: 30
            """, AppJsonContext.Default.Person);

        result!.Name.ShouldBe("John");
        result.Age.ShouldBe(30);
    }

    [Fact]
    public void SupportsUnicode()
    {
        var model = new Dictionary<string, string> { ["emoji"] = "héllo 🎉" };

        var yaml = YamlSerializer.Serialize(model, AppJsonContext.Default.DictionaryStringString);
        var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["emoji"].ShouldBe("héllo 🎉");
    }

    [Fact]
    public void SupportsEscapedStrings()
    {
        var model = new Dictionary<string, string> { ["text"] = "tab\tnewline\\quote\"end" };

        var yaml = YamlSerializer.Serialize(model, AppJsonContext.Default.DictionaryStringString);
        var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["text"].ShouldBe(model["text"]);
    }

    [Fact]
    public void RoundTripsCarriageReturnAndLineEndingStringsAcrossStyles()
    {
        foreach (var value in new[] { "before\rafter", "before\r\nafter", "before\n\rafter", "\r", "\r\n" })
        {
            foreach (var style in new[] { YamlStringStyle.Auto, YamlStringStyle.Literal, YamlStringStyle.DoubleQuoted })
            {
                var yaml = YamlSerializer.Serialize(value, AppJsonContext.Default.String, new YamlSerializerOptions { StringStyle = style });
                YamlSerializer.Deserialize(yaml, AppJsonContext.Default.String).ShouldBe(value, $"style {style}");
            }
        }
    }

    [Fact]
    public void ExpandsAliases()
    {
        var result = YamlSerializer.Deserialize("""
            base: &anchor
              Name: John
              Age: 30
            copy: *anchor
            """, AppJsonContext.Default.JsonElement);

        result.GetProperty("base").GetProperty("Name").GetString().ShouldBe("John");
        result.GetProperty("copy").GetProperty("Age").GetInt32().ShouldBe(30);
    }

    [Fact]
    public void RejectsUndefinedAliases()
    {
        var exception = Should.Throw<YamlException>(() => YamlSerializer.Deserialize("""
            copy: *missing
            """, AppJsonContext.Default.Person));

        exception.Message.ShouldContain("missing");
    }

    [Fact]
    public void TreatsDoubleAngleKeysAsOrdinaryStrings()
    {
        var result = YamlSerializer.Deserialize("""
            plain:
              <<: value
            quoted:
              "<<": value
            tagged:
              !!str <<: value
            """, AppJsonContext.Default.JsonElement);

        result.GetProperty("plain").GetProperty("<<").GetString().ShouldBe("value");
        result.GetProperty("quoted").GetProperty("<<").GetString().ShouldBe("value");
        result.GetProperty("tagged").GetProperty("<<").GetString().ShouldBe("value");
    }

    [Fact]
    public void ExplicitMergeTagIsRejected()
    {
        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("value: !!merge <<", AppJsonContext.Default.JsonElement))
            .Message.ShouldContain("not supported");
    }

    [Fact]
    public void RejectsMultipleDocuments()
    {
        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("""
            Name: John
            ---
            Name: Jane
            """, AppJsonContext.Default.Person))
            .Message.ShouldContain("Multiple YAML documents");
    }

    [Fact]
    public void DeserializesTheFirstDocumentOfAStream()
    {
        var result = YamlSerializer.DeserializeFirstDocument("""
            Name: John
            Age: 30
            ---
            Name: Ignored
            """, AppJsonContext.Default.Person);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("John");
        result.Age.ShouldBe(30);
    }

    [Fact]
    public void DeserializesAllDocumentsOfAStream()
    {
        var results = YamlSerializer.DeserializeDocuments("""
            Name: John
            Age: 30
            ---
            Name: Jane
            Age: 42
            """, AppJsonContext.Default.Person);

        results.Count.ShouldBe(2);
        results[0]!.Name.ShouldBe("John");
        results[1]!.Name.ShouldBe("Jane");
        results[1]!.Age.ShouldBe(42);
    }

    [Fact]
    public void ReusedAnchorNamesAreScopedToTheirDocumentsWhenResolvingMerges()
    {
        var options = new YamlSerializerOptions { ResolveMergeKeys = true };
        var results = YamlSerializer.DeserializeDocuments("""
            name: first
            defaults: &defaults {source: first}
            labels: {<<: *defaults}
            ---
            name: second
            defaults: &defaults {source: second}
            labels: {<<: *defaults}
            """, AppJsonContext.Default.JsonElement, options);

        results[0]!.GetProperty("labels").GetProperty("source").GetString().ShouldBe("first");
        results[1]!.GetProperty("labels").GetProperty("source").GetString().ShouldBe("second");

        Should.Throw<YamlException>(() => YamlSerializer.DeserializeDocuments("""
            defaults: &defaults {source: first}
            ---
            labels: {<<: *defaults}
            """, AppJsonContext.Default.JsonElement, options));
    }

    [Fact]
    public void CommentOnlyDocumentsAreNullInDocumentStreams()
    {
        var results = YamlSerializer.DeserializeDocuments("""
            value: before
            --- # template emitted an empty document
            ---
            value: after
            """, AppJsonContext.Default.JsonElement);

        results.Count.ShouldBe(3);
        results[0]!.GetProperty("value").GetString().ShouldBe("before");
        results[1].ValueKind.ShouldBe(JsonValueKind.Null);
        results[2]!.GetProperty("value").GetString().ShouldBe("after");
    }

    [Fact]
    public void DeserializesAnEmptyStreamToAnEmptyList()
    {
        YamlSerializer.DeserializeDocuments("", AppJsonContext.Default.Person).ShouldBeEmpty();
    }

    [Fact]
    public void DeserializesStreamsWithMoreThanTheOldDefaultDocumentCount()
    {
        var yaml = string.Join("\n---\n", Enumerable.Range(0, 100).Select(i => $"{i}"));
        var results = YamlSerializer.DeserializeDocuments(yaml, AppJsonContext.Default.Int32);
        results.Count.ShouldBe(100);
        results[99].ShouldBe(99);
    }

    [Fact]
    public void SerializesMultipleDocuments()
    {
        var yaml = YamlSerializer.SerializeDocuments(
        [
            new Person { Name = "John", Age = 30 },
            new Person { Name = "Jane", Age = 42 },
        ], AppJsonContext.Default.Person);

        yaml.ReplaceLineEndings().ShouldBe("---" + Environment.NewLine + "Name: John" + Environment.NewLine + "Age: 30" + Environment.NewLine
            + "---" + Environment.NewLine + "Name: Jane" + Environment.NewLine + "Age: 42" + Environment.NewLine);

        var results = YamlSerializer.DeserializeDocuments(yaml, AppJsonContext.Default.Person);
        results.Count.ShouldBe(2);
        results[0]!.Name.ShouldBe("John");
        results[1]!.Age.ShouldBe(42);
    }

    [Fact]
    public void RejectsComplexMappingKeys()
    {
        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("""
            ? [1, 2]
            : value
            """, AppJsonContext.Default.JsonElement))
            .Message.ShouldContain("Complex mapping keys");
    }

    [Fact]
    public void RejectsCustomTags()
    {
        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("value: !Custom 123", AppJsonContext.Default.JsonElement))
            .Message.ShouldContain("not supported");
    }

    [Fact]
    public void RejectsInvalidYaml()
    {
        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("""
            Name: John
             Broken: [unclosed
            """, AppJsonContext.Default.Person));
    }

    [Fact]
    public void RejectsEmptyInput()
    {
        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("", AppJsonContext.Default.Person));
        Should.Throw<YamlException>(() => YamlSerializer.Deserialize("   ", AppJsonContext.Default.Person));
    }

    [Fact]
    public void DeserializesExplicitDocumentStart()
    {
        var yaml = YamlSerializer.Deserialize("""
            ---
            Name: John
            Age: 30
            """, AppJsonContext.Default.Person);

        yaml.ShouldNotBeNull();
        yaml.Name.ShouldBe("John");
    }

    [Fact]
    public void SupportsStreamOverloads()
    {
        var person = new Person { Name = "John", Age = 30 };

        using var serializeStream = new MemoryStream();
        YamlSerializer.Serialize(serializeStream, person, AppJsonContext.Default.Person);
        serializeStream.Flush();

        serializeStream.Position = 0;
        using var reader = new StreamReader(serializeStream, leaveOpen: true);
        reader.ReadToEnd().ShouldBe("Name: John" + Environment.NewLine + "Age: 30" + Environment.NewLine);

        serializeStream.Position = 0;
        var result = YamlSerializer.Deserialize(serializeStream, AppJsonContext.Default.Person);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("John");
        result.Age.ShouldBe(30);
    }

    [Fact]
    public void StreamOverloadsLeaveTheStreamOpen()
    {
        var person = new Person { Name = "John", Age = 30 };

        using var stream = new MemoryStream();
        YamlSerializer.Serialize(stream, person, AppJsonContext.Default.Person);

        stream.CanWrite.ShouldBeTrue();

        stream.Position = 0;
        YamlSerializer.Deserialize(stream, AppJsonContext.Default.Person);

        stream.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task SupportsAsyncStreamOverloads()
    {
        var person = new Person { Name = "John", Age = 30 };

        using var serializeStream = new MemoryStream();
        await YamlSerializer.SerializeAsync(serializeStream, person, AppJsonContext.Default.Person);
        serializeStream.Flush();

        serializeStream.Position = 0;
        using var reader = new StreamReader(serializeStream, leaveOpen: true);
        reader.ReadToEnd().ShouldBe("Name: John" + Environment.NewLine + "Age: 30" + Environment.NewLine);

        serializeStream.Position = 0;
        var result = await YamlSerializer.DeserializeAsync(serializeStream, AppJsonContext.Default.Person);

        result.ShouldNotBeNull();
        result.Name.ShouldBe("John");
        result.Age.ShouldBe(30);
    }

    [Fact]
    public async Task AsyncOverloadsLeaveTheStreamOpen()
    {
        var person = new Person { Name = "John", Age = 30 };

        using var stream = new MemoryStream();
        await YamlSerializer.SerializeAsync(stream, person, AppJsonContext.Default.Person);

        stream.CanWrite.ShouldBeTrue();

        stream.Position = 0;
        await YamlSerializer.DeserializeAsync(stream, AppJsonContext.Default.Person);

        stream.CanRead.ShouldBeTrue();
    }

    [Fact]
    public async Task AsyncOverloadsHandleUtf8BomAndLargePayloads()
    {
        var large = new string('x', 128 * 1024);
        var model = new Dictionary<string, string> { ["text"] = large };

        using var serializeStream = new MemoryStream();
        await YamlSerializer.SerializeAsync(serializeStream, model, AppJsonContext.Default.DictionaryStringString);
        serializeStream.Flush();

        serializeStream.Position = 0;
        var result = await YamlSerializer.DeserializeAsync(serializeStream, AppJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["text"].ShouldBe(large);
    }

    [Fact]
    public async Task AsyncOverloadsHonorCancellationToken()
    {
        var person = new Person { Name = "John", Age = 30 };

        using var serializeStream = new MemoryStream();

        // Deliberately passing a pre-cancelled token to verify cancellation is honored.
#pragma warning disable xUnit1051
        await Should.ThrowAsync<OperationCanceledException>(() =>
            YamlSerializer.SerializeAsync(serializeStream, person, AppJsonContext.Default.Person, null, new CancellationToken(canceled: true)));

        using var deserializeStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes("Name: John" + Environment.NewLine));

        await Should.ThrowAsync<OperationCanceledException>(() =>
            YamlSerializer.DeserializeAsync(deserializeStream, AppJsonContext.Default.Person, null, new CancellationToken(canceled: true)));
#pragma warning restore xUnit1051
    }

    [Fact]
    public void RejectsNullTypeInfo()
    {
        Should.Throw<ArgumentNullException>(() => YamlSerializer.Serialize(new Person(), (JsonTypeInfo<Person>)null!));
        Should.Throw<ArgumentNullException>(() => YamlSerializer.Deserialize("Name: John", (JsonTypeInfo<Person>)null!));
    }

    [Fact]
    public void RejectsNullYamlInput()
    {
        Should.Throw<ArgumentNullException>(() => YamlSerializer.Deserialize<Person>((string)null!));
        Should.Throw<ArgumentNullException>(() => YamlSerializer.Deserialize((string)null!, AppJsonContext.Default.Person));
        Should.Throw<ArgumentNullException>(() => YamlSerializer.Deserialize((Stream)null!, AppJsonContext.Default.Person));
    }

    [Fact]
    public void HandlesLargeIntegerScalarsWithoutLosingPrecision()
    {
        var yaml = "value: 123456789012345678901234567890";
        var element = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.JsonElement);

        element.GetProperty("value").ValueKind.ShouldBe(JsonValueKind.Number);
        element.GetProperty("value").GetRawText().ShouldBe("123456789012345678901234567890");
    }

    [Fact]
    public void RejectsDuplicateKeysByDefault()
    {
        var exception = Should.Throw<YamlException>(() => YamlSerializer.Deserialize("""
            Name: John
            Name: Jane
            """, AppJsonContext.Default.Person));

        exception.Message.ShouldContain("duplicate mapping key");
    }

    private static void YamlScalarShouldBe(string yaml, JsonValueKind kind, string? value = null, string? raw = null)
    {
        var element = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.JsonElement).GetProperty("value");

        element.ValueKind.ShouldBe(kind, $"YAML '{yaml}'");

        if (value != null)
        {
            element.GetString().ShouldBe(value, $"YAML '{yaml}'");
        }

        if (raw != null)
        {
            element.GetRawText().ShouldBe(raw, $"YAML '{yaml}'");
        }
    }
}

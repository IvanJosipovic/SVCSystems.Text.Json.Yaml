using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using SVCSystems.Text.Json.Yaml;

namespace SVCSystems.Text.Json.Yaml.Tests;

/// <summary>
/// Verifies that JSON serialization settings and YAML formatting settings stay independent and
/// each option has a single authoritative owner (plan phase 5).
/// </summary>
public partial class YamlSerializerOptionsTests
{
    public sealed class NamedModel
    {
        public string FullName { get; set; } = "";

        public string? Nickname { get; set; }
    }

    [JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
    [JsonSerializable(typeof(NamedModel))]
    [JsonSerializable(typeof(UpperCaseModel))]
    [JsonSerializable(typeof(Dictionary<string, List<int>>))]
    [JsonSerializable(typeof(Dictionary<string, Dictionary<string, int>>))]
    [JsonSerializable(typeof(Dictionary<string, string>))]
    internal sealed partial class OptionsJsonContext : JsonSerializerContext;

    public sealed class UpperCaseModel
    {
        [JsonConverter(typeof(UpperCaseValueConverter))]
        public string Value { get; set; } = "";
    }

    public sealed class UpperCaseValueConverter : JsonConverter<string>
    {
        public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        {
            return reader.GetString()!.ToUpperInvariant();
        }

        public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        {
            writer.WriteStringValue(value.ToUpperInvariant());
        }
    }

    [Fact]
    public void NamingPolicyControlsPropertyNames()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var model = new NamedModel { FullName = "John Doe" };

        var yaml = YamlSerializer.Serialize(model, jsonOptions);

        yaml.ShouldContain("fullName: John Doe");
        yaml.ShouldNotContain("FullName");
    }

    [Fact]
    public void WhenWritingNullOmitsNullProperties()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var jsonOptions = new JsonSerializerOptions { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };
        var model = new NamedModel { FullName = "John", Nickname = null };

        var yaml = YamlSerializer.Serialize(model, jsonOptions);

        yaml.ShouldContain("FullName: John");
        yaml.ShouldNotContain("Nickname");

        var result = YamlSerializer.Deserialize<NamedModel>(yaml, jsonOptions);

        result.ShouldNotBeNull();
        result.FullName.ShouldBe("John");
        result.Nickname.ShouldBeNull();
    }

    [Fact]
    public void CustomJsonConverterControlsSerializedValue()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var jsonOptions = new JsonSerializerOptions { Converters = { new UpperCaseValueConverter() } };
        var model = new UpperCaseModel { Value = "hello" };

        var yaml = YamlSerializer.Serialize(model, jsonOptions);

        yaml.ShouldContain("Value: HELLO");

        var result = YamlSerializer.Deserialize<UpperCaseModel>("Value: world" + Environment.NewLine, jsonOptions);

        result.ShouldNotBeNull();
        result.Value.ShouldBe("WORLD");
    }

    [Fact]
    public void SourceGeneratedNamingAndConvertersTakePrecedence()
    {
        // OptionsJsonContext uses camelCase naming via JsonSourceGenerationOptions and the
        // UpperCaseModel carries a custom converter attribute. Neither requires JsonSerializerOptions.
        var model = new NamedModel { FullName = "John Doe" };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.NamedModel);

        yaml.ShouldContain("fullName: John Doe");

        var upper = YamlSerializer.Serialize(new UpperCaseModel { Value = "hello" }, OptionsJsonContext.Default.UpperCaseModel);

        upper.ShouldContain("value: HELLO");

        var result = YamlSerializer.Deserialize("value: world" + Environment.NewLine, OptionsJsonContext.Default.UpperCaseModel);

        result.ShouldNotBeNull();
        result.Value.ShouldBe("WORLD");
    }

    [Fact]
    public void IndentationChangesOnlyFormatting()
    {
        var yamlOptions = new YamlSerializerOptions { IndentSize = 4 };
        var model = new Dictionary<string, Dictionary<string, int>> { ["outer"] = new() { ["inner"] = 1 } };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.DictionaryStringDictionaryStringInt32, yamlOptions);

        yaml.ReplaceLineEndings().ShouldBe("outer:" + Environment.NewLine + "    inner: 1" + Environment.NewLine);

        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.DictionaryStringDictionaryStringInt32);

        result.ShouldNotBeNull();
        result["outer"]["inner"].ShouldBe(1);
    }

    [Fact]
    public void EmitDocumentStartWritesTheDocumentMarker()
    {
        var yamlOptions = new YamlSerializerOptions { EmitDocumentStart = true };
        var model = new NamedModel { FullName = "John" };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.NamedModel, yamlOptions);

        yaml.ReplaceLineEndings().ShouldBe("---" + Environment.NewLine + "fullName: John" + Environment.NewLine + "nickname: null" + Environment.NewLine);

        // The emitted document must still deserialize.
        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.NamedModel);

        result.ShouldNotBeNull();
        result.FullName.ShouldBe("John");
    }

    [Fact]
    public void UseFlowStyleWritesFlowCollections()
    {
        var yamlOptions = new YamlSerializerOptions { UseFlowStyle = true };
        var model = new Dictionary<string, List<int>> { ["values"] = [1, 2] };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.DictionaryStringListInt32, yamlOptions);

        yaml.ReplaceLineEndings().ShouldBe("{values: [1, 2]}" + Environment.NewLine);

        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.DictionaryStringListInt32);

        result.ShouldNotBeNull();
        result["values"].ShouldBe([1, 2]);
    }

    [Fact]
    public void YamlOptionsDoNotMutateJsonOptions()
    {
        ReflectionOnlyTests.SkipWhenReflectionUnavailable();

        var jsonOptions = new JsonSerializerOptions { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
        var before = (jsonOptions.PropertyNamingPolicy, jsonOptions.DefaultIgnoreCondition, jsonOptions.ReadCommentHandling);
        var yamlOptions = new YamlSerializerOptions { IndentSize = 8, UseFlowStyle = true, EmitDocumentStart = true };

        YamlSerializer.Serialize(new NamedModel { FullName = "John" }, jsonOptions, yamlOptions);

        (jsonOptions.PropertyNamingPolicy, jsonOptions.DefaultIgnoreCondition, jsonOptions.ReadCommentHandling).ShouldBe(before);
    }

    [Fact]
    public void InvalidIndentationIsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { IndentSize = 0 })
            .ParamName.ShouldBe("value");
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { IndentSize = 10 });
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { IndentSize = -1 });
    }

    [Fact]
    public void UndefinedScalarSchemaIsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { ScalarSchema = (YamlScalarSchema)99 });
    }

    [Fact]
    public void CoreScalarSchemaIsTheDefault()
    {
        new YamlSerializerOptions().ScalarSchema.ShouldBe(YamlScalarSchema.Core);
        new YamlSerializerOptions().IndentSize.ShouldBe(2);
        new YamlSerializerOptions().EmitDocumentStart.ShouldBeFalse();
        new YamlSerializerOptions().UseFlowStyle.ShouldBeFalse();
        new YamlSerializerOptions().Width.ShouldBe(80);
        new YamlSerializerOptions().MaxSimpleKeyLength.ShouldBe(128);
        new YamlSerializerOptions().IndentedSequences.ShouldBeFalse();
        new YamlSerializerOptions().StringStyle.ShouldBe(YamlStringStyle.Auto);
    }

    [Fact]
    public void EmitterOptionsAreValidated()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { Width = 0 });
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { MaxSimpleKeyLength = 0 });
        Should.Throw<ArgumentNullException>(() => new YamlSerializerOptions { NewLine = null! });
        // System.Text.Json parity: only "\n" and "\r\n" are valid newline sequences.
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { NewLine = "" });
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { NewLine = "\r" });
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { NewLine = "\n\r" });
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { NewLine = "\r\n\r\n" });
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { StringStyle = (YamlStringStyle)42 });
    }

    [Fact]
    public void WidthFoldsLongPlainScalars()
    {
        var yamlOptions = new YamlSerializerOptions { Width = 20 };
        var model = new Dictionary<string, string> { ["text"] = "aaaaaaaaaa bbbbbbbbbb cccccccccc" };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.DictionaryStringString, yamlOptions);

        // The emitter folds the long plain scalar at the configured width; the value must
        // round trip exactly.
        yaml.ShouldNotContain("aaaaaaaaaa bbbbbbbbbb cccccccccc");

        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["text"].ShouldBe("aaaaaaaaaa bbbbbbbbbb cccccccccc");
    }

    [Fact]
    public void NewLineDefaultsToEnvironmentNewLine()
    {
        // System.Text.Json parity: JsonWriterOptions.NewLine defaults to Environment.NewLine.
        new YamlSerializerOptions().NewLine.ShouldBe(Environment.NewLine);
    }

    [Fact]
    public void NewLineControlsTheLineBreakSequence()
    {
        var yamlOptions = new YamlSerializerOptions { NewLine = "\r\n" };
        var model = new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.DictionaryStringString, yamlOptions);

        yaml.ShouldContain("\r\n");
        // Every line break must be the configured sequence.
        yaml.Replace("\r\n", "").ShouldNotContain("\n");

        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["a"].ShouldBe("1");
    }

    [Fact]
    public void LongKeysAreEmittedAsExplicitKeys()
    {
        var longKey = new string('k', 40);
        var yamlOptions = new YamlSerializerOptions { MaxSimpleKeyLength = 20 };
        var model = new Dictionary<string, string> { [longKey] = "value" };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.DictionaryStringString, yamlOptions);

        yaml.ShouldContain("? " + longKey);

        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result[longKey].ShouldBe("value");
    }

    [Fact]
    public void IndentedSequencesIndentDashEntries()
    {
        var yamlOptions = new YamlSerializerOptions { IndentedSequences = true };
        var model = new Dictionary<string, List<int>> { ["values"] = [1, 2] };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.DictionaryStringListInt32, yamlOptions);

        yaml.ReplaceLineEndings("\n").ShouldBe("values:\n  - 1\n  - 2\n");

        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.DictionaryStringListInt32);

        result.ShouldNotBeNull();
        result["values"].ShouldBe([1, 2]);
    }

    [Fact]
    public void DoubleQuotedStringStyleQuotesAllStrings()
    {
        var yamlOptions = new YamlSerializerOptions { StringStyle = YamlStringStyle.DoubleQuoted };
        var model = new Dictionary<string, string> { ["key"] = "hello" };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.DictionaryStringString, yamlOptions);

        yaml.ReplaceLineEndings("\n").ShouldBe("\"key\": \"hello\"\n");

        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["key"].ShouldBe("hello");
    }

    [Fact]
    public void LiteralStringStyleUsesBlockScalarsForValues()
    {
        var yamlOptions = new YamlSerializerOptions { StringStyle = YamlStringStyle.Literal };
        var model = new Dictionary<string, string> { ["key"] = "hello" };

        var yaml = YamlSerializer.Serialize(model, OptionsJsonContext.Default.DictionaryStringString, yamlOptions);

        yaml.ReplaceLineEndings("\n").ShouldBe("key: |-\n  hello\n");

        var result = YamlSerializer.Deserialize(yaml, OptionsJsonContext.Default.DictionaryStringString);

        result.ShouldNotBeNull();
        result["key"].ShouldBe("hello");
    }
}

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
        var yamlOptions = new YamlSerializerOptions { Indentation = 4 };
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
        var yamlOptions = new YamlSerializerOptions { Indentation = 8, UseFlowStyle = true, EmitDocumentStart = true };

        YamlSerializer.Serialize(new NamedModel { FullName = "John" }, jsonOptions, yamlOptions);

        (jsonOptions.PropertyNamingPolicy, jsonOptions.DefaultIgnoreCondition, jsonOptions.ReadCommentHandling).ShouldBe(before);
    }

    [Fact]
    public void InvalidIndentationIsRejected()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { Indentation = 0 })
            .ParamName.ShouldBe("value");
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { Indentation = 10 });
        Should.Throw<ArgumentOutOfRangeException>(() => new YamlSerializerOptions { Indentation = -1 });
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
        new YamlSerializerOptions().Indentation.ShouldBe(2);
        new YamlSerializerOptions().EmitDocumentStart.ShouldBeFalse();
        new YamlSerializerOptions().UseFlowStyle.ShouldBeFalse();
        new YamlSerializerOptions().AllowDuplicateKeys.ShouldBeFalse();
    }
}

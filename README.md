# SVCSystems.Text.Json.Yaml

[![Nuget](https://img.shields.io/nuget/vpre/SVCSystems.Text.Json.Yaml.svg?style=flat-square)](https://www.nuget.org/packages/SVCSystems.Text.Json.Yaml)
[![Nuget)](https://img.shields.io/nuget/dt/SVCSystems.Text.Json.Yaml.svg?style=flat-square)](https://www.nuget.org/packages/SVCSystems.Text.Json.Yaml)
[![codecov](https://codecov.io/gh/IvanJosipovic/SVCSystems.Text.Json.Yaml/branch/main/graph/badge.svg)](https://codecov.io/gh/IvanJosipovic/SVCSystems.Text.Json.Yaml)

YAML serialization and deserialization for .NET powered by the existing `System.Text.Json`
serialization infrastructure.

Reuse the contracts you already have: `JsonSerializerOptions`, `JsonSerializerContext`,
`JsonTypeInfo<T>`, converters and attributes. There is no separate YAML serialization contract to
maintain, and no second source generator.

- **`System.Text.Json` is authoritative for .NET object serialization.** Property naming, custom
  converters, polymorphism, null handling, number handling and reference handling are all owned by
  the options you already use.
- **[YamlDotNet](https://github.com/aaubry/YamlDotNet) is an implementation detail.** It is used
  internally to parse and emit YAML; its builders and object graph engine are not exposed.
- **The library bridges JSON tokens and YAML events.** Objects are never materialized as
  `JsonNode`/`JsonDocument`/dictionaries in between.
- **Works with or without source generated contexts**, and supports Native AOT when source
  generated contracts are supplied.

## Installation

```sh
dotnet add package SVCSystems.Text.Json.Yaml
```

Targets .NET 10 and .NET 11.

## Basic serialization

```csharp
using SVCSystems.Text.Json.Yaml;

public sealed class Person
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

var person = new Person { Name = "John", Age = 30 };

var yaml = YamlSerializer.Serialize(person);
// Name: John
// Age: 30

var result = YamlSerializer.Deserialize<Person>(yaml);
```

## Basic deserialization

```csharp
var person = YamlSerializer.Deserialize<Person>("""
    Name: Jane
    Age: 42
    """);
```

## Using `JsonSerializerOptions`

JSON settings control everything about .NET object mapping; they are passed straight through to
`JsonSerializer`:

```csharp
var jsonOptions = new JsonSerializerOptions
{
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
};

var yaml = YamlSerializer.Serialize(person, jsonOptions: jsonOptions);
var roundTrip = YamlSerializer.Deserialize<Person>(yaml, jsonOptions);
```

`WriteIndented` is a JSON setting and is not reinterpreted as YAML indentation. YAML indentation is
owned by `YamlSerializerOptions`.

## Using `YamlSerializerOptions`

`YamlSerializerOptions` contains only YAML specific settings:

```csharp
public sealed class YamlSerializerOptions
{
    public int Indentation { get; set; } = 2;          // 1..9 spaces for nested block collections
    public bool EmitDocumentStart { get; set; }        // emit an explicit '---' marker
    public bool UseFlowStyle { get; set; }             // {a: 1} and [1, 2] instead of block style
    public YamlScalarSchema ScalarSchema { get; set; } = YamlScalarSchema.Core; // or Json
    public bool AllowDuplicateKeys { get; set; }       // false: reject, true: last value wins
    public bool AllowMergeKeys { get; set; } = true;   // apply '<<' merge keys
    public SpecialFloatBehavior SpecialFloats { get; set; } // Reject (default) or String
    public UnknownTagBehavior UnknownTags { get; set; }     // Reject (default) or Ignore
    public bool AllowBinaryTag { get; set; }           // accept '!!binary' as a base64 string
    public bool AllowTimestampTag { get; set; }        // accept '!!timestamp' as a string
    public int MaxDepth { get; set; } = 64;            // nesting limit
    public int MaxInputLength { get; set; }            // 0: unlimited; enforced on string and stream inputs
    public int MaxScalarLength { get; set; }           // 0: unlimited
    public int MaxAliasExpansionEvents { get; set; } = 1_000_000;
    public int MaxDocuments { get; set; } = 16;        // multi document APIs
    public int Width { get; set; } = 80;               // emitter folding width
    public int MaxSimpleKeyLength { get; set; } = 128; // longer keys become explicit '? key' entries
    public string NewLine { get; set; } = Environment.NewLine;
    public bool IndentedSequences { get; set; }        // indent '- ' entries under their key
    public YamlStringStyle StringStyle { get; set; }   // Auto (default), Literal, DoubleQuoted
}
```

```csharp
var yamlOptions = new YamlSerializerOptions
{
    Indentation = 4,
    EmitDocumentStart = true,
};

var yaml = YamlSerializer.Serialize(person, yamlOptions: yamlOptions);
```

### Configuration ownership

| Concern | Owner |
|---|---|
| Property naming, ordering, ignoring | `JsonSerializerOptions` |
| Custom converters and factories | `JsonSerializerOptions` |
| Null handling, number handling, reference handling | `JsonSerializerOptions` |
| Polymorphism, required members, extension data | `JsonSerializerOptions` |
| Indentation, document markers, flow style | `YamlSerializerOptions` |
| Scalar resolution schema, duplicate keys | `YamlSerializerOptions` |

YAML options never mutate or override the supplied `JsonSerializerOptions`, and no JSON setting is
duplicated in `YamlSerializerOptions`.

## Using a `JsonSerializerContext` (source generated)

Pass the contract you already generate; nothing about the context changes:

```csharp
[JsonSerializable(typeof(Person))]
internal partial class AppJsonContext : JsonSerializerContext;

var yaml = YamlSerializer.Serialize(person, AppJsonContext.Default.Person);
Person? result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.Person);
```

Contexts generated in other assemblies work as-is, including their custom converters:

```csharp
var yaml = YamlSerializer.Serialize(model, ThirdPartyJsonContext.Default.Model);
var result = YamlSerializer.Deserialize(yaml, ThirdPartyJsonContext.Default.Model);
```

When an explicit `JsonTypeInfo<T>` is supplied it is used as-is; the serializer does not create a
replacement contract.

## Custom converter support

Because `System.Text.Json` performs all object mapping, existing converters keep working:

```csharp
public sealed class UpperCaseStringConverter : JsonConverter<string>
{
    public override string Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
        => reader.GetString()!.ToUpperInvariant();

    public override void Write(Utf8JsonWriter writer, string value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToUpperInvariant());
}

var options = new JsonSerializerOptions { Converters = { new UpperCaseStringConverter() } };
var yaml = YamlSerializer.Serialize(model, options);
```

Verified compatibility (see the test suite): `JsonPropertyName`, `JsonIgnore`,
`JsonPropertyOrder`, `JsonConverter`/`JsonConverterFactory`, `JsonStringEnumConverter`,
`JsonExtensionData`, `JsonRequired`, `JsonConstructor` (record/constructor parameter binding),
`JsonNumberHandling`, `JsonPolymorphic`/`JsonDerivedType`, `PropertyNamingPolicy`,
`DictionaryKeyPolicy`, `DefaultIgnoreCondition`, `PropertyNameCaseInsensitive`,
`UnmappedMemberHandling` and nullable value types. Do not assume universal compatibility for
exotic converter behavior; it is bound by what `JsonSerializer` itself supports.

## Native AOT

The `JsonTypeInfo<T>` overloads are trim and AOT safe:

```csharp
var yaml = YamlSerializer.Serialize(person, AppJsonContext.Default.Person);
var result = YamlSerializer.Deserialize(yaml, AppJsonContext.Default.Person);
```

The reflection based overloads (those accepting `JsonSerializerOptions`) are annotated with
`RequiresUnreferencedCode`/`RequiresDynamicCode` and are not usable in Native AOT.

For AOT applications, publish with reflection based JSON serialization disabled so the linker
proves no reflection fallback is used:

```xml
<PropertyGroup>
  <PublishAot>true</PublishAot>
  <PublishTrimmed>true</PublishTrimmed>
  <JsonSerializerIsReflectionEnabledByDefault>false</JsonSerializerIsReflectionEnabledByDefault>
</PropertyGroup>
```

## YAML scalar handling

The default `YamlScalarSchema.Core` implements the YAML 1.2 core schema (section 10.3 of the
specification):

| YAML input | Resolves to |
|---|---|
| `true`, `True`, `false` | boolean |
| `null`, `Null`, `NULL`, `~`, empty | null |
| `42`, `-7`, `+42`, `007` | integer (leading zeros are decimal) |
| `0x1A`, `0o17` | integer (core schema; pass `YamlScalarSchema.Json` to keep them strings) |
| `1.25`, `1.20`, `1e5`, `.5`, `5.` | number |
| `"1.20"`, `'true'`, `"null"` | string (quoted scalars are always strings) |
| `yes`, `no`, `on`, `off`, `y` | string (YAML 1.1 booleans are not core-schema booleans) |
| `2026-01-01` | string (timestamps have no JSON equivalent) |
| `.inf`, `-.inf`, `.nan` | rejected by default (no JSON number representation); set `SpecialFloats = String` to keep the text as a string |

All numeric parsing is culture invariant. Integers that exceed 64 bits keep their exact value.

### Scalar preservation

Serialization quotes strings that a YAML parser could misread as numbers, booleans or null, so
JSON string values survive a round trip exactly:

```csharp
var json = """{"version":"1.20","enabled":"true","count":3}""";
var yaml = YamlSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json));
// version: '1.20'
// enabled: 'true'
// count: 3
```

## Supported YAML subset

Supported: block and flow mappings and sequences, plain/quoted/block scalars, comments (ignored),
explicit keys (`? key : value`), anchors and aliases (expanded, with an expansion budget against
"billion laughs"), merge keys (`<<`, spec precedence), explicit document start (`---`), multiple
documents (via the dedicated APIs), the standard YAML tags (`!!str`, `!!int`, `!!float`, `!!bool`,
`!!null`, and optionally `!!binary`/`!!timestamp`), and configurable nesting depth.

The default `YamlScalarSchema.Core` follows the YAML 1.2 core schema; see
[docs/yaml-1.2-compat-plan.md](docs/yaml-1.2-compat-plan.md) for the measured conformance against
the official yaml-test-suite.

Explicitly rejected with a descriptive error (including line/column where possible), unless the
corresponding policy is enabled:

- Complex mapping keys (sequences/mappings used as keys)
- Custom and non-standard tags (for example `!MyType`), unless `UnknownTags = Ignore`
- `!!binary`/`!!timestamp`, unless `AllowBinaryTag`/`AllowTimestampTag` is enabled
- Special floats `.inf`/`.nan`, unless `SpecialFloats = String`
- Multiple documents in a single `Deserialize` call (use `DeserializeFirstDocument` or
  `DeserializeDocuments`)
- Duplicate mapping keys, unless `AllowDuplicateKeys` is enabled (then the last value wins)
- Merge keys, unless `AllowMergeKeys` (enabled by default)
- Limits: `MaxDepth` (64), optional `MaxInputLength`/`MaxScalarLength`, alias expansion budget

## Multi document streams

```csharp
// One .NET value per document.
List<Person?> people = YamlSerializer.DeserializeDocuments(yamlText, AppJsonContext.Default.Person);

// Only the first document.
Person? first = YamlSerializer.DeserializeFirstDocument(yamlText, AppJsonContext.Default.Person);

// Every document is emitted with an explicit '---' marker.
string stream = YamlSerializer.SerializeDocuments([a, b, c], AppJsonContext.Default.Person);
```

## Streams

```csharp
using var file = File.OpenRead("config.yaml");
var config = YamlSerializer.Deserialize(file, ConfigJsonContext.Default.Config);

using var output = File.Create("out.yaml");
YamlSerializer.Serialize(output, config, ConfigJsonContext.Default.Config);
```

Asynchronous variants perform stream I/O asynchronously and honor a
`CancellationToken` (parsing and object serialization are synchronous CPU work performed after
the I/O completes):

```csharp
using var file = File.OpenRead("config.yaml");
var config = await YamlSerializer.DeserializeAsync(file, ConfigJsonContext.Default.Config, cancellationToken: ct);

using var output = File.Create("out.yaml");
await YamlSerializer.SerializeAsync(output, config, ConfigJsonContext.Default.Config, cancellationToken: ct);
```

## Known limitations

- Comments and YAML formatting (quoting styles, indentation, anchors) are not preserved on round
  trips: the JSON data model that bridges YAML to .NET objects has no slot for them, and the
  specification assigns comments no data meaning. Structural fidelity is covered by the
  conformance suite; a comment-preserving rewrite API would be a separate, non-JSON tool.
- A small number of yaml-test-suite cases deviate from the upstream YamlDotNet scanner (tab
  handling, some block scalar chomping edge cases); each is named and skipped with a reason in
  the conformance suite.
- Intermediate JSON is buffered; the reflection based overloads need runtime reflection.
- YamlDotNet's emitter escapes some non-ASCII scalars (for example emoji) using double quoted
  escapes; the values still round trip exactly.
- Block sequences under a mapping key are emitted at the parent indentation level
  (YamlDotNet's default), which is valid YAML but visually "indentless".

## Performance considerations

The pipeline is `object → JSON (System.Text.Json) → YAML (token bridge)`. Intermediate JSON is
staged in pooled buffers (`ArrayPool<byte>` backed writers), so no large intermediate arrays are
allocated per call. Benchmarks (BenchmarkDotNet, `benchmarks/`) compare YamlDotNet's reflection
based serializer with this library in reflection and source generated modes:

```sh
dotnet run --project benchmarks/SVCSystems.Text.Json.Yaml.Benchmarks -c Release -- --filter '*'
```

Correctness and compatibility were prioritized over micro-optimization; streaming and zero-copy
conversion paths are future work.

### Recorded results

BenchmarkDotNet `ShortRun`, linux-x64, .NET 11 (times relative to YamlDotNet reflection
serialization = 1.00):

| Scenario | Serialize time | Serialize alloc | Deserialize time | Deserialize alloc |
|---|---|---|---|---|
| Small config (6 properties) | 0.31x (contracts) | 0.35x | 0.65x | 0.84x |
| Kubernetes manifest (10 containers) | 0.38x | 0.44x | 0.63x | 0.98x |
| 1,000 item list | 0.36x | 0.40x | 0.66x | 0.95x |
| 64 KB scalar document | 0.97x | 0.93x | 0.72x | 1.44x |

Reflection mode tracks the source generated mode closely (within ~5-10%). The token bridge is
faster and allocates less than YamlDotNet's reflection based object engine for structured data.
Emitter output is encoded directly into pooled UTF-8 buffers (no intermediate `StringBuilder`).
The one remaining deficit is deserialize allocations for a single large scalar, which is
YamlDotNet scanner-internal (it materializes the scalar as a UTF-16 string while scanning) and
is therefore only addressable by the UTF-8 native front-end work.

## Building from source

```sh
dotnet build SVCSystems.Text.Json.Yaml.slnx
dotnet run --project tests/SVCSystems.Text.Json.Yaml.Tests -c Debug
dotnet test  # uses the Microsoft.Testing.Platform runner (configured in global.json)
dotnet pack src/SVCSystems.Text.Json.Yaml -c Release
```

Native AOT validation:

```sh
dotnet publish tests/SVCSystems.Text.Json.Yaml.AotTests -c Release -r linux-x64
./artifacts/aot-tests/SVCSystems.Text.Json.Yaml.AotTests
```

## Contributing

This repository uses **[Conventional Commits](https://www.conventionalcommits.org/)**:
[semantic-release](https://semantic-release.gitbook.io/semantic-release/) derives the package
version and changelog from commit messages, and pull request titles are linted against the
convention. Use `feat:`, `fix:`, `docs:`, `chore:` and similar prefixes.

## License

MIT. Portions adapted from [YamlDotNet.System.Text.Json](https://github.com/IvanJosipovic/YamlDotNet.System.Text.Json).

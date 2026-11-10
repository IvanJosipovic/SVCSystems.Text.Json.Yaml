# SVCSystems.Text.Json.Yaml

[![Nuget](https://img.shields.io/nuget/vpre/SVCSystems.Text.Json.Yaml.svg?style=flat-square)](https://www.nuget.org/packages/SVCSystems.Text.Json.Yaml)
[![Nuget)](https://img.shields.io/nuget/dt/SVCSystems.Text.Json.Yaml.svg?style=flat-square)](https://www.nuget.org/packages/SVCSystems.Text.Json.Yaml)
[![codecov](https://codecov.io/gh/IvanJosipovic/SVCSystems.Text.Json.Yaml/graph/badge.svg?token=VjTYjTh4oA)](https://codecov.io/gh/IvanJosipovic/SVCSystems.Text.Json.Yaml)

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

The options-based overloads mirror `System.Text.Json`'s surface, including streams, bytes and
`SerializeToUtf8Bytes` (`SerializeToUtf8Bytes<T>(T, JsonSerializerOptions?, YamlSerializerOptions?)`,
`Serialize(Stream, T, JsonSerializerOptions?, YamlSerializerOptions?)`,
`SerializeAsync(Stream, T, ...)`, `Deserialize<T>(Stream, JsonSerializerOptions?, ...)`,
`DeserializeAsync<T>(Stream, ...)`,
`Deserialize<T>(ReadOnlyMemory<byte>, JsonSerializerOptions?, ...)`). They are annotated with
`RequiresUnreferencedCode`/`RequiresDynamicCode` exactly like the corresponding
`System.Text.Json` overloads.

`WriteIndented` is a JSON setting and is not reinterpreted as YAML indentation. YAML indentation is
owned by `YamlSerializerOptions`.

## Using `YamlSerializerOptions`

`YamlSerializerOptions` contains only YAML specific settings:

```csharp
public sealed class YamlSerializerOptions
{
    public int IndentSize { get; set; } = 2;          // 1..9 spaces for nested block collections
    public bool EmitDocumentStart { get; set; }        // emit an explicit '---' marker
    public bool UseFlowStyle { get; set; }             // {a: 1} and [1, 2] instead of block style
    public YamlScalarSchema ScalarSchema { get; set; } = YamlScalarSchema.Core; // or Json
    public bool ResolveMergeKeys { get; set; }               // false by default; YAML 1.1 extension
    public YamlDuplicateKeyHandling DuplicateKeyHandling { get; set; } // Reject by default
    public int MaxDepth { get; set; } = 64;            // nesting limit
    public int MaxScalarLength { get; set; }           // post-scan acceptance limit; 0: unlimited
    public int MaxAliasExpansionEvents { get; set; } = 1_000_000;
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
    IndentSize = 4,
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
| IndentSize, document markers, flow style | `YamlSerializerOptions` |
| Scalar resolution schema | `YamlSerializerOptions` |
| YAML merge-key resolution and duplicate-key policy | `YamlSerializerOptions` |

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

`YamlScalarSchema.Core` is the default and implements YAML 1.2.2 Core scalar resolution. Set
`ScalarSchema = YamlScalarSchema.Json` for the YAML 1.2.2 JSON schema (not JSON syntax; YAML
collections and syntax remain available). Its implicit resolution table has no plain-string
fallback: unmatched plain scalars are errors and strings must be quoted or tagged `!!str`.

| YAML input | Core | Json |
|---|---|---|
| `true`, `True`, `false` | boolean | lowercase `true`/`false` are boolean; other forms error |
| `null`, `Null`, `NULL`, `~`, empty | null | lowercase `null` is null; other forms error |
| `42`, `-7`, `+42`, `007` | integer (leading zeros are decimal) | JSON-form integers; `+42` and `007` error |
| `0x1A`, `0o17` | integer | error |
| `1.25`, `1.20`, `1e5`, `5.`, `.5` | number | `1.25`, `1.20`, `1e5`, and `5.` are floats; `.5` errors |
| `"1.20"`, `'true'`, `"null"` | string | string |
| `yes`, `no`, `on`, `off`, `y`, `2026-01-01` | string | error unless quoted or tagged `!!str` |
| `.inf`, `-.inf`, `.nan` | rejected (no JSON number representation) | error (no matching JSON-schema rule) |

Numeric tokens are normalized to valid JSON number syntax without floating-point rounding; integers
retain arbitrary precision. Explicit standard tags validate the scalar against the selected schema,
regardless of quoting style. Under the Json schema, output strings (including object keys) are
quoted so they remain valid strings when read with that schema.

### Scalar preservation

With the default Core schema, serialization quotes strings that could be resolved as numbers,
booleans or null. Under the Json schema, every emitted string key and value is quoted because plain
scalars have no implicit string fallback. JSON string values therefore survive round trips exactly:

```csharp
var json = """{"version":"1.20","enabled":"true","count":3}""";
var yaml = YamlSerializer.Serialize(JsonSerializer.Deserialize<JsonElement>(json));
// version: '1.20'
// enabled: 'true'
// count: 3
```

## Supported YAML subset

Supported: block and flow mappings and sequences, plain/quoted/block scalars, comments (ignored),
explicit keys (`? key : value`), acyclic anchors and aliases (expanded, with an expansion budget
against "billion laughs"), explicit document start (`---`), multiple documents (via the dedicated
APIs), and the JSON-representable standard tags `!!str`, `!!null`, `!!bool`, `!!int`, `!!float`,
`!!seq`, and `!!map`.

The default `YamlScalarSchema.Core` follows the YAML 1.2 core schema; see
[docs/yaml-1.2-compat-plan.md](docs/yaml-1.2-compat-plan.md) for the measured conformance against
the official yaml-test-suite.

Explicitly rejected with a descriptive error (including line/column where possible):

- Complex mapping keys (sequences/mappings used as keys)
- Duplicate YAML mapping keys, including keys that differ textually but have the same YAML tag and canonical value
- Scalar keys that collide after projection to JSON property names; duplicate JSON object property names are also rejected during serialization
- Custom tags and tags without a JSON mapping, including `!!binary`, `!!timestamp`, `!!set`, `!!omap`, `!!pairs`, and `!!merge`
- YAML 1.1 merge-key behavior. Under Core, plain `<<` is an ordinary string key; quoted or `!!str`-tagged `<<` is an ordinary string key under either schema. `!!merge` is rejected.
- Core-schema special floats `.inf`/`.nan` (no JSON number representation)
- Cyclic aliases (JSON has no cyclic graph representation)
- Multiple documents in a single `Deserialize` call (use `DeserializeFirstDocument` or
  `DeserializeDocuments`)
- Version directives other than YAML 1.2
- Limits: `MaxDepth` (64), optional post-scan `MaxScalarLength`, and per-document alias expansion budget

There is no document-count or input-byte cap. Applications handling untrusted input should bound
the input stream at the hosting layer; `MaxScalarLength` is checked after YamlDotNet has scanned and
materialized a scalar and is not a peak-memory limit.

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

UTF-8 bytes and `IBufferWriter<byte>` sinks are also supported, mirroring
`System.Text.Json`'s API shape. Deserialization from bytes decodes incrementally (no
whole-input UTF-16 string), skips a leading UTF-8 byte order mark, and rejects malformed UTF-8:

```csharp
IBufferWriter<byte> buffer = ...;
YamlSerializer.Serialize(buffer, config, ConfigJsonContext.Default.Config);

byte[] yamlBytes = Encoding.UTF8.GetBytes(yamlText);
var config = YamlSerializer.Deserialize(yamlBytes, ConfigJsonContext.Default.Config);
var config2 = await YamlSerializer.DeserializeAsync(yamlBytes, ConfigJsonContext.Default.Config, cancellationToken: ct);
```

## Known limitations

- Comments and YAML formatting (quoting styles, indentation, anchors) are not preserved on round
  trips: the JSON data model that bridges YAML to .NET objects has no slot for them, and the
  specification assigns comments no data meaning. Structural fidelity is covered by the
  conformance suite; a comment-preserving rewrite API would be a separate, non-JSON tool.
- A small number of yaml-test-suite cases deviate from the upstream YamlDotNet scanner (tab
  handling, some block scalar chomping edge cases); each is named and skipped with a reason in
  the conformance suite.
- Async stream deserialization stages the UTF-8 YAML input because YamlDotNet's parser is
  synchronous. Small converted JSON payloads use a bounded pooled-memory fast path; larger payloads
  spill through a bounded pipe to `System.Text.Json`.
- The reflection based overloads need runtime reflection.
- YamlDotNet's emitter escapes some non-ASCII scalars (for example emoji) using double quoted
  escapes; the values still round trip exactly.
- Block sequences under a mapping key are emitted at the parent indentation level
  (YamlDotNet's default), which is valid YAML but visually "indentless".

## Performance considerations

The pipeline is `object → JSON tokens (System.Text.Json) → YAML events (YamlDotNet)`. JSON is
consumed incrementally; a complete intermediate JSON document is not staged for serialization.
Stream deserialization keeps converted JSON in pooled memory up to a fixed threshold, then spills to
a bounded pipe. The async input-stream path still stages the source UTF-8 YAML because YamlDotNet's
parser is synchronous. Benchmarks (BenchmarkDotNet, `benchmarks/`) compare YamlDotNet's reflection
based serializer with this library in reflection and source generated modes:

```sh
dotnet run --project benchmarks/SVCSystems.Text.Json.Yaml.Benchmarks -c Release -- --filter '*'
```

String and byte-returning overloads necessarily aggregate the YAML result, but no longer allocate a
second, complete JSON document. The `IBufferWriter<byte>` overload writes directly to the caller's
writer; synchronous stream serialization writes bounded UTF-8 chunks, while async stream
serialization stages small results in pooled memory before using the bounded pipe for larger
outputs and asynchronous backpressure. Stream deserialization uses the same memory-first threshold
strategy for its JSON bridge. Deserialization decodes UTF-8 incrementally into characters as the
parser pulls them (no whole-input UTF-16 string on any path). The one remaining known deficit is
deserializing a single very large scalar
(scanner-internal); a raw UTF-8 front-end for the scanner is recorded as future work in
`docs/phase-7-completion-plan.md`.

### Object-engine comparison

The earlier YamlDotNet object-engine comparison used ShortRun and is not reported here as a
performance comparison. A full before/after run against YamlDotNet has not been made for this
change.

### Sync vs async stream overloads (MemoryStream, no device I/O)

The tables compare the same BenchmarkDotNet `DefaultJob` cases on clean HEAD and the current
implementation on linux-x64 / .NET 11. Values are mean time / allocated memory. The clean-HEAD
baseline was rerun in a detached worktree. That run reported a 3.30 GHz maximum CPU frequency,
while the current implementation run reported 2.52 GHz, so cross-run time changes are directional rather than a
controlled speedup measurement; allocated bytes are more comparable. Rerun both revisions under
the same CPU conditions for precise timing comparisons.

| Operation | Before | After | Time change |
|---|---:|---:|---:|
| Serialize small, sync | 17.96 µs / 4.98 KB | 17.72 µs / 5.17 KB | -1.3% |
| Serialize small, async | 18.67 µs / 4.98 KB | 17.89 µs / 5.39 KB | -4.2% |
| Serialize 64 KB scalar, sync | 3,494.27 µs / 194.25 KB | 3,018.03 µs / 194.48 KB | -13.6% |
| Serialize 64 KB scalar, async | 3,356.79 µs / 194.25 KB | 3,170.38 µs / 194.67 KB | -5.6% |
| Deserialize small, sync | 45.05 µs / 19.65 KB | 45.99 µs / 19.97 KB | +2.1% |
| Deserialize small, async | 43.30 µs / 18.63 KB | 45.65 µs / 19.04 KB | +5.4% |
| Deserialize 64 KB scalar, sync | 2,531.25 µs / 410.78 KB | 2,718.48 µs / 413.24 KB | +7.4% |
| Deserialize 64 KB scalar, async | 2,522.78 µs / 409.79 KB | 2,851.27 µs / 413.21 KB | +13.0% |

The stream benchmark uses a fresh `MemoryStream`; its backing-store growth is included, so chunked
writes can increase measured allocation compared with a single batched write. The bounded-pipe
bridges also add overhead. The full run shows a throughput and allocation cost in the stream
overloads; the 64 KB scalar case also has to retain an individual token while parsing. Async stream
deserialization still stages the UTF-8 YAML input for YamlDotNet's synchronous scanner.

### Buffer and byte API benchmarks

| Operation | Before | After | Time change |
|---|---:|---:|---:|
| Serialize small, sink | 19.54 µs / 4.91 KB | 17.27 µs / 5.10 KB | -11.6% |
| Serialize large, sink | 4,121.03 µs / 386.13 KB | 3,293.73 µs / 382.52 KB | -20.1% |
| Serialize small, string | 17.46 µs / 4.92 KB | 17.63 µs / 5.11 KB | +1.0% |
| Serialize large, string | 3,456.65 µs / 258.24 KB | 3,232.68 µs / 258.43 KB | -6.5% |
| Deserialize small, bytes | 45.81 µs / 18.37 KB | 41.69 µs / 18.37 KB | -9.0% |
| Deserialize large, bytes | 2,551.94 µs / 409.52 KB | 2,408.14 µs / 409.52 KB | -5.6% |
| Deserialize small, string | 41.10 µs / 16.24 KB | 38.86 µs / 16.24 KB | -5.5% |
| Deserialize large, string | 2,488.49 µs / 407.34 KB | 2,617.03 µs / 407.35 KB | +5.2% |

## Building from source

```sh
dotnet build SVCSystems.Text.Json.Yaml.slnx
dotnet run --project tests/SVCSystems.Text.Json.Yaml.Tests -c Debug
dotnet test  # uses the Microsoft.Testing.Platform runner (configured in global.json)
dotnet pack src/SVCSystems.Text.Json.Yaml -c Release
```

Native AOT validation:

```sh
dotnet publish tests/SVCSystems.Text.Json.Yaml.AotTests -c Release -r linux-x64 -o artifacts/aot-tests
./artifacts/aot-tests/SVCSystems.Text.Json.Yaml.AotTests
```

## Contributing

This repository uses **[Conventional Commits](https://www.conventionalcommits.org/)**:
[semantic-release](https://semantic-release.gitbook.io/semantic-release/) derives the package
version and changelog from commit messages, and pull request titles are linted against the
convention. Use `feat:`, `fix:`, `docs:`, `chore:` and similar prefixes.

## License

MIT. Portions adapted from [YamlDotNet.System.Text.Json](https://github.com/IvanJosipovic/YamlDotNet.System.Text.Json).

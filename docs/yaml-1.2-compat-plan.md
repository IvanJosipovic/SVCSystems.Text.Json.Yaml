# YAML 1.2.2 JSON projection profile

This library targets YAML 1.2.2 semantics for values that can be represented by the
`System.Text.Json` data model. It is not a general YAML object constructor and does not claim
lossless support for every YAML representation graph.

`System.Text.Json` remains the .NET object-mapping engine; YamlDotNet parses and emits YAML; the
library bridges their token/event streams. See [the review remediation plan](review-remediation-plan.md)
for implementation rationale and verification detail.

## Scalar schemas

`YamlScalarSchema.Core` is the default YAML 1.2 Core schema:

- Null: empty, `null` (any capitalization), and `~`.
- Boolean: `true`/`false` in supported capitalization variants.
- Integer: signed decimal, hexadecimal (`0x`), and octal (`0o`); leading-zero decimal values are
  decimal, not YAML 1.1 octal.
- Float: Core decimal forms, including `.5` and `5.`. Core special floats have no JSON number
  representation and are rejected.
- Other plain scalars are strings.

`YamlScalarSchema.Json` follows YAML 1.2.2 section 10.2:

- Plain `null`, `true`, and `false` resolve to their corresponding JSON values.
- Integers and floats follow the JSON-schema patterns. The YAML float pattern accepts forms such
  as `5.` and `1e3`; `.5`, `+42`, `007`, `0x1A`, and `True` do not match.
- A plain scalar that matches no JSON-schema rule is an error, not a string. Use quotes or
  `!!str` for strings. The non-specific `!` tag resolves a scalar to a string.

Quoted and block scalars resolve as strings unless an explicit supported standard tag changes the
type. Explicit tags are validated against their scalar content and do not depend on presentation
style. Numeric tokens are normalized to valid JSON syntax without `decimal`/`double` round-trip
loss; integer precision is preserved.

## JSON-compatible projection

Supported tags are `!!str`, `!!null`, `!!bool`, `!!int`, `!!float`, `!!seq`, and `!!map`, applied
to the matching node kind. Custom tags and tags without a JSON mapping are rejected, including
`!!binary`, `!!timestamp`, `!!set`, `!!omap`, `!!pairs`, and `!!merge`.

YAML mappings require unique keys. This bridge accepts scalar keys, compares them by resolved tag
and canonical scalar value, then projects them to JSON property names. Duplicate YAML keys and
collisions in projected JSON property names are rejected. Complex keys are rejected because JSON
objects require string names. JSON objects with duplicate property names are also rejected during
serialization.

YAML 1.2.2 does not define the YAML 1.1 merge-key extension. No merge behavior is implemented.
Under Core, plain `<<` is an ordinary string key. Under either schema, quoted or explicitly
`!!str`-tagged `<<` is a string key. Under Json, plain `<<` is an unmatched scalar and therefore
an error. Explicit `!!merge` is rejected.

Anchors and aliases are expanded under a per-document event budget. Shared acyclic nodes become
repeated equal JSON values; anchor identity is not preserved. Cyclic aliases are rejected because
JSON cannot represent cyclic graphs. Multi-document APIs have no document-count cap. A declared
YAML version other than 1.2 is rejected; `%TAG` directives are resolved by the YAML parser before
tag validation.

## Encoding and resource boundaries

Byte and stream inputs are strict UTF-8; malformed input is rejected rather than replaced. A
leading UTF-8 BOM is accepted. `MaxDepth` limits nesting. `MaxAliasExpansionEvents` limits replayed
alias events per document. `MaxScalarLength` is an optional post-scan acceptance limit and does
not bound scanner peak memory.

The library has no input-byte or document-count cap. Hosts processing untrusted data should bound
the input before passing it to the serializer. Current stream APIs stage complete representations
at some points; they are not advertised as end-to-end streaming.

## Conformance and limitations

The test project runs data-driven cases from the vendored yaml-test-suite and unit tests for the
JSON bridge. Documented policy rejections (such as complex keys, unsupported tags, duplicate keys,
and Core special floats) are classified separately from unexpected failures. YamlDotNet scanner
deviations are tracked in the conformance tests.

Comments, scalar styles, anchor names, mapping order as semantic information, and byte-identical
formatting are not preserved through the JSON bridge. YAML features that require arbitrary tags,
complex keys, or cyclic/shared graph identity are intentionally outside this projection.

Run the checks with:

```sh
dotnet test tests/SVCSystems.Text.Json.Yaml.Tests/SVCSystems.Text.Json.Yaml.Tests.csproj -c Release
dotnet publish tests/SVCSystems.Text.Json.Yaml.AotTests -c Release -r linux-x64
```

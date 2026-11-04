# Roadmap: Full YAML 1.2.2 Compatibility

Target: [YAML 1.2 revision 1.2.2](https://yaml.org/spec/1.2.2/) (published 2021-10-01; the latest
specification, with no normative changes from 1.2.1). This document plans the path from the
current JSON-compatible subset to full YAML 1.2.2 support while preserving the architecture
established in the implementation plan:

1. `System.Text.Json` remains the only .NET object mapping engine.
2. YamlDotNet remains the parse/emit engine; the library bridges JSON tokens and YAML events.
3. Nothing is rejected that can be represented losslessly in the JSON data model; anything that
   cannot be represented is handled through an explicit, documented policy instead of silent
   reinterpretation.
4. New behavior is opt-in or strictly additive; no breaking changes to the existing API.

## Current state vs the spec

Already supported: block/flow collections, plain/quoted/block scalars, all scalar styles the
emitter produces, comments are skipped per spec semantics (they have no data effect), the
standard `!!str/!!int/!!float/!!bool/!!null` tags, `%TAG`/`%YAML` directive resolution (the
YamlDotNet parser handles this and rejects versions above 1.2), explicit `? key : value` syntax,
duplicate keys (strict), document start markers, empty documents.

Explicitly rejected today (the gap): anchors/aliases, merge keys (`<<`), multiple documents per
call, custom/non-standard tags, complex mapping keys, non-JSON scalars (`.inf`, `.nan`, core
schema integers such as `0x1A`/`0o17`), and any comment or formatting preservation.

## Compatibility tiers

Full spec compatibility is decomposed into three independently verifiable tiers.

### Tier 1 — Structural acceptance

*Every* valid YAML 1.2.2 document parses into a JSON-shaped data graph. This tier covers the
syntactic machinery of the spec: streams, documents, anchors/aliases, merge keys, node
properties (anchors/tags in any order), flow and block contexts, block scalars with indentation
and chomping indicators, explicit keys, single-pair mappings in sequences, empty documents, and
directives. Nothing in this tier needs a type system beyond what JSON already has: aliases
expand to shared content, merge keys splice maps, every collection is a JSON object or array.

### Tier 2 — Semantic mapping

Every resolved scalar lands in a documented type. This tier covers scalar resolution schemas,
the non-JSON scalars (`.inf`, `.nan`), core-schema number forms (`0x1A`, `0o17`), YAML 1.1
compatibility scalars (`yes`/`on`), tag machinery beyond the standard five tags (`!!binary`,
`!!timestamp`, `!!set`, `!!omap`, `!!pairs`, unknown/custom tags), non-string mapping key
canonicalization, and duplicate key policy (already implemented).

### Tier 3 — Fidelity

Round-trip preservation of information that has no JSON data-model meaning: comments, scalar
styles, indentation, line folding, document markers. The data model of `System.Text.Json` has no
slots for these, so Tier 3 is a separate *mode* (`PreserveComments`, style preferences), not a
default. Full source-fidelity round trip (byte-identical formatting) is explicitly out of scope —
that would require a text-level editing model, not a serializer.

## Phases

### Phase 0 — Conformance harness (prerequisite for everything else)

Port the official [yaml-test-suite](https://github.com/yaml/yaml-test-suite) as data-driven xUnit
tests: each test case is a YAML input plus expected events/JSON or an expected error. Build two
runners:

1. *Event conformance*: compare our YAML→JSON bridge output against the case's expected JSON
   (using the spec's JSON schema in appendix), tolerating documented policy deviations.
2. *Round-trip conformance*: `YAML → JSON → YAML` must preserve the data model (types, strings,
   numbers).

Add a CI job that reports the conformance percentage per tier so every later phase is measurable.
Current strict-rejection behaviors map to test-suite `error`/skip classifications and are
documented in a compatibility matrix in the README.

### Phase 1 — Streams and multiple documents

* **API**: `YamlSerializer.DeserializeFirstDocument<T>(...)`, `DeserializeDocuments<T>(...)`
  returning materialized documents, and `SerializeDocuments<T>(...)` for multi-document output.
  Non-generic `JsonTypeInfo` based variants included for AOT.
* **Semantics**: one JSON value per document; a document containing only comments or an explicit
  end marker maps to JSON `null` (consistent with the current empty-document rule). `%TAG` and
  `%YAML` directives already resolve through the parser and are validated per document.
* **AOT**: `List<T>`/array materialization uses the source generated context's collection
  metadata; no reflection.
* Acceptance: test-suite multi-document cases; mixed explicit/implicit markers; directive scoping
  across documents.

### Phase 2 — Anchors, aliases, and merge keys

* **Anchors/aliases**: maintain an anchor registry while streaming. When a node carries an
  anchor, buffer its events (nested anchors inside the buffer are registered as they replay). An
  `AnchorAlias` replays the buffered events into the JSON writer through the same conversion
  path. Aliases can only reference earlier nodes, so this is acyclic by construction.
* **Expansion budget**: exponential alias fan-out ("billion laughs") is a real attack; cap total
  events materialized per document (`MaxAliasExpansionEvents`, default ~1M) and document the
  limit. A budget exception is a `YamlException` with position, not an OOM.
* **Merge keys** (`<<`): when a mapping contains a `<<` key whose value is a mapping, alias to a
  mapping, or a sequence of these, splice the source mapping(s) into the current mapping before
  emitting. Per spec, explicit keys in the current mapping win and the first source in the
  sequence wins among sources. Implementation: buffer each mapping's entries, then emit
  key-by-key, skipping keys already emitted (the existing per-level key set already provides
  this). `AllowMergeKeys` option (default `true` once implemented — it is lossless).
* **Anchor registry memory**: buffered anchored nodes are linear in input size; bound total
  buffered bytes with the document limits from Phase 5.
* Acceptance: test-suite anchors/aliases/merge cases; expansion-budget tests; nested anchor
  replay tests; `!!merge` tagged merge keys.

### Phase 3 — Scalar schema completion

* **Schema selection**: extend `YamlScalarSchema` with `Core` (YAML 1.2 core schema: `0x`/`0o`
  integers, strict booleans, `y`/`n` are strings) and keep `Json` (current, JSON-compatible
  resolution) as default. A `Version11` schema (yes/no/on/off booleans, sexagesimal) is optional
  and explicitly documented as lossy for `System.Text.Json` consumers.
* **Non-JSON floats**: add `SpecialFloatBehavior { Reject (default), String }`. With `String`,
  `.inf`/`-.inf`/`.nan` resolve to the strings `".inf"`/`"-.inf"`/`".nan"` (no silent value
  change; the JSON data model has no infinity). Emitting is unaffected: JSON never produces these
  values unless `JsonNumberHandling.AllowNamedFloatingPointLiterals` was used, in which case they
  arrive as strings and stay strings.
* **Number fidelity**: `0x1A`/`0o17` under `Core` become integers (hex/octal forms are 1.2 core
  schema integers). Under `Json` they remain strings (current behavior).
* **Emission quality**: control the emitter's non-ASCII escaping (the current `"\U0001F389"`
  behavior) — evaluate `EmitterSettings.WithUtf16SurrogatePairs` and, if insufficient, an
  internal emitter patch or contributed fix; expose `Width`, `NewLine`, `MaxSimpleKeyLength`,
  `IndentedSequences` in `YamlSerializerOptions`; add a `StringStyle { Auto, Literal, DoubleQuoted }`
  emission preference.
* Acceptance: test-suite scalar resolution suites for each schema; property-based round-trip
  fuzzing (CsCheck) over strings including control characters, U+0085, U+2028/U+2029, unpaired
  and paired surrogates, and long lines.

### Phase 4 — Tags and directives

* **Standard tags beyond the JSON set** with documented policies (default: reject; per-tag
  opt-in):
  - `!!binary` → base64 string (`AllowBinaryTag`), or reject (default).
  - `!!timestamp` → ISO-8601 string (`AllowTimestampTag`), or reject (default). It is already a
    string under `Json` when untagged; the tag form only differs in validation.
  - `!!set` → object with `null` values; `!!omap`/`!!pairs` → array of `{k, v}` pairs (lossy,
    documented) or reject (default).
  - `!!merge` behaves as the merge key semantics of Phase 2.
* **Unknown/custom tags**: `UnknownTagBehavior { Reject (default), Ignore }`. With `Ignore`, an
  unrecognized tag is discarded and the node resolves as if untagged — the pragmatic behavior for
  ecosystem documents (e.g. Ansible `!vault`). This never constructs .NET types from tags
  (security requirement stands); it only strips the tag.
* **Non-specific tags**: `!` already resolves to an untyped node (the parser surfaces it as
  non-specific).
* Acceptance: test-suite tag tests; directive error cases (undefined tag handle, duplicate
  directives, `%YAML 1.3` rejection).

### Phase 5 — Limits and hardening generalization

Replace hardcoded/implicit limits with validated options on `YamlSerializerOptions`:

| Option | Default | Rationale |
|---|---|---|
| `MaxDepth` | 64 | aligns reader/writer/STJ; makes the current STJ failure explicit at the YAML layer |
| `MaxInputLength` | unlimited (opt-in) | untrusted input size guard |
| `MaxScalarLength` | unlimited (opt-in) | memory guard |
| `MaxAliasExpansionEvents` | 1,000,000 | billion-laughs guard (Phase 2) |
| `MaxDocuments` | 16 | multi-document guard (Phase 1) |

Add differential fuzzing of the YAML→JSON bridge (SharpFuzz) in CI, and propagate
line/column into every converter error (already the case; keep covered by tests).

### Phase 6 — Fidelity mode (Tier 3)

* **Comments**: verified — YamlDotNet 18.1.0's *emitter* accepts `Comment` events, but its
  *parser* dropped comment events in v16+; upstream master has re-added parser comment support
  (unreleased as of YamlDotNet 18.1.0). Plan: track the upstream release; until then, capture
  comments with a thin custom `IScanner` wrapper feeding the parser (comments have no grammar
  effect, so a wrapper is safe). Thread a comment-annotated event stream through the bridge and
  emit it with the existing emitter.
* **Scope of fidelity**: structural + comments are preserved; input scalar styles, indentation
  and line folding of the *input* are re-canonicalized (emitter-decided) — documented as
  lossy-by-design. `PreserveComments` option defaults to `false`.
* Acceptance: round-trip tests asserting comment placement (before/after values, trailing
  comments on their own line), plus the existing data-model round-trip suite.

### Phase 7 — Streaming, UTF-8 path, and re-benchmarking

* Stream/`IBufferWriter<byte>` overloads; UTF-8 end-to-end path avoiding intermediate UTF-16
  strings (`StreamWriter` for the emitter, `StreamReader` for the parser — both already accept
  `TextReader/TextWriter`); async variants with cancellation.
* Re-run the BenchmarkDotNet suite per phase; record deltas. The 64 KB large-scalar case is the
  regression sentinel for buffering changes.

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| YamlDotNet parser gaps (comments, future regressions) | vendor-able bridge: our converters only use `IParser`/`IEmitter`; a thin custom parser/scanner wrapper or a patched YamlDotNet can be swapped in without public API changes |
| Special floats have no JSON representation | explicit `SpecialFloatBehavior` policy; never silently reinterpret |
| Alias expansion memory | event budget + anchored-node byte budget; fuzz in CI |
| Schema divergence costs double maintenance | resolver implementations are pure functions over scalar text; test-suite data drives all variants |
| Multi-document + AOT | collection materialization through `JsonTypeInfo` only; AOT suite covers the new APIs |
| `System.Text.Json` depth/limits | surface as YAML-layer `MaxDepth` errors with position, instead of deep `JsonException`s |

## Non-goals (unchanged)

No forking `System.Text.Json`; no second source generator; no arbitrary .NET type construction
from YAML tags (including `!!python/object`-style tags); no byte-identical formatting round trip;
no YAML 1.1 semantics by default.

## Sequencing

Phase 0 is mandatory first — every other phase defines its acceptance criteria against the
conformance harness. Recommended order after that: 1 → 2 → 5 → 3 → 4 → 7 → 6 (hardening lands
before the features that increase attack surface: aliases). Phases 3 and 4 are independent and
can proceed in parallel after Phase 0. Tier 3 (Phase 6) is scheduled last because it is the only
tier that may require changes outside our control (upstream YamlDotNet parser work).

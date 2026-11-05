# YAML ↔ System.Text.Json Review Remediation Plan

## Purpose

This plan turns the deep review of the current working tree into an ordered implementation and verification program. The goal is to make the library’s behavior predictable, secure for bounded untrusted inputs, faithful to its documented YAML profile, and measurably efficient—while retaining `System.Text.Json` as the .NET object mapping engine.

This is a plan, not a claim that the library will implement every YAML feature. YAML can represent arbitrary tagged graphs, non-scalar mapping keys, and cycles; JSON’s data model cannot represent all of those losslessly. The supported profile and its deliberate losses must be explicit.

## Baseline

- Library targets `net10.0` and `net11.0`; the current test project targets `net11.0`.
- The architecture stages JSON between `System.Text.Json` and YamlDotNet events. Several public stream APIs also stage the complete input or output.
- The Release test run observed during review completed with **890 passed, 170 skipped, 0 failed**. This aggregate includes the unit suite and both data-driven conformance theories; it is not itself an official YAML-suite pass percentage.
- The current working tree already contains staged changes. Apply alpha API removals deliberately, but do not discard or rewrite unrelated work.

## Goals and non-goals

### Goals

1. Prevent silent data changes for numbers, strings, encodings, tags, mapping keys, and aliases.
2. Make every limit’s scope and enforcement point match its public documentation.
3. Implement YAML 1.2.2 semantics for the JSON-representable profile and document the features that JSON cannot represent.
4. Improve conformance tests so incorrect results cannot be accepted through floating-point comparison or broad skips.
5. Establish reproducible performance measurements and reduce allocations where measurements justify it.

### Non-goals

- Arbitrary .NET type construction from YAML tags.
- Lossless representation of arbitrary YAML graphs through JSON.
- Comment, style, anchor-name, indentation, or byte-identical formatting preservation.
- A custom YAML scanner unless measurements show YamlDotNet is the dominant, actionable bottleneck.
- Replacing `System.Text.Json` converters, metadata, polymorphism, or source-generated contracts with a second object-mapping system.

## Alpha policy decisions

The package is alpha. There is no compatibility mode, obsolete alias, or legacy option retained solely to preserve the current behavior. Prefer the smallest API that accurately exposes YAML 1.2.2 behavior and its JSON projection limits. Make the following semantics fixed rather than configurable where YAML has one correct answer.

### 1. YAML version and schemas

- Target YAML 1.2.2. Accept supported YAML 1.2 version directives and tag directives; reject unsupported declared versions with a clear error rather than silently applying YAML 1.2 rules to them.
- Keep `Core` as the default schema and make `Json` implement the actual YAML 1.2.2 JSON schema, including its strict null, boolean, and numeric resolution rules. Plain scalars that match no JSON-schema rule are errors; quoted or `!!str`-tagged scalars provide strings.
- Do not retain the current “Core except hexadecimal/octal” behavior under a compatibility name.
- Under `Core`, reject `.inf`, `.nan`, and their signed/case variants because they resolve to YAML floats with no JSON number equivalent. Under `Json`, unmatched forms (including special floats) are schema-resolution errors. Remove `SpecialFloatHandling`; do not add a lossy policy switch.

### 2. Tags and merge keys

- Support only tags whose value semantics can be represented by the JSON bridge (`str`, `int`, `float`, `bool`, `null`, `seq`, and `map`) and validate tags on every node kind.
- Reject unsupported standard tags and custom tags. Remove `UnknownTagHandling`, `AllowBinaryTag`, and `AllowTimestampTag`; do not silently strip tags or construct CLR types from them.
- YAML 1.2.2 does not define the YAML 1.1 merge-key extension. Do not implement merge semantics, including for an explicit `!!merge` tag. Reject that unsupported tag. Under Core, plain `<<` is an ordinary string key; quoted or explicitly `!!str`-tagged `<<` is a string key under either schema.
- Remove `AllowMergeKeys` and the event-buffering merge implementation rather than adding a legacy mode.

### 3. Mapping keys and duplicate semantics

- Reject duplicate YAML mapping keys unconditionally. YAML mappings require unique keys; remove `AllowDuplicateKeys` and do not replace it with `AllowDuplicateProperties`.
- For the JSON projection, accept scalar mapping keys only. Preserve string key content; represent supported non-string scalar keys using their schema/tag canonical text as JSON property names. Detect duplicate YAML keys using tag plus canonical value **before** projecting them to JSON, and reject any collision in projected JSON property names.
- Reject complex mapping keys because JSON objects cannot represent them. Document that scalar-key type identity is necessarily lost when projected to a JSON property name.
- When serializing JSON objects, reject duplicate property names rather than emitting a YAML mapping whose keys are not unique.

### 4. Encoding, options, and resource boundaries

- Reject malformed UTF-8 consistently for all byte and stream APIs. Do not expose a replacement-fallback option.
- Keep only YAML options with a clear YAML formatting, schema, or parser-safety purpose. Alpha permits removing/renaming current options directly; do not add compatibility aliases.
- Keep a bounded alias-expansion budget per document and a nesting limit. Remove the `MaxDocuments` default cap; document streams of arbitrary length are valid, and consumers that accept untrusted streams must bound input at the hosting layer.
- Keep `MaxScalarLength` as an optional post-scan acceptance limit; document that it does not bound peak scanner memory and is not an input-size security boundary.
- `MaxInputBytes` remains a hosting-layer responsibility for parity with the surrounding `System.Text.Json` input APIs. Document this explicitly and show callers how to wrap/bound untrusted streams.
- Keep `NewLine` validation aligned with `System.Text.Json` (`"\n"` or `"\r\n"` only, default `Environment.NewLine`). Preserve useful emitter settings such as `Width` and `MaxSimpleKeyLength`, and document deliberate differences from YamlDotNet defaults.

## Prioritized work

### Phase 0 — Lock regression cases and establish a clean baseline

**Purpose:** ensure each observed issue has a failing test before the fix.

Add targeted tests for:

- YAML floats that underflow, overflow `double`, and exceed decimal precision.
- A supplementary UTF-8 character at each edge around `SequenceTextReader`’s 1,024-character buffer.
- Supported standard tags on scalars and collections; unsupported standard and custom tags always reject. Cover non-specific `!` resolution and `%TAG` directives.
- Under Core, plain `<<` is an ordinary string key. Quoted `"<<"`/`'<<'` and `!!str <<` are string keys under either schema. An explicit `!!merge` tag rejects as unsupported; it does not trigger merge behavior.
- CR, LF, and CRLF string round-trips for default, literal, and double-quoted output styles.
- Streams with many documents (e.g. 100) deserialize completely — document count is not capped by YAML options.
- Signed/case variants of `.nan` and `.inf` under both schemas: Core rejects their non-JSON float values; Json rejects them as unmatched plain scalars.
- Invalid UTF-8 throws on every byte path: `ReadOnlyMemory<byte>`, `ReadOnlySequence<byte>`, synchronous streams, and asynchronous streams.
- Explicitly tagged quoted scalars, including valid and invalid values.
- Alias expansion limits independently in multiple documents.
- Duplicate mapping keys always reject, including keys with different spellings but equal supported YAML tag/canonical value.
- Scalar-key projection to JSON property names rejects collisions; complex mapping keys reject.
- JSON objects with duplicate property names reject during YAML serialization.
- Cyclic aliases reject with an explicit “cannot be represented by JSON” error; acyclic aliases project to equal JSON values.

**Gate:** tests demonstrate each regression on the current implementation and pass only once the corresponding phase is complete. Preserve the existing test corpus and add new cases rather than replacing older tests.

### Phase 1 — Correct scalar conversion and string fidelity

#### 1.1 Preserve YAML numeric values

Review `YamlToJsonConverter.WriteFloat` and `JsonScalarResolver.TryParseNumber`.

- Avoid routing arbitrary YAML number tokens through `decimal`/`double` merely to write JSON.
- Validate against the selected schema, then write a valid JSON number token using `Utf8JsonWriter.WriteRawValue` where possible.
- Normalize resolved numeric tokens to valid JSON syntax without routing them through `decimal`/`double`; for example Core `.5` → `0.5`, `5.` → `5.0`, and signed/leading-zero integers. Under Json, follow YAML 1.2.2's integer/float patterns (`5.` is a float, `.5` is not); unmatched plain scalars are errors, not strings.
- Convert Core hexadecimal/octal integers exactly; retain exact arbitrary-precision integer behavior.
- Do not resolve JSON-schema scalars using Core-only forms.
- Reject Core-resolved special-float values because JSON has no equivalent, including signed and case variants of NaN and infinity.
- Ensure a valid YAML number never results in a missing JSON token, an accidental zero, or a .NET infinity exception.

**Acceptance:** `JsonElement.GetRawText()` preserves the exact mathematical value for large and high-precision inputs. Where lexical form is deliberately normalized, tests assert numeric equivalence without a binary floating-point comparison.

#### 1.2 Honor explicit tags independently of style

Refactor scalar resolution so an explicit supported tag controls resolution regardless of plain, single-quoted, double-quoted, or block style. Style is presentation, not the scalar’s resolved type.

- Accept valid forms such as `!!int "3"` and `!!bool 'true'`.
- Validate the tagged scalar against the selected tag’s supported content forms.
- Resolve explicit standard tags according to their tag semantics and YAML 1.2.2 rules, independent of style. Test valid quoted forms and reject invalid content with a positioned YAML exception.

#### 1.3 Preserve newline characters in emitted strings

Update `JsonToYamlConverter.WriteScalar` so strings containing carriage returns are not emitted as literal block scalars that YAML normalizes.

- Prefer escaped double-quoted scalars for strings containing `\r`.
- Retain literal-block output for safe LF-only multiline strings when it round-trips exactly.
- Test final-newline/chomping behavior and strings containing CRLF, standalone CR, multiple trailing LF, tabs, and control characters.

**Gate:** serialization followed by deserialization returns the exact original string for valid Unicode scalar values and newline patterns. Validate invalid .NET surrogate handling against YAML’s character rules; reject invalid Unicode rather than silently replacing it.

Also test JSON-to-YAML depth at the configured `JsonSerializerOptions.MaxDepth`. The JSON token reader must not silently impose a lower unrelated default; either pass the configured limit through or enforce one documented shared limit without risking unbounded recursion.

### Phase 2 — Repair UTF-8 input and encoding policy

#### 2.1 Fix incremental decoder boundary handling

Refactor `SequenceTextReader.FillBuffer` so a zero-progress decoder result is not automatically treated as an incomplete rune.

- Distinguish destination exhaustion from incomplete/malformed input.
- Preserve unconsumed input bytes across refills.
- Do not slice away an entire sequence segment unless all its bytes were consumed or are deliberately buffered.
- Ensure a supplementary code point can be decoded when only one UTF-16 slot remains, either by reserving two slots or by retaining the bytes until a suitable refill.
- Keep correct handling of UTF-8 code points split across `ReadOnlySequence<byte>` segments and a leading UTF-8 BOM.

#### 2.2 Enforce strict malformed-UTF-8 rejection consistently

Use strict decoding for all byte and stream paths. Decoder fallback must throw a clear YAML/input exception and must not emit U+FFFD as a substitute for malformed bytes. Do not add a replacement compatibility mode.

Test UTF-8 validity and consistency across:

- `ReadOnlyMemory<byte>` and `ReadOnlySpan<byte>`
- Segmented `ReadOnlySequence<byte>` with invalid bytes at segment boundaries
- `Stream` and `DeserializeAsync(Stream)`
- BOM and no-BOM inputs

**Gate:** valid inputs produce the same result on every byte path; malformed UTF-8 behavior is intentional, consistent, and does not depend on segmentation.

### Phase 3 — Enforce node tags and define YAML projection policies

#### 3.1 Validate tags on every YAML node kind

Inspect `SequenceStart`, `MappingStart`, and `Scalar` tags before converting their content.

- Accept only tags that map to the JSON data model (`str`, `int`, `float`, `bool`, `null`, `seq`, and `map`), with tag-kind and scalar-content validation.
- Reject custom tags and standard tags without a lossless JSON mapping (including `binary`, `timestamp`, `set`, `omap`, `pairs`, and `merge`).
- Remove `UnknownTagHandling`, `AllowBinaryTag`, and `AllowTimestampTag`; there is no ignore/passthrough mode.
- Resolve the YAML non-specific tag `!` according to YAML 1.2.2 rather than treating it as an unknown custom tag.
- Keep the security invariant: no YAML tag causes arbitrary CLR type construction.

#### 3.2 Define explicit tag and directive scope

Test `%YAML` and `%TAG` directives across multiple documents. Accept YAML 1.2 directives and reject unsupported declared versions with a clear error. Resolve tag handles before applying the supported/unsupported tag policy.

#### 3.3 Implement scalar key projection and duplicate semantics

Implement the fixed scalar-key projection policy in the decision section. Include canonical-equivalence cases such as decimal/radix representations and explicit versus implicit scalar tags.

- Detect duplicate YAML mapping keys unconditionally using tag and canonical content, not merely original key text.
- Project supported scalar keys to canonical JSON property-name text and reject collisions after projection.
- Remove `AllowDuplicateKeys`; do not preserve duplicate JSON members as a YAML mapping.
- Reject duplicate JSON property names on JSON-to-YAML serialization so output always represents a valid YAML mapping.

**Gate:** unsupported tags never disappear silently; mapping uniqueness is enforced before and after JSON projection; key behavior is tested with `JsonElement`, POCOs, and custom converters where applicable.

### Phase 4 — Remove non-YAML-1.2 merge behavior and define graph projection

The current text-based merge implementation is a YAML 1.1-era extension, not a YAML 1.2.2 feature. Remove it instead of implementing or exposing an opt-in compatibility mode.

1. Delete merge-key interpretation and `AllowMergeKeys` from the converter, options, cloning, tests, and docs.
2. Treat plain and quoted `<<` as an ordinary string property name under the selected schema.
3. Reject explicit `!!merge` as an unsupported tag; do not recognize it as a YAML 1.2.2 standard tag.
4. Keep normal alias expansion for acyclic graphs, subject to the per-document expansion budget.
5. Detect a reference to an anchor that is still being composed as a cycle, and reject it specifically because JSON cannot represent cyclic graphs. Do not report it as an undefined forward alias.
6. Document that alias identity/sharing is not preserved: a shared acyclic YAML node becomes repeated equal JSON values.

**Acceptance:** no input form triggers merge semantics; acyclic aliases preserve value semantics; cycles fail clearly and within bounded work.

### Phase 5 — Resource hardening and precise limit semantics

#### 5.1 Fix current limit behavior

- Remove `MaxDocuments` and its current off-by-one behavior. Multi-document streams have no library-level document-count cap.
- Reset alias replay accounting at each document boundary; the option and documentation define the limit per document.
- Check the remaining alias budget before enqueuing a full replay buffer, not only while dequeuing events.
- Keep nesting checks positioned and consistent across alias expansion and regular parsing.

#### 5.2 Keep input-size limits at the hosting boundary

Do not add a `MaxInputBytes`/`MaxInputLength` option to this serializer. This keeps input sizing consistent with the `System.Text.Json` APIs it delegates to and avoids another YAML-specific policy knob. Document that callers processing untrusted YAML must bound byte, string, and stream input before passing it to the library; examples should show bounded reads.

Keep `MaxScalarLength` only as a post-scan acceptance limit if useful; explicitly state that it does not prevent YamlDotNet from materializing the scalar before the check. The host-level input bound is the memory-exhaustion control.

#### 5.3 Avoid adding overlapping limits

Do not add output-byte and event-count knobs speculatively. Keep the small set of limits that have a clear threat model: nesting depth and per-document alias expansion. Measure before considering another limit.

#### 5.4 Pool hygiene

Review pooled buffer return behavior. Do not add a secure-clear option without a clear use case; measure the cost of clearing if the library decides to clear pooled data. Document the pool behavior and ensure written slices are not exposed after disposal.

**Gate:** depth and alias limits are enforced before avoidable expansion; boundary tests cover exact limit and one over; host-bounding guidance is explicit; cancellation and exception paths return pooled buffers.

### Phase 6 — Conformance harness and fuzzing

#### 6.1 Make suite results trustworthy

- Replace `JsonDeepEquals`’s `double`-first comparison with exact numeric comparison. Large integer tokens must never compare equal merely because their `double` representations round to the same value.
- Avoid blanket-skipping every `JsonException`. Distinguish expected YAML policy rejections, expected suite errors, JSON-bridge defects, and target-type mapping failures.
- Keep explicit skip classifications, but emit a summary by category and case ID.
- Separate official-suite case totals from byte-path parity totals. A byte/string path agreement test where both reject is not a successful parse-conformance result.
- Pin and record the yaml-test-suite revision and the exact number of included cases.
- Add CI thresholds for unexpected skips/failures, not just a green test process.

#### 6.2 Expand differential and property-based coverage

Add fuzz/property tests for:

- Scalar text, tags, line endings, control characters, escapes, and numeric boundaries.
- Chunk/segment positions for UTF-8 sequences.
- Nested collections, aliases/cycles, scalar mapping keys, and document boundaries.
- Round-trips through `JsonElement`, common POCOs, dictionaries, custom converters, and source-generated contracts.

Run fuzzing with bounded input sizes and persist minimized regressions as ordinary tests.

#### 6.3 Make profile expectations explicit

For every known unsupported feature, classify it as one of:

- YAML syntax rejection (parser defect or unsupported syntax)
- Valid YAML feature that cannot be projected losslessly to JSON and is rejected with a clear error
- Upstream YamlDotNet deviation

Do not label a documented semantic limitation as full YAML 1.2 conformance.

### Phase 7 — Performance measurement and targeted optimization

#### 7.1 Establish fair benchmarks first

Use longer BenchmarkDotNet jobs than `ShortRun` for decisions. Cache string and byte inputs in setup so string-input decoding is not charged only to one side. Separate:

- API cost from output-storage cost (returned string vs caller-owned byte buffer)
- Sync stream from async stream
- Reflection options from source-generated `JsonTypeInfo`
- Small structured documents, large collections, large scalars, deep documents, and alias-heavy documents
- Throughput, allocated bytes, peak memory, and Gen2/LOH behavior

Record runtime/SDK, OS/architecture, YamlDotNet version, job settings, and fixture sizes with every report.

#### 7.2 Take low-risk allocation wins

- Decode contiguous JSON numeric `ValueSpan` directly with the span overload rather than allocating `ToArray()` for each numeric token.
- Measure allocations in alias event buffering and per-mapping key tracking; avoid list slicing/copying where it materially affects realistic inputs.
- Measure the initial pooled buffer size and growth behavior against real fixture distributions.

#### 7.3 Decide whether to add bounded streaming modes

Current stream APIs stage whole buffers; document them as buffered. Prefer simple low-allocation improvements to a new concurrent pipeline. Only if representative benchmarks show unacceptable large-document peak memory, prototype a measured alternative:

- For serialization, explore a stateful JSON-byte/token adapter that converts completed JSON tokens to YAML while `System.Text.Json` writes, or a bounded producer/consumer pipeline.
- For asynchronous deserialization, explore feeding generated JSON through a bounded pipe to `JsonSerializer.DeserializeAsync` while YAML events are converted.
- Preserve `JsonTypeInfo<T>`, custom converters, and AOT behavior in any prototype.
- The package is alpha: make any necessary API changes directly and do not add compatibility overloads or modes. Preserve STJ contracts, cancellation behavior, and AOT support.

Do not call a path “zero-copy” merely because it uses pooled buffers. State precisely which intermediate strings/arrays/buffers are avoided and which complete representations remain staged.

**Gate:** adopt a performance optimization only if it improves a published target scenario without regressing correctness, AOT, or typical small-document performance beyond an agreed threshold.

### Phase 8 — Documentation and release gates

Update documentation only after behavior and measurements stabilize:

- Reconcile `README.md`, `docs/yaml-1.2-compat-plan.md`, and `docs/phase-7-completion-plan.md` with the current implementation.
- State the actual YAML 1.2.2 supported profile, schema names, rejection of merge/custom/non-JSON tags, mapping-key projection, duplicate behavior, strict encoding policy, and graph limitations.
- Explain resource-limit defaults and whether limits are per document or per call.
- Describe stream APIs as buffered or streaming accurately, including async input staging and intermediate JSON.
- Replace stale test/benchmark totals and identify skipped conformance cases by category.
- Document that configured `System.Text.Json` converters and polymorphism remain application code and should be reviewed as part of the consuming application’s security boundary.

Release validation:

1. Build both library target frameworks with warnings as errors.
2. Run unit and conformance suites on the supported runtime matrix.
3. Run the Native AOT publish-and-execute suite with reflection disabled.
4. Run UTF-8, resource-limit, cancellation, and malformed-input tests on all applicable overloads.
5. Publish benchmark reports from the current code and compare against the established baseline.
6. Review the final alpha API surface; remove obsolete options without compatibility aliases and document the resulting behavior.

## Suggested issue breakdown

For reviewable, independently testable changes, split implementation into issues or commits approximately as follows:

1. `fix: preserve YAML float values in JSON tokens`
2. `fix: handle UTF-8 supplementary characters across decoder refills`
3. `fix: preserve CR characters in emitted YAML strings`
4. `fix: validate YAML tags on all node kinds`
5. `fix: implement explicit tag semantics independent of scalar style`
6. `fix: enforce YAML mapping key uniqueness and JSON key projection`
7. `refactor: remove YAML merge-key extension and related options`
8. `fix: distinguish cyclic aliases from undefined aliases`
9. `refactor: remove non-YAML-1.2 and duplicate-key policy options`
10. `test: make YAML suite numeric comparison exact and skips auditable`
11. `perf: remove per-number UTF-8 array allocation`
12. `docs: define YAML 1.2.2 JSON profile and refresh performance claims`

The ordering above is intentional: establish failing tests first, fix data integrity before expanding features, remove non-YAML-1.2 behavior rather than adding compatibility switches, and revise claims only after evidence exists. The library should be a small, explicit YAML 1.2.2-to-JSON projection—not a general YAML object constructor or a collection of legacy modes.

# Plan: Phase 7 Completion — `IBufferWriter<byte>` Sinks and the Zero-Copy Path

> **Status: P0-P3 implemented.** The input adapter (`Internal/SequenceTextReader`), the emit
> adapter (`Internal/Utf8BufferTextWriter`), the public `IBufferWriter<byte>` serialize sinks,
> the UTF-8 byte deserialization APIs (`ReadOnlyMemory<byte>`/`ReadOnlySequence<byte>`), and the
> `DeserializeAsync(Stream)` re-route (staged pooled input, incremental decode, no
> `Encoding.UTF8.GetString`) are all in place.
>
> Verification evidence: `tests/SVCSystems.Text.Json.Yaml.Tests/BufferApiTests.cs`;
> latest Release suite 1078 tests, 0 failures, 206 documented-policy skips; a byte-path agreement theory
> over all 406 yaml-test-suite cases (268 verified, 138 named skips); Native AOT publish +
> run of `tests/SVCSystems.Text.Json.Yaml.AotTests` exercising the new APIs with reflection
> disabled. Benchmarks (ShortRun, linux-x64, .NET 11): the 64 KB large-scalar sentinel is
> closed — `Deserialize_Large_Async` 409.64 KB vs `Deserialize_Large_Sync` 410.41 KB
> (pre-rework async baseline: 536 KB, +30%), and the byte deserialize path allocates 409.37 KB
> vs 535.27 KB for the string path. Recorded in `test.md` and the README.
> P4 (raw UTF-8 scanner) remains gated on measurements and is not scheduled.

## Goals

1. Public byte-level sink APIs on `YamlSerializer`, additive and AOT/AOA-safe (source generated
   contracts only, no reflection).
2. Remove the full UTF-16 input copy from `DeserializeAsync(Stream)`. The API still stages the
   complete UTF-8 input in a pooled buffer because YamlDotNet's scanner may reread it; decoding
   then proceeds incrementally. This reduces allocation but does not make stream input memory
   constant with respect to document size.
3. Close the 64 KB large-scalar sentinel recorded in `benchmarks/.../SyncVsAsyncBenchmarks.cs`
   and `test.md` (async deserialize was ~+30% allocations vs sync).

## Current pipeline (grounding)

| Path | Today | Cost |
|---|---|---|
| `Serialize(Stream)` | STJ → `Utf8JsonWriter` over pooled JSON buffer → emitter writes chars via `StreamWriter` over `PooledBufferStream` (`YamlSerializer.cs:135`) → re-encoded UTF-8 → one batched `stream.Write(yamlBuffer.WrittenSpan)` | fully buffered output; char round-trip on emit |
| `Deserialize(Stream)` | strict UTF-8 `StreamReader` over a three-byte BOM-prefix adapter → `Parser` → `YamlToJsonConverter` → `Utf8JsonWriter` over `PooledBufferWriter` | no whole-input byte or UTF-16 buffer |
| `DeserializeAsync(Stream)` | `stream.CopyToAsync(PooledBufferStream)` → `SequenceTextReader` → `Parser` → `YamlToJsonConverter` → `Utf8JsonWriter` | full UTF-8 input staged; no whole-input UTF-16 string |

Both serializer halves already produce/consume UTF-8 `IBufferWriter<byte>` internally
(`PooledBufferWriter`); the work is exposing that and inserting incremental adapters where a
string or avoidable intermediate buffer is currently materialized.

## Phases

### P0 — Shared input adapter: `TextReader` over `ReadOnlySequence<byte>` — foundation

* Internal `SequenceTextReader`: a pull-based `TextReader` that decodes UTF-8
  chunks from a `ReadOnlySequence<byte>`/`ReadOnlyMemory<byte>` on demand. No whole-input string.
* Feeds `CreateParser(TextReader, ...)` directly, as the stream path does.
* Implemented policy:
  - **BOM/encoding**: byte and stream inputs use strict UTF-8; a leading UTF-8 BOM (`EF BB BF`) is
    skipped, while malformed UTF-8 is rejected. Other BOMs do not select another encoding.
  - **Cancellation**: the parser is pull-based, so the adapter is the only place to check a
    token. Accept an optional `CancellationToken` and check it at each chunk refill boundary.
  - **Input size**: host responsibility for `System.Text.Json` parity. YAML-specific guards are
    `MaxDepth` and the per-document `MaxAliasExpansionEvents` budget. `MaxScalarLength` is checked
    after a scalar is scanned and materialized; it is not an input or peak-memory limit. There is no
    document-count cap.
* Acceptance: round-trip and error tests identical to the string path; malformed UTF-8 rejection;
  conformance harness
  (406 cases) run through a parser built on the adapter; adversarial cases: BOM, no BOM,
  multi-byte characters split across chunk boundaries, and strict decoder behavior.

### P1 — Serialize `IBufferWriter<byte>` sinks

* New public APIs (additive, mirroring STJ's `Serialize(IBufferWriter<byte>)` shape):
  - `Serialize(IBufferWriter<byte> writer, T value, JsonTypeInfo<T> jsonTypeInfo, YamlSerializerOptions? yamlOptions = null)`
  - `SerializeAsync(...)` with `CancellationToken`
  - Non-generic `JsonTypeInfo` variants included (AOT).
* New internal `Utf8BufferTextWriter` (name TBD): a `TextWriter` subclass that UTF-8-encodes
  chars directly into `IBufferWriter.GetSpan()/Advance()` — replaces the
  `StreamWriter`-over-`PooledBufferStream` sandwich at `YamlSerializer.cs:135/437` and removes
  the intermediate pooled output buffer.
  - Fiddly bits: never split a code unit across `GetSpan` boundaries; encode in bulk when the
    buffer allows; correct final `Advance`; `Flush()` is a no-op (emitter drives the writes).
  - Fallback if YamlDotNet's emitter misbehaves with a non-standard `TextWriter`: keep the
    existing `StreamWriter` + forwarding-`Stream`-over-`IBufferWriter` shape (still zero-copy
    into the caller's buffer, one extra small char buffer).
* The Stream overloads can switch their emit stage to this adapter too (they currently batch the
  whole document, so `stream.Write` becomes chunked only if the adapter is wrapped — decide
  based on `test.md` guidance: sync Stream stays chunked-by-chunk? No: sync Stream today is
  batched by design; keep the existing batched-`Write` behavior and only drop the intermediate
  pooled buffer if measurements agree).
* Acceptance: byte-identical output vs the string path for the existing test corpus; new
  sink round-trip tests; allocation benchmark shows the intermediate YAML buffer gone.

### P2 — Deserialize from bytes; re-route `DeserializeAsync(Stream)`

* New public APIs (STJ parity: STJ deserializes from `ReadOnlyMemory<byte>`, not from a
  `IBufferWriter`):
  - `Deserialize<T>(ReadOnlyMemory<byte> utf8Yaml, JsonTypeInfo<T>, ...)` and
    `Deserialize<T>(ReadOnlySequence<byte> utf8Yaml, ...)` (+ async variants).
* Re-route `DeserializeAsync(Stream)` to asynchronously stage the complete input in a pooled
  buffer (the scanner may reread it), then feed it to `SequenceTextReader`. Delete the
  `Encoding.UTF8.GetString` step; the staged bytes are decoded incrementally. This removes the
  full UTF-16 copy while keeping the change small. Full pull-based async (no staging buffer)
  requires an async-capable `TextReader` and is optional follow-up, gated on measurements.
* Acceptance: `DeserializeAsync` large-scalar allocations within noise of sync in
  `SyncVsAsyncBenchmarks.cs`; conformance harness passes through the byte path; byte input
  with/without BOM.

### P3 — Benchmarks, docs, CI

* Extend `SyncVsAsyncBenchmarks.cs` (or add `BufferSinkBenchmarks.cs`): serialize/deserialize
  to/from `IBufferWriter`/`ReadOnlyMemory<byte>` for small + 64 KB scalar fixtures.
* Update `test.md` sync-vs-async guidance (async still stages input bytes, but no longer materializes
  a whole UTF-16 string) and the README compatibility/API matrix.
* CI: run the conformance harness with the new paths exercised (cheap — data-driven), plus the
  allocation sentinel as a tracked number, not a gate.

### P4 (optional, gated) — Raw UTF-8 scanner

* Only if P0-P3 measurements show the remaining char decode is the dominant cost. A custom
  `IParser` over raw UTF-8 bytes eliminates the incremental decode entirely, but re-implements
  the YAML scanner (block scalars, indentation, folding) — the roadmap's "thin custom
  parser/scanner wrapper" risk item, a project of its own. The conformance harness is what
  makes it feasible. **Not scheduled; decide after P3 numbers.**

## Risks and mitigations

| Risk | Mitigation |
|---|---|
| YamlDotNet emitter assumptions about `TextWriter` | adapter keeps the `TextWriter` contract; fallback shape (forwarding `Stream` + `StreamWriter`) preserved behind the same public API |
| Chunk-boundary bugs (split multi-byte runes, split surrogates) | adapter is pure and small; fuzz + the conformance harness drive it on every case |
| BOM / encoding policy divergence across the three input styles | single documented rule in P0, tested for String, Stream, and byte paths |
| Cancellation that never fires mid-parse | adapter checks at every chunk refill; test a canceled stream mid-document |
| Unbounded input memory | input size is the hosting layer's responsibility (same stance as `System.Text.Json`); alias expansion is budgeted per document, while scalar length is only checked after scanning |
| Scope creep into a custom scanner | P4 explicitly gated on measurements; harness + benchmarks decide, not enthusiasm |

## Non-goals (unchanged)

No public surface for comments/formatting (Phase 6 resolution stands); no byte-identical
round trip; no reflection-based materialization;
no async YamlDotNet internals — async stays "async I/O around sync CPU" with cancellation at
chunk boundaries.

## Sequencing

P0 → P1 → P2 → P3. P1 and P2 both depend on P0's decisions (BOM, cancellation, limits) but P1
can land independently if the decisions are documented. P4 is not scheduled. Each phase defines
its acceptance criteria against the existing conformance harness and benchmark suite, so the
64 KB sentinel is the measurable finish line: async deserialize large-scalar allocations ≈ sync.

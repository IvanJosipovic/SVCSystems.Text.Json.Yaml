### Sync vs async stream overloads (MemoryStream, no device I/O)

Measured after the phase 7 completion rework (`docs/phase-7-completion-plan.md`):

| Operation | Sync | Async | Delta |
|---|---|---|---|
| Serialize small | 16.1 µs / 4.45 KB | 16.0 µs / 4.45 KB | parity |
| Serialize 64 KB scalar | 3,168 µs / 194.08 KB | 3,076 µs / 194.08 KB | allocation parity (batched write in both; was 199 KB with the StreamWriter emit path) |
| Deserialize small | 35.1 µs / 18.87 KB | 37.8 µs / 18.02 KB | ≈ equal, async slightly lighter |
| Deserialize 64 KB scalar | 2,455 µs / 410.41 KB | 2,345 µs / **409.64 KB** | **parity** (pre-rework baseline: 2,485 µs / 536 KB / +30% — the whole-input UTF-16 copy is gone) |

The 64 KB async deserialize row is the sentinel from the phase 7 plan: it used to buffer the
whole input and decode it with `Encoding.UTF8.GetString` (536 KB). It now stages the UTF-8
input in pooled buffers and decodes it incrementally via the internal `SequenceTextReader`,
matching the sync allocation profile (≈410 KB).

Guidance: serialize allocates identically for sync and async (the document is encoded into
pooled UTF-8 buffers and written with a single call). The async deserialize overload still
stages the whole input (the YAML scanner reads it more than once), so its allocation profile is
proportional to input size like sync; neither overload materializes a whole-input UTF-16 string
anymore. Choose based on direction and cancellation needs.

### Buffer sinks and byte deserialization (phase 7 completion)

`BufferSinkBenchmarks` compares the new APIs against the string overloads. Measured ShortRun,
linux-x64, .NET 11 (ArrayBufferWriter consumers are created per invocation, so sink numbers
include that harness overhead; ArrayBufferWriter cannot return buffers to the pool):

| Method | Mean | Allocated |
|---|---:|---:|
| Serialize_Small_Sink | 16.5 µs | 4.38 KB |
| Serialize_Small_String | 16.0 µs | 4.39 KB |
| Serialize_Large_Sink | 3,853 µs | 385.95 KB |
| Serialize_Large_String | 3,206 µs | 258.15 KB |
| Deserialize_Small_Bytes | 36.2 µs | 17.76 KB |
| Deserialize_Small_String | 40.1 µs | 15.88 KB |
| Deserialize_Large_Bytes | 2,524 µs | **409.37 KB** |
| Deserialize_Large_String | 2,679 µs | 535.27 KB |

Reading the numbers:

- `Deserialize(ReadOnlyMemory<byte>/ReadOnlySequence<byte>, ...)` decodes incrementally, so the
  ~126 KB whole-input UTF-16 copy (64 KB input × 2) disappears from the 64 KB sentinel —
  409 KB vs 535 KB. This is the same saving the re-routed `DeserializeAsync(Stream)` gets.
- The small byte deserialize pays a one-off 2 KB char decode buffer, which amortizes on large
  inputs (net faster and lighter above a few KB).
- The large sink serialize row is dominated by the benchmark's own non-pooled
  `ArrayBufferWriter` growth (386 KB) versus the pooled `PooledBufferWriter` used by the string
  path (258 KB); the library-side emit path itself allocates strictly less than before (no
  `StreamWriter` char buffer). Prefer pooled or caller-owned `IBufferWriter` implementations in
  production.
- The intermediate JSON stage remains pooled-buffered in all serialize paths (unavoidable:
  `System.Text.Json` owns object mapping and needs the complete token stream).

## Building from source

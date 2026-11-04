### Sync vs async stream overloads (MemoryStream, no device I/O)

| Operation | Sync | Async | Delta |
|---|---|---|---|
| Serialize small | 16.4 µs / 6.5 KB | 17.2 µs / 6.6 KB | ~+5% time (async state machine) |
| Serialize 64 KB scalar | 2,763 µs / 390 KB | 2,771 µs / 199 KB | async **-49% allocations** (batched write) |
| Deserialize small | 37.4 µs / 18.9 KB | 36.5 µs / 16.2 KB | ≈ equal, async slightly lighter |
| Deserialize 64 KB scalar | 2,390 µs / 410 KB | 2,485 µs / 536 KB | async +30% allocations (whole input is buffered and decoded) |

Guidance: the sync `Stream` overloads stream in constant extra memory (chunked writes, chunked
reads) — prefer them for memory-constrained large payloads. The async overloads batch the
serialized document into one write (cheaper for large outputs) but buffer the whole input before
parsing (more expensive for large inputs). Choose based on direction and size.

## Building from source

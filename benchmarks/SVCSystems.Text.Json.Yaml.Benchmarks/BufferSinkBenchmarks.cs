using System.Buffers;
using System.Text;
using BenchmarkDotNet.Attributes;
using SVCSystems.Text.Json.Yaml;

namespace YamlBenchmarks;

/// <summary>
/// Phase 7 completion benchmarks (docs/phase-7-completion-plan.md): the
/// <see cref="IBufferWriter{T}"/> serialize sinks and the UTF-8 byte deserialization APIs,
/// compared against the string overloads, for the small document and the 64 KB large-scalar
/// sentinel.
/// </summary>
[MemoryDiagnoser]
public class BufferSinkBenchmarks
{
    private readonly SmallConfig _smallModel = Fixtures.CreateSmallConfig();
    private readonly LargeScalarDocument _largeModel = Fixtures.CreateLargeScalar(64);
    private byte[] _smallYaml = [];
    private byte[] _largeYaml = [];

    [GlobalSetup]
    public void Setup()
    {
        _smallYaml = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(_smallModel, BenchmarkJsonContext.Default.SmallConfig));
        _largeYaml = Encoding.UTF8.GetBytes(YamlSerializer.Serialize(_largeModel, BenchmarkJsonContext.Default.LargeScalarDocument));
    }

    // --- Serialize to IBufferWriter<byte> ---

    [Benchmark]
    public void Serialize_Small_Sink()
    {
        var buffer = new ArrayBufferWriter<byte>();
        YamlSerializer.Serialize(buffer, _smallModel, BenchmarkJsonContext.Default.SmallConfig);
    }

    [Benchmark]
    public void Serialize_Large_Sink()
    {
        var buffer = new ArrayBufferWriter<byte>();
        YamlSerializer.Serialize(buffer, _largeModel, BenchmarkJsonContext.Default.LargeScalarDocument);
    }

    // --- Serialize to string (baseline) ---

    [Benchmark]
    public string Serialize_Small_String() =>
        YamlSerializer.Serialize(_smallModel, BenchmarkJsonContext.Default.SmallConfig);

    [Benchmark]
    public string Serialize_Large_String() =>
        YamlSerializer.Serialize(_largeModel, BenchmarkJsonContext.Default.LargeScalarDocument);

    // --- Deserialize from UTF-8 bytes ---

    [Benchmark]
    public SmallConfig? Deserialize_Small_Bytes() =>
        YamlSerializer.Deserialize(_smallYaml, BenchmarkJsonContext.Default.SmallConfig);

    [Benchmark]
    public LargeScalarDocument? Deserialize_Large_Bytes() =>
        YamlSerializer.Deserialize(_largeYaml, BenchmarkJsonContext.Default.LargeScalarDocument);

    // --- Deserialize from string (baseline) ---

    [Benchmark]
    public SmallConfig? Deserialize_Small_String() =>
        YamlSerializer.Deserialize(Encoding.UTF8.GetString(_smallYaml), BenchmarkJsonContext.Default.SmallConfig);

    [Benchmark]
    public LargeScalarDocument? Deserialize_Large_String() =>
        YamlSerializer.Deserialize(Encoding.UTF8.GetString(_largeYaml), BenchmarkJsonContext.Default.LargeScalarDocument);
}

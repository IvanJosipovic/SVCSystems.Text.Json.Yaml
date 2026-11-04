using System.Text;
using BenchmarkDotNet.Attributes;
using SVCSystems.Text.Json.Yaml;

namespace YamlBenchmarks;

/// <summary>
/// Compares the synchronous and asynchronous stream overloads of
/// <see cref="YamlSerializer"/>. Streams are <see cref="MemoryStream"/>s so that only the
/// serializer overhead (not real device I/O) is measured; deserialize benchmarks construct a
/// fresh stream per invocation, so the measured allocations include that constructor.
/// </summary>
[MemoryDiagnoser]
public class SyncVsAsyncBenchmarks
{
    private readonly SmallConfig _smallModel = Fixtures.CreateSmallConfig();
    private readonly LargeScalarDocument _largeModel = Fixtures.CreateLargeScalar(64);
    private byte[] _smallYaml = [];
    private byte[] _largeYaml = [];

    [GlobalSetup]
    public void Setup()
    {
        using var small = new MemoryStream();
        YamlSerializer.Serialize(small, _smallModel, BenchmarkJsonContext.Default.SmallConfig);
        _smallYaml = small.ToArray();

        using var large = new MemoryStream();
        YamlSerializer.Serialize(large, _largeModel, BenchmarkJsonContext.Default.LargeScalarDocument);
        _largeYaml = large.ToArray();
    }

    // --- Serialize: small document ---

    [Benchmark]
    public void Serialize_Small_Sync()
    {
        using var stream = new MemoryStream();
        YamlSerializer.Serialize(stream, _smallModel, BenchmarkJsonContext.Default.SmallConfig);
    }

    [Benchmark]
    public async Task Serialize_Small_Async()
    {
        using var stream = new MemoryStream();
        await YamlSerializer.SerializeAsync(stream, _smallModel, BenchmarkJsonContext.Default.SmallConfig);
    }

    // --- Serialize: 64 KB scalar ---

    [Benchmark]
    public void Serialize_Large_Sync()
    {
        using var stream = new MemoryStream();
        YamlSerializer.Serialize(stream, _largeModel, BenchmarkJsonContext.Default.LargeScalarDocument);
    }

    [Benchmark]
    public async Task Serialize_Large_Async()
    {
        using var stream = new MemoryStream();
        await YamlSerializer.SerializeAsync(stream, _largeModel, BenchmarkJsonContext.Default.LargeScalarDocument);
    }

    // --- Deserialize: small document ---

    [Benchmark]
    public SmallConfig? Deserialize_Small_Sync()
    {
        using var stream = new MemoryStream(_smallYaml);
        return YamlSerializer.Deserialize(stream, BenchmarkJsonContext.Default.SmallConfig);
    }

    [Benchmark]
    public async Task<SmallConfig?> Deserialize_Small_Async()
    {
        using var stream = new MemoryStream(_smallYaml);
        return await YamlSerializer.DeserializeAsync(stream, BenchmarkJsonContext.Default.SmallConfig);
    }

    // --- Deserialize: 64 KB scalar ---

    [Benchmark]
    public LargeScalarDocument? Deserialize_Large_Sync()
    {
        using var stream = new MemoryStream(_largeYaml);
        return YamlSerializer.Deserialize(stream, BenchmarkJsonContext.Default.LargeScalarDocument);
    }

    [Benchmark]
    public async Task<LargeScalarDocument?> Deserialize_Large_Async()
    {
        using var stream = new MemoryStream(_largeYaml);
        return await YamlSerializer.DeserializeAsync(stream, BenchmarkJsonContext.Default.LargeScalarDocument);
    }
}

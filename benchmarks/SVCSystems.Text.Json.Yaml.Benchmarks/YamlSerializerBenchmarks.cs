using BenchmarkDotNet.Attributes;
using SVCSystems.Text.Json.Yaml;

namespace YamlBenchmarks;

/// <summary>
/// Compares YamlDotNet's reflection based object serialization with
/// SVCSystems.Text.Json.Yaml in reflection mode and in source generated mode.
/// </summary>
/// <remarks>
/// YamlDotNet's static context (analyzer generated) serialization is not included in this
/// benchmark suite because it requires YamlDotNet.Analyzers.StaticGenerator; it can be added as a
/// follow up. Run with <c>dotnet run -c Release -- --filter *</c>.
/// </remarks>
[MemoryDiagnoser]
public class SmallConfigBenchmarks
{
    private readonly SmallConfig _model = Fixtures.CreateSmallConfig();
    private readonly string _yaml = YamlSerializer.Serialize(Fixtures.CreateSmallConfig(), BenchmarkJsonContext.Default.SmallConfig);

    [Benchmark(Baseline = true)]
    public string YamlDotNetReflection_Serialize() => YamlDotNetReflection.Serialize(_model);

    [Benchmark]
    public string StjReflection_Serialize() => YamlSerializer.Serialize(_model);

    [Benchmark]
    public string StjContracts_Serialize() => YamlSerializer.Serialize(_model, BenchmarkJsonContext.Default.SmallConfig);

    [Benchmark]
    public SmallConfig YamlDotNetReflection_Deserialize() => YamlDotNetReflection.Deserialize<SmallConfig>(_yaml)!;

    [Benchmark]
    public SmallConfig? StjReflection_Deserialize() => YamlSerializer.Deserialize<SmallConfig>(_yaml);

    [Benchmark]
    public SmallConfig? StjContracts_Deserialize() => YamlSerializer.Deserialize(_yaml, BenchmarkJsonContext.Default.SmallConfig);
}

[MemoryDiagnoser]
public class KubeManifestBenchmarks
{
    private readonly KubeManifest _model = Fixtures.CreateKubeManifest();
    private readonly string _yaml = YamlSerializer.Serialize(Fixtures.CreateKubeManifest(), BenchmarkJsonContext.Default.KubeManifest);

    [Benchmark(Baseline = true)]
    public string YamlDotNetReflection_Serialize() => YamlDotNetReflection.Serialize(_model);

    [Benchmark]
    public string StjReflection_Serialize() => YamlSerializer.Serialize(_model);

    [Benchmark]
    public string StjContracts_Serialize() => YamlSerializer.Serialize(_model, BenchmarkJsonContext.Default.KubeManifest);

    [Benchmark]
    public KubeManifest YamlDotNetReflection_Deserialize() => YamlDotNetReflection.Deserialize<KubeManifest>(_yaml)!;

    [Benchmark]
    public KubeManifest? StjReflection_Deserialize() => YamlSerializer.Deserialize<KubeManifest>(_yaml);

    [Benchmark]
    public KubeManifest? StjContracts_Deserialize() => YamlSerializer.Deserialize(_yaml, BenchmarkJsonContext.Default.KubeManifest);
}

[MemoryDiagnoser]
public class ListBenchmarks
{
    private readonly ItemList _model = Fixtures.CreateItems(1000);
    private readonly string _yaml = YamlSerializer.Serialize(Fixtures.CreateItems(1000), BenchmarkJsonContext.Default.ItemList);

    [Benchmark(Baseline = true)]
    public string YamlDotNetReflection_Serialize() => YamlDotNetReflection.Serialize(_model);

    [Benchmark]
    public string StjReflection_Serialize() => YamlSerializer.Serialize(_model);

    [Benchmark]
    public string StjContracts_Serialize() => YamlSerializer.Serialize(_model, BenchmarkJsonContext.Default.ItemList);

    [Benchmark]
    public ItemList YamlDotNetReflection_Deserialize() => YamlDotNetReflection.Deserialize<ItemList>(_yaml)!;

    [Benchmark]
    public ItemList? StjReflection_Deserialize() => YamlSerializer.Deserialize<ItemList>(_yaml);

    [Benchmark]
    public ItemList? StjContracts_Deserialize() => YamlSerializer.Deserialize(_yaml, BenchmarkJsonContext.Default.ItemList);
}

[MemoryDiagnoser]
public class LargeScalarBenchmarks
{
    private readonly LargeScalarDocument _model = Fixtures.CreateLargeScalar(64);
    private readonly string _yaml = YamlSerializer.Serialize(Fixtures.CreateLargeScalar(64), BenchmarkJsonContext.Default.LargeScalarDocument);

    [Benchmark(Baseline = true)]
    public string YamlDotNetReflection_Serialize() => YamlDotNetReflection.Serialize(_model);

    [Benchmark]
    public string StjReflection_Serialize() => YamlSerializer.Serialize(_model);

    [Benchmark]
    public string StjContracts_Serialize() => YamlSerializer.Serialize(_model, BenchmarkJsonContext.Default.LargeScalarDocument);

    [Benchmark]
    public LargeScalarDocument YamlDotNetReflection_Deserialize() => YamlDotNetReflection.Deserialize<LargeScalarDocument>(_yaml)!;

    [Benchmark]
    public LargeScalarDocument? StjReflection_Deserialize() => YamlSerializer.Deserialize<LargeScalarDocument>(_yaml);

    [Benchmark]
    public LargeScalarDocument? StjContracts_Deserialize() => YamlSerializer.Deserialize(_yaml, BenchmarkJsonContext.Default.LargeScalarDocument);
}

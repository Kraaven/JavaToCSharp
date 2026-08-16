using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace JavaToCSharp.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 3, iterationCount: 10)]
public class ConvertTextBenchmarks
{
    private string _small = string.Empty;
    private string _medium = string.Empty;
    private string _large = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        string corpus = Path.Combine(AppContext.BaseDirectory, "Corpus");
        _small = File.ReadAllText(Path.Combine(corpus, "small", "Small.java"));
        _medium = File.ReadAllText(Path.Combine(corpus, "medium", "Medium.java"));
        _large = File.ReadAllText(Path.Combine(corpus, "large", "Large.java"));
    }

    [Benchmark]
    public string Benchmark_ConvertText_Small()
    {
        return JavaToCSharpConverter.ConvertText(_small).ConvertedText ?? "";
    }

    [Benchmark]
    public string Benchmark_ConvertText_Medium()
    {
        return JavaToCSharpConverter.ConvertText(_medium).ConvertedText ?? "";
    }

    [Benchmark]
    public string Benchmark_ConvertText_Large()
    {
        return JavaToCSharpConverter.ConvertText(_large).ConvertedText ?? "";
    }
}

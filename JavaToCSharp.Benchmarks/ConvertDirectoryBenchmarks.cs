using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;

namespace JavaToCSharp.Benchmarks;

[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net10_0, warmupCount: 2, iterationCount: 8, invocationCount: 1)]
public class ConvertDirectoryBenchmarks
{
    private string _corpusDir = string.Empty;
    private string _tempRoot = string.Empty;
    private string _workDir = string.Empty;

    [GlobalSetup]
    public void Setup()
    {
        _corpusDir = Path.Combine(AppContext.BaseDirectory, "Corpus");
        _tempRoot = Path.Combine(Path.GetTempPath(), $"javatocsharp-bench-{Guid.NewGuid():N}");
        _workDir = Path.Combine(_tempRoot, "work");
        Directory.CreateDirectory(_tempRoot);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        if (Directory.Exists(_tempRoot))
        {
            Directory.Delete(_tempRoot, recursive: true);
        }
    }

    [IterationSetup]
    public void IterationSetup()
    {
        // Restore a private copy of the corpus each iteration so replace mode always has
        // fresh .java files (the source Corpus/ tree must never be modified).
        if (Directory.Exists(_workDir))
        {
            Directory.Delete(_workDir, recursive: true);
        }

        CopyCorpus(_corpusDir, _workDir);
    }

    [Benchmark]
    public async Task Benchmark_ConvertDirectory_NonDestructive()
    {
        string output = Path.Combine(_workDir, "out");
        Directory.CreateDirectory(output);

        await JavaToCSharpConverter.ConvertDirectoryAsync(
            new DirectoryInfo(_workDir),
            new DirectoryInfo(output),
            new JavaConversionOptions());
    }

    [Benchmark]
    public async Task Benchmark_ConvertDirectory_ReplaceMode()
    {
        await JavaToCSharpConverter.ConvertDirectoryAsync(
            new DirectoryInfo(_workDir),
            outputDirectory: null,
            new JavaConversionOptions());
    }

    private static void CopyCorpus(string sourceRoot, string destRoot)
    {
        foreach (string file in Directory.EnumerateFiles(sourceRoot, "*.java", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sourceRoot, file);
            string dest = Path.Combine(destRoot, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest) ?? destRoot);
            File.Copy(file, dest);
        }
    }
}

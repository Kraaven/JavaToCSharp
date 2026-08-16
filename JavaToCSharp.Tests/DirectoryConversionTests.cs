namespace JavaToCSharp.Tests;

public class DirectoryConversionTests
{
    [Fact]
    public async Task ConvertDirectoryAsync_ReplaceMode_DeletesSourceAndWritesCs()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var inputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "input"));

        try
        {
            inputDir.Create();
            var sourceFile = new FileInfo(Path.Combine(inputDir.FullName, "Test.java"));
            const string javaCode = """
                package com.example;

                public class Test {
                    public static void main(String[] args) {
                        System.out.println("Hello");
                    }
                }
                """;
            await File.WriteAllTextAsync(sourceFile.FullName, javaCode);

            var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
                inputDir,
                outputDirectory: null,
                new JavaConversionOptions { ConvertSystemOutToConsole = true },
                new ConvertDirectoryOptions { ContinueOnError = true });

            Assert.Equal(1, result.TotalFiles);
            Assert.Equal(1, result.SucceededCount);
            Assert.Empty(result.FailedFiles);

            var outputFile = new FileInfo(Path.Combine(inputDir.FullName, "Test.cs"));
            Assert.True(outputFile.Exists, "Expected Test.cs to be written");
            Assert.False(sourceFile.Exists, "Expected Test.java to be deleted in replace mode");

            string csText = await File.ReadAllTextAsync(outputFile.FullName);
            Assert.Contains("public class Test", csText);
            Assert.Contains("Console.WriteLine", csText);
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_WithOutputDir_MirrorsTree_LeavesSourceUntouched()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var inputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "input"));
        var outputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "output"));

        try
        {
            inputDir.Create();
            var subDir = inputDir.CreateSubdirectory("nested");

            var rootSource = new FileInfo(Path.Combine(inputDir.FullName, "Root.java"));
            var nestedSource = new FileInfo(Path.Combine(subDir.FullName, "Nested.java"));

            await File.WriteAllTextAsync(rootSource.FullName, "package com.example; public class Root { }");
            await File.WriteAllTextAsync(nestedSource.FullName, "package com.example.nested; public class Nested { }");

            var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
                inputDir,
                outputDir,
                new(),
                new ConvertDirectoryOptions { ContinueOnError = true });

            Assert.Equal(2, result.TotalFiles);
            Assert.Equal(2, result.SucceededCount);
            Assert.Empty(result.FailedFiles);

            Assert.True(File.Exists(Path.Combine(outputDir.FullName, "Root.cs")), "Expected mirrored Root.cs");
            Assert.True(File.Exists(Path.Combine(outputDir.FullName, "nested", "Nested.cs")), "Expected nested Nested.cs");

            Assert.True(rootSource.Exists, "Expected source .java untouched in mirror mode");
            Assert.True(nestedSource.Exists, "Expected nested source .java untouched in mirror mode");
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_ContinuesOnError_WhenConfigured()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var inputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "input"));

        try
        {
            inputDir.Create();
            var validFile = new FileInfo(Path.Combine(inputDir.FullName, "Valid.java"));
            var invalidFile = new FileInfo(Path.Combine(inputDir.FullName, "Invalid.java"));

            await File.WriteAllTextAsync(validFile.FullName, "package com.example; public enum Test { A, B }");
            await File.WriteAllTextAsync(invalidFile.FullName, "invalid java code here");

            var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
                inputDir,
                outputDirectory: null,
                new(),
                new ConvertDirectoryOptions { ContinueOnError = true });

            Assert.Equal(2, result.TotalFiles);
            Assert.Equal(1, result.SucceededCount);
            Assert.Equal(1, result.FailedCount);
            Assert.Single(result.Exceptions);
            Assert.Contains(invalidFile.FullName, result.FailedFiles.Select(f => f.InputPath));
            Assert.Equal(invalidFile.FullName, result.Exceptions[0].InputPath);

            Assert.True(File.Exists(Path.Combine(inputDir.FullName, "Valid.cs")), "Expected valid file converted");
            Assert.False(validFile.Exists, "Expected valid .java deleted in replace mode");
            Assert.True(invalidFile.Exists, "Expected failed .java preserved");
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_StopsOnFirstError_WhenConfigured()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var inputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "input"));

        try
        {
            inputDir.Create();
            var validFile = new FileInfo(Path.Combine(inputDir.FullName, "Valid.java"));
            var invalidFile = new FileInfo(Path.Combine(inputDir.FullName, "Invalid.java"));

            await File.WriteAllTextAsync(validFile.FullName, "package com.example; public enum Test { A, B }");
            await File.WriteAllTextAsync(invalidFile.FullName, "invalid java code here");

            await Assert.ThrowsAnyAsync<Exception>(() => JavaToCSharpConverter.ConvertDirectoryAsync(
                inputDir,
                outputDirectory: null,
                new(),
                new ConvertDirectoryOptions { ContinueOnError = false }));

            Assert.True(invalidFile.Exists, "Expected failed .java preserved");
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_Overwrite_False_SkipsExisting()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var inputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "input"));

        try
        {
            inputDir.Create();
            var sourceFile = new FileInfo(Path.Combine(inputDir.FullName, "Test.java"));
            var outputFile = new FileInfo(Path.Combine(inputDir.FullName, "Test.cs"));
            const string existing = "// pre-existing output";

            await File.WriteAllTextAsync(sourceFile.FullName, "package com.example; public class Test { }");
            await File.WriteAllTextAsync(outputFile.FullName, existing);

            var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
                inputDir,
                outputDirectory: null,
                new(),
                new ConvertDirectoryOptions { Overwrite = false });

            Assert.Equal(1, result.TotalFiles);
            Assert.Equal(0, result.SucceededCount);
            Assert.Equal(0, result.FailedCount);

            Assert.True(outputFile.Exists);
            Assert.Equal(existing, await File.ReadAllTextAsync(outputFile.FullName));
            Assert.True(sourceFile.Exists, "Expected skipped .java preserved");
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }

    [Fact]
    public async Task ConvertDirectoryAsync_RespectsMaxDegreeOfParallelism()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var inputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "input"));

        try
        {
            inputDir.Create();
            const int fileCount = 6;

            for (int i = 0; i < fileCount; i++)
            {
                var file = new FileInfo(Path.Combine(inputDir.FullName, $"Test{i}.java"));
                await File.WriteAllTextAsync(file.FullName, $$"""
                    package com.example;
                    public class Test{{i}} {
                        private int value = {{i}};
                        public int getValue() { return value; }
                    }
                    """);
            }

            int concurrent = 0;
            int maxConcurrent = 0;
            var progress = new SyncProgress<FileConversionProgress>(_ =>
            {
                int now = Interlocked.Increment(ref concurrent);
                InterlockedExchangeMax(ref maxConcurrent, now);
                Thread.Sleep(20);
                Interlocked.Decrement(ref concurrent);
            });

            var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
                inputDir,
                outputDirectory: null,
                new(),
                new ConvertDirectoryOptions { MaxDegreeOfParallelism = 1 },
                progress);

            Assert.Equal(1, maxConcurrent);
            Assert.Equal(fileCount, result.TotalFiles);
            Assert.Equal(fileCount, result.SucceededCount);
            Assert.Empty(result.FailedFiles);

            for (int i = 0; i < fileCount; i++)
            {
                var csFile = new FileInfo(Path.Combine(inputDir.FullName, $"Test{i}.cs"));
                Assert.True(csFile.Exists, $"Expected Test{i}.cs to be written");
                Assert.Contains($"public class Test{i}", await File.ReadAllTextAsync(csFile.FullName));
            }
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }

    [Fact]
    public void ConvertText_ReturnsConversionResult_OnSuccess()
    {
        const string java = """
            package com.example;
            public class Test {
                public static void main(String[] args) {
                    System.out.println("Hello");
                }
            }
            """;

        var result = JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions { ConvertSystemOutToConsole = true });

        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(1, result.SucceededCount);
        Assert.Empty(result.FailedFiles);
        Assert.NotNull(result.ConvertedText);
        Assert.Contains("Console.WriteLine", result.ConvertedText);
    }

    [Fact]
    public void ConvertText_ReturnsConversionResult_OnParseFailure()
    {
        const string java = "invalid java code here";

        var result = JavaToCSharpConverter.ConvertText(java, new());

        Assert.Equal(1, result.TotalFiles);
        Assert.Equal(0, result.SucceededCount);
        Assert.Equal(1, result.FailedCount);
        Assert.Empty(result.SucceededFiles);
        Assert.Single(result.FailedFiles);
        Assert.Single(result.Exceptions);
        Assert.Equal("<inline>", result.Exceptions[0].InputPath);
    }

    private sealed class SyncProgress<T> : IProgress<T>
    {
        private readonly Action<T> _handler;

        public SyncProgress(Action<T> handler)
        {
            _handler = handler;
        }

        public void Report(T value) => _handler(value);
    }

    private static void InterlockedExchangeMax(ref int location, int value)
    {
        int current = Volatile.Read(ref location);
        while (value > current)
        {
            int prior = Interlocked.CompareExchange(ref location, value, current);
            if (prior == current)
            {
                return;
            }

            current = prior;
        }
    }
}

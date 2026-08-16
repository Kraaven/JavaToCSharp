using System.Text;

namespace JavaToCSharp.Tests;

public class LoggingTests
{
    private const string ValidJava = """
        package com.example;

        public class Test {
            public static void main(String[] args) {
                System.out.println("Hello");
            }
        }
        """;

    [Fact]
    public void ConvertText_EnableLogging_LogsToConsole()
    {
        var sw = new StringBuilder();
        var originalOut = Console.Out;

        try
        {
            Console.SetOut(new StringWriter(sw));

            var options = new JavaConversionOptions
            {
                EnableLogging = true,
                LogToConsole = true,
            };

            var result = JavaToCSharpConverter.ConvertText(ValidJava, options, "Test.java");

            Assert.Equal(1, result.SucceededCount);
            Assert.Contains("Test.java -> OK", sw.ToString());
        }
        finally
        {
            Console.SetOut(originalOut);
        }
    }

    [Fact]
    public void ConvertText_EnableLogging_LogsToCallback()
    {
        var logs = new List<string>();

        var options = new JavaConversionOptions
        {
            EnableLogging = true,
            LogToCallback = true,
            Log = logs.Add,
        };

        JavaToCSharpConverter.ConvertText(ValidJava, options, "Test.java");

        Assert.Single(logs);
        Assert.Equal("Test.java -> OK", logs[0]);
    }

    [Fact]
    public void ConvertText_EnableLogging_FailureLogsException()
    {
        var logs = new List<string>();

        var options = new JavaConversionOptions
        {
            EnableLogging = true,
            LogToCallback = true,
            Log = logs.Add,
        };

        var result = JavaToCSharpConverter.ConvertText("invalid java code here", options, "Broken.java");

        Assert.Equal(1, result.FailedCount);
        Assert.Single(logs);
        Assert.StartsWith("Broken.java -> FAILED: System.InvalidOperationException", logs[0]);
    }

    [Fact]
    public void ConvertText_LoggingDisabled_NoOutput()
    {
        var logs = new List<string>();

        var options = new JavaConversionOptions
        {
            EnableLogging = false,
            LogToCallback = true,
            Log = logs.Add,
        };

        JavaToCSharpConverter.ConvertText(ValidJava, options, "Test.java");

        Assert.Empty(logs);
    }

    [Fact]
    public async Task ConvertDirectoryAsync_EnableLogging_LogsIndexedLines()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var inputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "input"));

        try
        {
            inputDir.Create();
            await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "One.java"), ValidJava);
            await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "Two.java"), "package com.example; public enum Two { A, B }");

            var logs = new List<string>();

            var options = new JavaConversionOptions
            {
                EnableLogging = true,
                LogToCallback = true,
                Log = logs.Add,
            };

            await JavaToCSharpConverter.ConvertDirectoryAsync(
                inputDir,
                outputDirectory: null,
                options,
                new ConvertDirectoryOptions { ContinueOnError = true });

            Assert.Equal(2, logs.Count);
            Assert.Contains(logs, line => line.StartsWith("[1/2] ", StringComparison.Ordinal));
            Assert.Contains(logs, line => line.StartsWith("[2/2] ", StringComparison.Ordinal));
            Assert.All(logs, line => Assert.EndsWith(" -> OK", line));
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
    public async Task ConvertDirectoryAsync_EnableLogging_LogsSkipped()
    {
        var tempDir = new DirectoryInfo(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString()));
        var inputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "input"));
        var outputDir = new DirectoryInfo(Path.Combine(tempDir.FullName, "output"));

        try
        {
            inputDir.Create();
            outputDir.Create();
            await File.WriteAllTextAsync(Path.Combine(inputDir.FullName, "One.java"), ValidJava);
            await File.WriteAllTextAsync(Path.Combine(outputDir.FullName, "One.cs"), "// already converted");

            var logs = new List<string>();

            var options = new JavaConversionOptions
            {
                EnableLogging = true,
                LogToCallback = true,
                Log = logs.Add,
            };

            var result = await JavaToCSharpConverter.ConvertDirectoryAsync(
                inputDir,
                outputDir,
                options,
                new ConvertDirectoryOptions { ContinueOnError = true, Overwrite = false });

            Assert.Equal(0, result.SucceededCount);
            Assert.Equal(0, result.FailedCount);
            Assert.Single(logs);
            Assert.Contains("[1/1] ", logs[0], StringComparison.Ordinal);
            Assert.Contains("One.java", logs[0], StringComparison.Ordinal);
            Assert.Contains(" -> SKIPPED (output already exists)", logs[0], StringComparison.Ordinal);
        }
        finally
        {
            if (tempDir.Exists)
            {
                tempDir.Delete(true);
            }
        }
    }
}

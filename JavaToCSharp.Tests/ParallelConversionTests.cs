namespace JavaToCSharp.Tests;

public class ParallelConversionTests
{
    [Fact]
    public void ParallelConversion_IsDeterministic()
    {
        const string java = """
        package com.example.cards;

        public enum Day {
            SUNDAY, MONDAY, TUESDAY
        }
        """;

        var reference = JavaToCSharpConverter.ConvertText(java).ConvertedText ?? "";

        const int iterations = 64;
        var results = new string[iterations];

        Parallel.For(0, iterations, i => results[i] = JavaToCSharpConverter.ConvertText(java).ConvertedText ?? "");

        Assert.All(results, result => Assert.Equal(reference, result));
    }

    [Fact]
    public void ParallelConversion_NoCrossFileLeak()
    {
        const string enumJava = """
        package com.example.cards;

        public enum Suit {
            HEARTS, DIAMONDS, CLUBS, SPADES
        }
        """;

        const string interfaceJava = """
        package com.example.shapes;

        public interface Shape {
            double area();
        }
        """;

        string referenceEnum = JavaToCSharpConverter.ConvertText(enumJava).ConvertedText ?? "";
        string referenceInterface = JavaToCSharpConverter.ConvertText(interfaceJava).ConvertedText ?? "";

        const int iterations = 32;
        var results = new string[iterations * 2];

        Parallel.For(0, iterations, i =>
        {
            results[i * 2] = JavaToCSharpConverter.ConvertText(enumJava).ConvertedText ?? "";
            results[i * 2 + 1] = JavaToCSharpConverter.ConvertText(interfaceJava).ConvertedText ?? "";
        });

        for (int i = 0; i < iterations; i++)
        {
            Assert.Equal(referenceEnum, results[i * 2]);
            Assert.Equal(referenceInterface, results[i * 2 + 1]);
        }

        // Regression checks: no enum static-using leak across files, no interface I-prefix renaming.
        Assert.DoesNotContain(results, result => result.Contains("using static Com.Example.Cards"));
        Assert.DoesNotContain(results, result => result.Contains("IShape"));
    }

    [Fact]
    public void ParallelConversion_Stress_IsThreadSafe()
    {
        const int fileCount = 32;
        const int passes = 8;

        var files = new string[fileCount];

        for (int i = 0; i < fileCount; i++)
        {
            files[i] = $$"""
            package com.example.stress{{i}};

            import java.util.List;

            public class StressClass{{i}} {
                private int value = {{i}};
                public int getValue() { return value; }
            }
            """;
        }

        var references = files.Select(f => JavaToCSharpConverter.ConvertText(f).ConvertedText ?? "").ToArray();

        var results = new string[fileCount * passes];

        Parallel.For(0, results.Length, idx =>
        {
            int i = idx % fileCount;
            results[idx] = JavaToCSharpConverter.ConvertText(files[i]).ConvertedText ?? "";
        });

        for (int idx = 0; idx < results.Length; idx++)
        {
            Assert.Equal(references[idx % fileCount], results[idx]);
        }
    }
}

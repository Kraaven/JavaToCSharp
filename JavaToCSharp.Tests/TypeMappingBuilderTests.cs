using com.github.javaparser;
using com.github.javaparser.ast.expr;
using JavaToCSharp.Expressions;
using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

public class TypeMappingBuilderTests
{
    private static JavaConversionOptions Options(TypeMappingIndex index, bool preserve = true) => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
        PreserveJavaMethodNames = preserve,
        MappingIndex = index,
    };

    private static string Convert(string java, TypeMappingIndex index, bool preserve = true)
        => JavaToCSharpConverter.ConvertText(java, Options(index, preserve)).ConvertedText ?? "";

    private static string ConvertWithWarnings(
        string java,
        TypeMappingIndex index,
        out IReadOnlyList<ConversionWarningEventArgs> warnings)
    {
        var captured = new List<ConversionWarningEventArgs>();
        var result = JavaToCSharpConverter.ConvertText(java, Options(index), null, null, warning: captured.Add);
        warnings = captured;
        return result.ConvertedText ?? "";
    }

    private static string ConvertExpression(string javaExpr, TypeMappingIndex index, bool preserve = true)
    {
        ParseResult parseResult = new JavaParser().parseExpression(javaExpr);
        Expression parsedExpr = parseResult.getResult().FromRequiredOptional<Expression>();
        return ExpressionVisitor.VisitExpression(new ConversionContext(Options(index, preserve)), parsedExpr)?.ToFullString() ?? "";
    }

    private static TypeMappingIndex MathIndex() => new TypeMappingBuilder()
        .AddNamespace("java.lang", "System")
        .AddClass("java.lang.Math", "Math", c => c
            .Rename("abs", 1, "Abs")
            .Rename("pow", 2, "Pow")
            .Template("random", 0, "new Random().NextDouble()", confidence: MappingConfidence.Medium,
                notes: "No direct static C# equivalent; flag for review.")
            .AddField("PI", "PI"))
        .Build();

    // ---------- Programmatic conversion ----------

    [Fact]
    public void Build_Rename_ConvertsMathAbs()
    {
        var index = MathIndex();
        var parsed = Convert("public class Foo { double d = Math.abs(-1.0); }", index);

        Assert.Contains("Math.Abs(-1.0)", parsed);
        Assert.DoesNotContain("Math.abs", parsed);
    }

    [Fact]
    public void Build_TemplateAndProperty_Convert()
    {
        var index = new TypeMappingBuilder()
            .AddClass("java.lang.System", "Console", c => c
                .Property("currentTimeMillis", 0, "UtcNow")
                .Template("nanoTime", 0, "Stopwatch.GetTimestamp()"))
            .Build();

        Assert.Equal("Console.UtcNow", ConvertExpression("System.currentTimeMillis()", index));
        Assert.Equal("Stopwatch.GetTimestamp()", ConvertExpression("System.nanoTime()", index));
    }

    [Fact]
    public void Build_Unsupported_EmitsTodo()
    {
        var index = new TypeMappingBuilder()
            .AddClass("java.lang.System", "Console", c => c
                .Unsupported("exit", 1, notes: "Environment.Exit is an application-level decision."))
            .Build();

        var parsed = Convert("public class Foo { void f() { System.exit(1); } }", index);

        Assert.Contains("TODO(java-to-csharp)", parsed);
        Assert.Contains("Environment.Exit is an application-level decision.", parsed);
        Assert.Contains("System.exit(1)", parsed);
    }

    [Fact]
    public void Build_MediumConfidence_EmitsReview()
    {
        var index = MathIndex();
        var parsed = Convert("public class Foo { double d = Math.random(); }", index);

        Assert.Contains("/* REVIEW(java-to-csharp): medium confidence -", parsed);
        Assert.Contains("new Random().NextDouble()", parsed);
    }

    [Fact]
    public void Build_RoutingChain_ConsoleWriteLine()
    {
        var index = new TypeMappingBuilder()
            .AddNamespace("java.lang", "System")
            .AddNamespace("java.io", "System")
            .AddClass("java.lang.System", "Console", c => c
                .Route("out", MemberMappingType.Field, n => n
                    .AddClass("java.io.PrintStream", "Console", p => p
                        .Rename("println", 1, "WriteLine")
                        .Rename("print", 1, "Write"))))
            .Build();

        Assert.Equal("Console.WriteLine(\"hi\")", ConvertExpression("System.out.println(\"hi\")", index));
        Assert.Equal("Console.Write(\"hi\")", ConvertExpression("System.out.print(\"hi\")", index));
    }

    [Fact]
    public void Build_VariadicTemplate_ExpandsRest()
    {
        var index = new TypeMappingBuilder()
            .AddClass("java.lang.System", "Console", c => c
                .Route("out", MemberMappingType.Field, n => n
                    .AddClass("java.io.PrintStream", "Console", p => p
                        .TemplateVariadic("printf", "$0.Write($rest)"))))
            .Build();

        Assert.Equal("Console.Write(\"{0} {1:D}\", name, 5)", ConvertExpression("System.out.printf(\"%s %d\", name, 5)", index));
    }

    [Fact]
    public void Build_TypeHint_DisambiguatesOverloads()
    {
        var index = new TypeMappingBuilder()
            .AddClass("java.lang.Integer", "Integer", c => c
                .Rename("valueOf", 1, "FromInt32", typeHint: ["int"])
                .Rename("valueOf", 1, "FromInt64", typeHint: ["long"]))
            .Build();

        Assert.Equal("Integer.FromInt32(5)", ConvertExpression("Integer.valueOf(5)", index));
        Assert.Equal("Integer.FromInt64(5)", ConvertExpression("Integer.valueOf(5L)", index));
    }

    [Fact]
    public void Build_Reusable_ReflectsLaterAdditions()
    {
        var builder = new TypeMappingBuilder()
            .AddClass("java.lang.Math", "Math", c => c.Rename("abs", 1, "Abs"));

        var first = builder.Build();
        Assert.True(first.TryGetByQualifiedName("java.lang.Math", out var math));
        Assert.NotNull(math);
        Assert.Single(math.Entries);

        builder.AddClass("java.lang.Math", "Math", c => c.Rename("pow", 2, "Pow"));
        var second = builder.Build();
        Assert.True(second.TryGetByQualifiedName("java.lang.Math", out math));
        Assert.NotNull(math);
        Assert.Equal(2, math.Entries.Count);

        Assert.Single(first.Classes);
    }

    [Fact]
    public void Build_Empty_ProducesUsableIndex()
    {
        var index = new TypeMappingBuilder().Build();

        Assert.Empty(index.Classes);
        Assert.False(index.TryGetByQualifiedName("java.lang.Math", out _));
        Assert.False(index.TryGetCSharpPackages("java.lang", out _));
    }

    // ---------- Mixing YAML and programmatic ----------

    [Fact]
    public void Build_MixedYamlAndProgrammatic_MergesSameRoot()
    {
        const string yaml = """
            namespaces:
              - java_packages: [java.lang]
                csharp_packages: [System]

            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    kind: rename
                    csharp_member: Abs
            """;

        var index = new TypeMappingBuilder()
            .AddYaml(yaml)
            .AddClass("java.lang.Math", "Math", c => c.Rename("pow", 2, "Pow"))
            .Build();

        Assert.True(index.TryGetByQualifiedName("java.lang.Math", out var math));
        Assert.NotNull(math);
        Assert.Equal(2, math.Entries.Count);
        Assert.Contains("Math.Pow(2.0", ConvertExpression("Math.pow(2.0, 3.0)", index));
        Assert.Equal("Math.Abs(-1.0)", ConvertExpression("Math.abs(-1.0)", index));
    }

    [Fact]
    public void Build_MixedYamlAndProgrammatic_DuplicateMemberKey_Throws()
    {
        const string yaml = """
            class_map:
              - java_class: java.lang.Math
                csharp_class: Math
                entries:
                  - java_member: abs
                    arg_count: 1
                    kind: rename
                    csharp_member: Abs
            """;

        var ex = Assert.Throws<MappingValidationException>(() => new TypeMappingBuilder()
            .AddYaml(yaml)
            .AddClass("java.lang.Math", "Math", c => c.Rename("abs", 1, "Absolute"))
            .Build());

        Assert.Contains("duplicate entry", ex.Message);
        Assert.Contains("java.lang.Math.abs", ex.Message);
    }

    [Fact]
    public void Build_ConflictingClassRoots_Throws()
    {
        var ex = Assert.Throws<MappingValidationException>(() => new TypeMappingBuilder()
            .AddYaml("class_map:\n  - java_class: java.lang.Math\n    csharp_class: Math\n")
            .AddClass("java.lang.Math", "Math2")
            .Build());

        Assert.Contains("different csharp_class values", ex.Message);
    }

    // ---------- Validation parity ----------

    [Fact]
    public void Build_InvalidIdentifiers_Throw()
    {
        var invalidMember = Assert.Throws<MappingValidationException>(() => new TypeMappingBuilder()
            .AddClass("java.lang.Math", "Math", c => c.Rename("abs", 1, "foo-bar"))
            .Build());
        Assert.Contains("csharp_member", invalidMember.Message);

        var invalidClass = Assert.Throws<MappingValidationException>(() => new TypeMappingBuilder()
            .AddClass("java.1Math", "Math")
            .Build());
        Assert.Contains("java_class", invalidClass.Message);

        var invalidNamespace = Assert.Throws<MappingValidationException>(() => new TypeMappingBuilder()
            .AddNamespace("java lang", "System")
            .Build());
        Assert.Contains("java_packages", invalidNamespace.Message);
    }

    [Fact]
    public void Build_MissingNamespaceCoverage_Warns()
    {
        var index = new TypeMappingBuilder()
            .AddClass("java.lang.Math", "Math", c => c.Rename("abs", 1, "Abs"))
            .Build();

        var parsed = ConvertWithWarnings("public class Foo { double d = Math.abs(-1.0); }", index, out var warnings);

        Assert.Contains("Math.Abs(-1.0)", parsed);
        Assert.NotEmpty(warnings);
        Assert.Contains(warnings, w => w.Message.Contains("java.lang", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Build_ConflictingNamespaces_Throw()
    {
        var ex = Assert.Throws<MappingValidationException>(() => new TypeMappingBuilder()
            .AddNamespace("java.lang", "System")
            .AddNamespace("java.lang", "System.Text")
            .Build());

        Assert.Contains("conflict", ex.Message);
        Assert.Contains("java.lang", ex.Message);
    }

    [Fact]
    public void Build_SimpleNameAmbiguity_IsDetected()
    {
        var index = new TypeMappingBuilder()
            .AddClass("java.io.Reader", "TextReader")
            .AddClass("java.sql.Reader", "SqlReader")
            .Build();

        Assert.False(index.TryGetBySimpleName("Reader", out _, out var ambiguous));
        Assert.True(ambiguous);
        Assert.True(index.TryGetByQualifiedName("java.io.Reader", out var ioReader));
        Assert.NotNull(ioReader);
        Assert.Equal("TextReader", ioReader.CSharpClass);
    }
}

using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the post-4.0.15 (4.0.16) fixes:
/// — Optional <c>orElseGet</c>/<c>orElseThrow</c> templates parenthesize the supplier so a lambda
///   can never sit directly after <c>??</c> (previously emitted <c>obj ?? ()</c> garbage that
///   truncates mid-statement);
/// — <c>TryFillTemplate</c> rejects any fill that does not parse without diagnostics, falling back
///   to a well-formed inline placeholder instead of a tolerant (partial) parse;
/// — <c>EmitUnsupported</c>/<c>ApplyReview</c> emit inline-safe block comments (never
///   <c>//</c> line comments) so a note can no longer swallow the rest of the source line
///   (e.g. <c>return // TODO...</c>);
/// — an unfillable numbered template argument substitutes <c>new object()</c> instead of leaking
///   the raw <c>$1</c> token.
/// </summary>
public class Round416FixTests
{
    private const string CollectorYaml = """
        class_map:
          - java_class: java.util.stream.Collector
            csharp_class: object
            generic: true
            entries:
              - java_member: broken
                arg_count: 1
                kind: template
                expression: "$0 ?? $1()"
                confidence: medium
                returns_type: GenericType
        """;

    private static JavaConversionOptions Options(TypeMappingIndex? index) => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
        PreserveJavaMethodNames = true,
        MappingIndex = index,
    };

    private static string Convert(string java, TypeMappingIndex? index = null)
        => JavaToCSharpConverter.ConvertText(java, Options(index)).ConvertedText ?? "";

    // ---------- Optional templates: lambda supplier must never follow '??' ----------

    [Fact]
    public void Optional_OrElseGet_LambdaSupplierInvokedInline()
    {
        const string java = """
            public class Foo {
                int go() {
                    java.util.Optional<Integer> opt = java.util.Optional.empty();
                    return opt.orElseGet(() -> 5);
                }
            }
            """;

        var output = Convert(java, BuiltInMappings.SystemCollections);

        Assert.Contains("?? (() => 5).Invoke()", output);
        Assert.DoesNotContain("?? ()", output);
    }

    [Fact]
    public void Optional_OrElseThrow1_LambdaSupplierInvokedInline()
    {
        const string java = """
            public class Foo {
                int go() {
                    java.util.Optional<Integer> opt = java.util.Optional.empty();
                    return opt.orElseThrow(() -> new RuntimeException("boom"));
                }
            }
            """;

        var output = Convert(java, BuiltInMappings.SystemCollections);

        Assert.Contains("?? (", output);
        Assert.Contains(".Invoke()", output);
        Assert.DoesNotContain("?? ()", output);
    }

    // ---------- TryFillTemplate: reject fills that do not parse cleanly ----------

    [Fact]
    public void InvalidlyFilledTemplate_FallsBackToPlaceholder_AndStatementSurvives()
    {
        const string java = """
            public class Foo {
                void go() {
                    java.util.stream.Collector c = null;
                    c.broken(() -> 5);
                    mark();
                }
            }
            """;

        var index = TypeMappingIndex.LoadFromYaml(CollectorYaml);
        var output = Convert(java, index);

        Assert.Contains("*/", output);
        Assert.Contains("new object ()", output);
        Assert.Contains("mark();", output);
    }

    // ---------- EmitUnsupported: block comments cannot truncate a statement ----------

    [Fact]
    public void UnsupportedMember_InlineDoesNotSwallowFollowingExpression()
    {
        const string java = """
            public class Foo {
                int go() {
                    return System.out.println(1) + 2;
                }
            }
            """;

        var output = Convert(java, BuiltInMappings.System);

        Assert.DoesNotContain("return //", output);
        Assert.Contains("+ 2;", output);
    }

    [Fact]
    public void UnsupportedEntry_BlockCommentDoesNotTruncateNextStatement()
    {
        const string java = """
            public class Foo {
                void go() {
                    System.exit(1);
                    mark();
                }
            }
            """;

        var index = TypeMappingIndex.LoadFromYaml("""
            class_map:
              - java_class: java.lang.System
                csharp_class: Console
                entries:
                  - java_member: exit
                    arg_count: 1
                    kind: unsupported
                    notes: "app-level decision."
            """);
        var output = Convert(java, index);

        Assert.Contains("/* TODO(java-to-csharp): no safe mapping for java.lang.System.exit", output);
        Assert.Contains("System.exit(1);", output);
        Assert.Contains("mark();", output);
        Assert.DoesNotContain("// ", output);
    }
}
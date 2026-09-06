using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the post-4.0.14 (4.0.15) fixes:
/// A — keyword-named bound variables in foreach/using are escaped (namespace, is, out, …);
/// B — <c>object&lt;...&gt;</c> inheritance/creation collapses to <c>object</c> on the
///     <c>GetSyntaxFromType</c> path (previously only the ConvertType path was normalized);
/// I — Java intersection casts <c>(A &amp; B)value</c> reduce to the first type;
/// H — bare <c>extends</c>/<c>super</c> bound markers inside generic arguments are erased;
/// C — <c>string.Format</c> templates no longer emit a dangling comma for empty <c>$rest</c>.
/// </summary>
public class Round415FixTests
{
    private const string CollectorYaml = """
        namespaces:
          - java_packages: [java.util.stream]
            csharp_packages: [System.Linq]

        class_map:
          - java_class: java.util.stream.Collector
            csharp_class: object
            generic: true
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

    // ---------- A: keyword-named bound variables ----------

    [Fact]
    public void KeywordBoundVar_ForEachDeclaratorIsEscaped()
    {
        const string java = """
            public class Thing {
                void go(String[] namespace) {
                    for (String namespace : namespace) {
                    }
                }
            }
            """;

        var output = Convert(java);

        Assert.Contains("foreach (string @namespace in @namespace)", output);
    }

    [Fact]
    public void KeywordBoundVar_ContextualKeywordIsEscaped()
    {
        const string java = """
            public class Thing {
                void go(String[] is) {
                    for (String is : is) {
                    }
                }
            }
            """;

        var output = Convert(java);

        Assert.Contains("foreach (string @is in @is)", output);
        Assert.DoesNotContain("foreach (string is in", output);
    }

    [Fact]
    public void KeywordBoundVar_UsingDeclaratorIsEscaped()
    {
        const string java = """
            public class Thing {
                void go() throws Exception {
                    try (java.lang.AutoCloseable out = null) {
                    }
                }
            }
            """;

        var output = Convert(java);

        Assert.Contains("@out", output);
    }

    // ---------- B: object<T> on the GetSyntaxFromType path ----------

    [Fact]
    public void MappedObjectType_InheritanceCollapsesGenericArguments()
    {
        const string java = """
            import java.util.stream.Collector;
            import java.util.Map;

            interface PlayerListComponent extends Collector<String, Object, Map<String, Integer>> {
            }
            """;

        var index = TypeMappingIndex.LoadFromYaml(CollectorYaml);
        var output = Convert(java, index);

        Assert.DoesNotContain("object<", output);
        Assert.Contains(": object", output);
    }

    // ---------- I: intersection casts ----------

    [Fact]
    public void IntersectionCast_ReducesToFirstType()
    {
        const string java = """
            public class Thing {
                Object go(Object e) {
                    return (Entity & Leashable) e;
                }
            }
            """;

        var output = Convert(java);

        Assert.Contains("(Entity)e", output);
        Assert.DoesNotContain("& Leashable", output);
    }

    // ---------- H: bare extends/super bounds ----------

    [Theory]
    [InlineData("Registry<extends T>", "Registry<T>")]
    [InlineData("EntityModel<super S>", "EntityModel<S>")]
    [InlineData("Registry< A, extends T >", "Registry<A,T>")]
    [InlineData("? extends T", "T")]
    public void BareTypeBounds_InGenericArgumentsAreErased(string input, string expected)
    {
        var actual = TypeHelper.ConvertType(input).Replace(" ", "");

        Assert.Equal(expected, actual);
    }

    // ---------- C: string.Format empty $rest ----------

    [Fact]
    public void StringFormat_SingleArgumentHasNoTrailingComma()
    {
        const string java = """
            public class Thing {
                String one() {
                    return String.format("Hello");
                }

                String two() {
                    return String.format("%s-%d", "x", 1);
                }
            }
            """;

        var output = Convert(java, BuiltInMappings.System);

        Assert.Contains("string.Format(\"Hello\")", output);
        Assert.Contains("string.Format(", output);
        Assert.Contains("\"x\", 1)", output);
        Assert.DoesNotContain(", )", output);
        Assert.DoesNotContain(",, ", output);
    }
}
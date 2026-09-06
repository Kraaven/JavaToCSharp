using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the post-4.0.13 fixes:
/// K — keyword method names are escaped at the declaration site (<c>is</c> → <c>@is</c>);
/// O — classes mapped to <c>object</c> never carry bogus generic arguments (<c>object&lt;...&gt;</c>);
/// D — C# keyword segments inside dotted type names are escaped (<c>base</c> → <c>@base</c>).
/// </summary>
public class Round413FixTests
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

    // ---------- K: keyword method-name declaration escaping ----------

    [Fact]
    public void KeywordMethodName_IsEscapedInClassDeclaration()
    {
        const string java = """
            public class Thing {
                public boolean is(String s) {
                    return s != null;
                }
            }
            """;

        var output = Convert(java);

        Assert.Contains("@is(string s)", output);
        Assert.DoesNotContain("bool is(", output);
    }

    [Fact]
    public void KeywordMethodName_IsEscapedInInterfaceDeclaration()
    {
        const string java = """
            public interface Thing {
                boolean is(String s);
            }
            """;

        var output = Convert(java);

        Assert.Contains("@is(", output);
        Assert.DoesNotContain("bool is(", output);
    }

    [Fact]
    public void KeywordMethodName_EscapedDeclarationMatchesEscapedCallSite()
    {
        const string java = """
            public class Thing {
                public boolean is(String s) {
                    return this.is(s + "x");
                }
            }
            """;

        var output = Convert(java);

        Assert.Contains("@is(string s)", output);
        Assert.Contains("this.@is(", output);
    }

    // ---------- O: object-generic collapse ----------

    [Fact]
    public void MappedObjectType_CollapsesTrailingGenericArguments()
    {
        const string java = """
            import java.util.stream.Collector;

            public class Thing {
                public Collector<String, Object, Map<String, Integer>> toMap() {
                    return null;
                }
            }
            """;

        var index = TypeMappingIndex.LoadFromYaml(CollectorYaml);
        var output = Convert(java, index);

        Assert.DoesNotContain("object<", output);
        Assert.Contains("object toMap()", output);
    }

    // ---------- D: dotted-name keyword segments ----------

    [Fact]
    public void DottedTypeName_KeywordSegmentIsEscaped()
    {
        const string java = """
            public class Thing {
                public com.google.common.base.Function<V1, V2> transform() {
                    return null;
                }
            }
            """;

        var output = Convert(java);

        Assert.Contains("com.google.common.@base.Function<V1, V2>", output);
        Assert.DoesNotContain(". base .", output);
    }
}
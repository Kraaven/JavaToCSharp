using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Variable-scope tracking correctness for mapping resolution:
/// foreach loop variables and for-init variables are declared only inside their loop scope,
/// never leaking into the enclosing method scope where they could shadow an outer variable's
/// declared type and corrupt variable-receiver mapping resolution.
/// </summary>
public class VariableScopeTests
{
    private static JavaConversionOptions Options(TypeMappingIndex index) => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
        PreserveJavaMethodNames = true,
        MappingIndex = index,
    };

    private static string Convert(string java, TypeMappingIndex index)
        => JavaToCSharpConverter.ConvertText(java, Options(index)).ConvertedText ?? "";

    private static readonly string TwoMemberYaml = """
        namespaces:
          - java_packages: [java.lang]
            csharp_packages: [System]
        class_map:
          - java_class: java.lang.String
            csharp_class: string
            entries:
              - java_member: length
                arg_count: 0
                kind: property
                csharp_member: Length
          - java_class: java.lang.StringBuilder
            csharp_class: StringBuilder
            entries:
              - java_member: length
                arg_count: 0
                kind: property
                csharp_member: Length
        """;

    [Fact]
    public void ForEachVariable_DoesNotLeakIntoEnclosingScope()
    {
        // Two mapped classes provide "length", so heuristic inference cannot resolve the
        // post-loop call — only a leaked loop-variable declaration could. The in-loop use sits
        // inside an if-condition so its property form ("sb.Length") can never collide with the
        // post-loop bare-statement assertion.
        const string java = """
            import java.util.List;

            public class Foo {
                void go(java.util.List<String> list) {
                    for (String sb : list) {
                        if (sb.length() > 0) {
                            mark();
                        }
                    }
                    sb.length();
                }
            }
            """;

        var output = Convert(java, TypeMappingIndex.LoadFromYaml(TwoMemberYaml));

        // Inside the loop the declared type resolves through the mapping (property, no parens).
        Assert.Contains("if (sb.Length > 0)", output);
        // After the loop the variable is out of scope: default conversion keeps the call form.
        Assert.Contains("sb.length();", output);
        Assert.DoesNotContain("sb.Length;", output);
    }

    [Fact]
    public void ForInitVariable_DoesNotLeakIntoEnclosingScope()
    {
        const string java = """
            public class Foo {
                void go() {
                    for (StringBuilder sb = new StringBuilder(); sb.length() > 0; ) {
                    }
                    sb.length();
                }
            }
            """;

        var output = Convert(java, TypeMappingIndex.LoadFromYaml(TwoMemberYaml));

        // Post-loop the for-init variable is out of scope: default conversion keeps parens.
        Assert.Contains("sb.length();", output);
        Assert.DoesNotContain("sb.Length;", output);
    }
}

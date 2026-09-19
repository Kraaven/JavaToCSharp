using JavaToCSharp;
using JavaToCSharp.TypeMapping;
using Xunit;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the post-4.0.17 (4.0.18) fixes:
/// - member-name heuristic inference must not hijack calls whose receiver is a variable with a
///   known but unmapped declared type (e.g. a <c>DamageSource.is(TagKey)</c> being resolved
///   against a mapped <c>CharMatcher.is</c> template);
/// - method references whose scope is a variable wrapped in a javaparser <c>TypeExpr</c>
///   (e.g. <c>out::add</c>) are treated as bound references, escaped correctly, and recover
///   arity/mapped name from the mapping index;
/// - type names declared in the current file (nested records/classes named like primitives or
///   wrappers, e.g. a nested record <c>Double&lt;S&gt;</c>) are no longer mapped to C# keywords.
/// </summary>
public class Round418FixTests
{
    private const string CharMatcherYaml = """
        class_map:
          - java_class: com.google.common.base.CharMatcher
            csharp_class: object
            kind: class
            entries:
              - java_member: is
                arg_count: 1
                kind: template
                expression: "c => c == $1"
                confidence: low
        """;

    private const string ListYaml = """
        namespaces:
          - java_packages: [java.util]
            csharp_packages: [System.Collections.Generic]
        class_map:
          - java_class: java.util.List
            csharp_class: IList
            kind: interface
            generic: true
            entries:
              - java_member: add
                arg_count: 1
                kind: rename
                csharp_member: Add
        """;

    private static string Convert(string java, TypeMappingIndex? index = null)
        => JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeComments = false,
            MappingIndex = index,
        }).ConvertedText ?? "";

    // ---------- Heuristic inference guard ----------

    [Fact]
    public void MemberInference_DoesNotHijack_VariableOfUnmappedType()
    {
        const string java = """
            public class Living {
                public boolean check(DamageSource source) {
                    return !source.is(SomeTags.NO_IMPACT);
                }
            }
            """;

        var result = Convert(java, TypeMappingIndex.LoadFromYaml(CharMatcherYaml));

        // The call must be emitted as a plain (escaped) method call on the receiver, not
        // rewritten into the CharMatcher.is lambda template.
        Assert.Contains("source.@is(SomeTags.NO_IMPACT)", result);
        Assert.DoesNotContain("c => c ==", result);
    }

    [Fact]
    public void MemberInference_DoesNotHijack_CamelCaseRoot()
    {
        const string java = """
            public class Living {
                public boolean check() {
                    return someHelper.is(SomeTags.NO_IMPACT);
                }
            }
            """;

        var result = Convert(java, TypeMappingIndex.LoadFromYaml(CharMatcherYaml));

        Assert.Contains("someHelper.@is(", result);
        Assert.DoesNotContain("c => c ==", result);
    }

    // ---------- out::add bound method reference ----------

    [Fact]
    public void MethodRef_VariableScopeWrappedInTypeExpr_IsBound()
    {
        const string java = """
            import java.util.List;
            import java.util.function.Consumer;

            public class A {
                public void m(List<String> out) {
                    Consumer<String> r = out::add;
                }
            }
            """;

        var result = Convert(java, TypeMappingIndex.LoadFromYaml(ListYaml));

        Assert.Contains("=> @out.Add(", result);
        Assert.DoesNotContain("out.add()", result);
    }

    // ---------- Declared type names vs built-in conversions ----------

    [Fact]
    public void NestedTypeNamedDouble_IsNotMappedToKeyword()
    {
        const string java = """
            public class Combiner {
                public record Double<S>(S first, S second) { }

                public Double<String> make(String first, String second) {
                    return new Double<>(first, second);
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("new Double(", result);
        Assert.DoesNotContain("new double", result);
    }

    [Fact]
    public void NestedTypeNamedDouble_IsNotMappedToKeyword_EvenWhenDeclaredAfterUse()
    {
        // The referencing method is declared BEFORE the nested record, so the type name must be
        // known from a pre-pass rather than from visit order.
        const string java = """
            public class Combiner {
                public Double<String> make(String first, String second) {
                    return new Double<>(first, second);
                }

                public record Double<S>(S first, S second) { }
            }
            """;

        var result = Convert(java);

        Assert.Contains("new Double(", result);
        Assert.DoesNotContain("new double", result);
        Assert.Contains("Double<string> make", result);
    }
}

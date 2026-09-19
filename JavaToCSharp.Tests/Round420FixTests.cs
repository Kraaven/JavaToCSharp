using JavaToCSharp;
using JavaToCSharp.TypeMapping;
using Xunit;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the post-4.0.20 (4.0.21) fixes:
/// - <c>Predicate&lt;String&gt;</c> no longer converts to the ill-formed
///   <c>Func&lt;bool&gt; &lt; string &gt;</c> (the "Func&lt;bool&gt;" simple-name substitution
///   injected a stray type argument ahead of the original ones); it now stays
///   <c>Predicate&lt;string&gt;</c>, and <c>BiPredicate&lt;A, B&gt;</c> re-aris to
///   <c>Func&lt;A, B, bool&gt;</c>;
/// - unbound method references to mapped types (<c>Pattern::asPredicate</c>) resolve through the
///   mapping index and emit the mapped template with the receiver as the first lambda parameter,
///   instead of the broken parameterless fallback (<c>Regex.asPredicate()</c>).
/// </summary>
public class Round420FixTests
{
    private const string PatternYaml = """
        namespaces:
          - java_packages: [java.util.regex]
            csharp_packages: [System.Text.RegularExpressions]
        class_map:
          - java_class: java.util.regex.Pattern
            csharp_class: Regex
            kind: class
            entries:
              - java_member: asPredicate
                arg_count: 0
                kind: template
                expression: "new Predicate<string>(s => $0.IsMatch(s))"
                confidence: medium
        """;

    private const string OptionalYaml = """
        namespaces:
          - java_packages: [java.util]
            csharp_packages: [System.Collections.Generic]
        class_map:
          - java_class: java.util.Optional
            csharp_class: Optional
            kind: class
            generic: true
            entries:
              - java_member: map
                arg_count: 1
                kind: rename
                csharp_member: Map
                returns_type: GenericType
              - java_member: orElse
                arg_count: 1
                kind: rename
                csharp_member: OrElse
                returns_type: GenericType
        """;

    private const string PredicateYaml = """
        namespaces:
          - java_packages: [java.util.function]
            csharp_packages: [System]
        class_map:
          - java_class: java.util.function.Predicate
            csharp_class: Predicate
            kind: interface
            generic: true
            entries:
              - java_member: test
                arg_count: 1
                kind: template
                expression: "$0($1)"
        """;

    private static TypeMappingIndex CreateIndex()
        => TypeMappingIndex.LoadFromSources(
        [
            ("pattern", PatternYaml),
            ("optional", OptionalYaml),
            ("predicate", PredicateYaml),
        ]);

    private static string Convert(string java, TypeMappingIndex? index = null)
        => JavaToCSharpConverter.ConvertText(java, new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeComments = false,
            MappingIndex = index,
        }).ConvertedText ?? "";

    // ---------- Predicate type conversion ----------

    [Fact]
    public void PredicateType_KeepsArity_InsteadOfInjectingBool()
    {
        Assert.Equal("Predicate<string>", TypeHelper.ConvertType("Predicate<String>"));
    }

    [Fact]
    public void PredicateType_WithMappedArgument_ComposesArgument()
    {
        Assert.Equal("Predicate<IList<string>>", TypeHelper.ConvertType("Predicate<List<String>>"));
    }

    [Fact]
    public void BiPredicateType_ReariesToFuncWithBoolResult()
    {
        Assert.Equal("Func<string, string, bool>", TypeHelper.ConvertType("BiPredicate<String, String>"));
    }

    [Fact]
    public void BiPredicateType_Nested_IsRewrittenRecursively()
    {
        Assert.Equal("IList<Func<string, string, bool>>", TypeHelper.ConvertType("List<BiPredicate<String, String>>"));
    }

    // ---------- IdentifierPattern-style chain ----------

    [Fact]
    public void MethodReference_OnMappedType_UsesMappedTemplate()
    {
        const string java = """
            import java.util.Optional;
            import java.util.function.Predicate;
            import java.util.regex.Pattern;

            public class IdentifierPattern {
                private final Predicate<String> namespacePredicate;
                private final Optional<Pattern> namespacePattern;

                private IdentifierPattern(Optional<Pattern> namespacePattern) {
                    this.namespacePattern = namespacePattern;
                    this.namespacePredicate = namespacePattern.<Predicate<String>>map(Pattern::asPredicate).orElse(r -> true);
                }
            }
            """;

        var result = Convert(java, CreateIndex());

        // The explicit type witness must survive as a valid generic argument.
        Assert.Contains("Map<Predicate<string>>", result);
        // The method reference must become the mapped template with a receiver lambda parameter.
        Assert.Contains("new Predicate<string>(s => mrefArg1.IsMatch(s))", result);
        // Neither the mangled type argument nor the unresolved fallback may appear.
        Assert.DoesNotContain("Func<bool>", result);
        Assert.DoesNotContain("Regex.asPredicate()", result);
    }

    [Fact]
    public void MethodReference_OnMappedType_RenameEntry_InvokesReceiverParameter()
    {
        const string java = """
            import java.util.function.Supplier;

            public class A {
                public Supplier<Boolean> m() {
                    return Boolean::getBoolean;
                }
            }
            """;

        const string yaml = """
            class_map:
              - java_class: java.lang.Boolean
                csharp_class: bool
                kind: class
                entries:
                  - java_member: getBoolean
                    arg_count: 1
                    kind: rename
                    csharp_member: Parse
            """;

        var result = Convert(java, TypeMappingIndex.LoadFromYaml(yaml));

        // Unbound instance-method reference: receiver becomes the first lambda parameter and the
        // renamed member is invoked on it with the remaining forwarded arguments.
        Assert.Contains("(mrefArg1, mrefArg2) => mrefArg1.Parse(mrefArg2)", result);
        Assert.DoesNotContain("bool.getBoolean", result);
    }

    // ---------- Raw generic type-argument synthesis ----------

    [Fact]
    public void RawGeneric_InProjectType_GetsSynthesizedArguments()
    {
        const string java = """
            import java.util.List;

            public class Holder {
                public Holder m(Holder other) {
                    return other;
                }
            }
            """;

        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeComments = false,
        };
        options.KnownGenericArity = new Dictionary<string, int> { ["Holder"] = 1 };

        var result = JavaToCSharpConverter.ConvertText(java, options).ConvertedText ?? "";

        Assert.Contains("Holder<object> m(Holder<object>", result);
    }

    [Fact]
    public void RawGeneric_BclType_GetsSynthesizedArguments()
    {
        const string java = """
            import java.util.Map;

            public class A {
                public Map m() {
                    return null;
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("Dictionary<object, object> m()", result);
    }

    [Fact]
    public void GenericType_WithExistingArguments_IsLeftAlone()
    {
        const string java = """
            import java.util.List;

            public class A {
                public List<String> m() {
                    return null;
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("List<string> m()", result);
        Assert.DoesNotContain("List<object>", result);
    }

    // ---------- Nested qualified type names ----------

    [Fact]
    public void NestedTypeReference_KeepsQualifyingScope()
    {
        const string java = """
            import com.mojang.datafixers.DSL;

            public class DataFixer {
                public void m(DSL.TypeReference reference) {
                }
            }
            """;

        const string yaml = """
            namespaces:
              - java_packages: [com.mojang.datafixers]
                csharp_packages: [MinecraftDotNet.DataFixers]
            class_map:
              - java_class: com.mojang.datafixers.DSL
                csharp_class: DSL
                kind: class
            """;

        var result = Convert(java, TypeMappingIndex.LoadFromYaml(yaml));

        // The qualifier must survive: the nested type is declared inside DSL, so a bare
        // "TypeReference" does not resolve in the referencing file.
        Assert.Contains("DSL.TypeReference", result);
        Assert.DoesNotContain("void m(TypeReference", result);
    }
}

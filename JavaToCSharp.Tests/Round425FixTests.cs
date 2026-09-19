using JavaToCSharp;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the 4.0.25 fixes:
/// - <c>csharp_class</c> targets support positional <c>$N</c> placeholders; the last
///   placeholder-bearing token absorbs all remaining Java type arguments
///   (<c>Dictionary&lt;$1&gt;</c> over <c>Map&lt;K, V&gt;</c>, <c>Dictionary&lt;long, $1&gt;</c>
///   over <c>Long2ObjectMap&lt;V&gt;</c>, <c>Func&lt;$1, bool&gt;</c> over
///   <c>BiPredicate&lt;A, B&gt;</c>), with raw usage filling unfilled positions with object;
/// - nested references to types declared in OTHER files of the host tree keep their qualifier
///   when the host feeds <see cref="JavaConversionOptions.KnownTypeNames"/>;
/// - anonymous classes become generic over the enclosing type parameters their bodies reference;
/// - enum constructors disambiguate the synthetic name/ordinal parameters only when the user's
///   own constructor declares a colliding parameter name (trailing-underscore suffix; 4.0.26
///   replaced the earlier dollar-prefixed spelling, which is not a legal C# identifier);
/// - references to types declared in the same file convert their ARGUMENTS
///   (<c>Codec&lt;Integer&gt;</c> → <c>Codec&lt;int&gt;</c>);
/// - protected nested types promote to public (Java's package-wide reachability).
/// </summary>
public class Round425FixTests
{
    private const string MappingYaml = """
        namespaces:
          - java_packages: [java.util]
            csharp_packages: [System.Collections.Generic]
        class_map:
          - java_class: java.util.Map
            csharp_class: Dictionary
            kind: interface
            generic: true
          - java_class: java.util.function.BiPredicate
            csharp_class: 'Func<$1, bool>'
            kind: interface
            generic: true
        """;

    private static string Convert(string java, Action<JavaConversionOptions>? configure = null)
    {
        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeComments = false,
            MappingIndex = JavaToCSharp.TypeMapping.TypeMappingIndex.LoadFromSources([("test", MappingYaml)]),
        };
        configure?.Invoke(options);
        return JavaToCSharpConverter.ConvertText(java, options).ConvertedText ?? "";
    }

    private static void AssertParseable(string csharp)
    {
        var errors = CSharpSyntaxTree.ParseText(csharp).GetDiagnostics()
            .Where(d => d.Severity == Microsoft.CodeAnalysis.DiagnosticSeverity.Error)
            .ToList();

        Assert.True(errors.Count == 0, "Converted output has parse errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    // ---------- $N placeholders ----------

    [Fact]
    public void ClassTarget_LastPlaceholderAbsorbsAllArguments()
    {
        const string java = """
            import java.util.Map;
            public class Store {
                private Map<String, Integer> counts;
            }
            """;

        var result = Convert(java);

        Assert.Contains("Dictionary<string, int>", result);
    }

    [Fact]
    public void ClassTarget_FixedPrefixWithPlaceholder()
    {
        const string java = """
            public class Registry {
                private java.util.function.BiFunction<String, Integer, Long> lookup;
            }
            """;

        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeComments = false,
            MappingIndex = JavaToCSharp.TypeMapping.TypeMappingIndex.LoadFromSources([("test", """
                class_map:
                  - java_class: java.util.function.BiFunction
                    csharp_class: 'Func<$1, $2>'
                    kind: interface
                    generic: true
                """)]),
        };
        var result = JavaToCSharpConverter.ConvertText(java, options).ConvertedText ?? "";

        Assert.Contains("Func<string, int, long>", result);
    }

    [Fact]
    public void ClassTarget_BiPredicateTemplateReArisWithBoolResult()
    {
        const string java = """
            import java.util.function.BiPredicate;
            public class Filter {
                private BiPredicate<String, Integer> match;
            }
            """;

        var result = Convert(java);

        Assert.Contains("Func<string, int, bool>", result);
    }

    // ---------- KnownTypeNames qualifier retention ----------

    [Fact]
    public void NestedReference_KeepsQualifier_WhenScopeIsKnownTypeName()
    {
        const string java = """
            public class BeeRenderer implements Renderer.SimpleMarker {
                public int count() { return 1; }
            }
            """;

        var result = Convert(java, o => o.KnownTypeNames = new HashSet<string> { "renderer" });

        // Renderer is not declared in this file; without KnownTypeNames the qualifier would be
        // dropped and the base clause would reference bare SimpleMarker.
        Assert.Contains("Renderer.SimpleMarker", result);
    }

    // ---------- Anonymous classes over enclosing type parameters ----------

    [Fact]
    public void AnonymousClass_BecomesGenericOverReferencedEnclosingTypeParameter()
    {
        const string java = """
            public class Codec<A> {
                public Codec<A> cached() {
                    return new Codec<A>() {
                        public A decode(String input) { return null; }
                    };
                }
            }
            """;

        var result = Convert(java);

        // The hoisted class declares the enclosing parameter and the usage passes it through.
        Assert.Contains("class AnonymousCodec<A> : Codec<A>", result);
        Assert.Contains("new AnonymousCodec<A>", result);
    }

    // ---------- Enum synthetic-parameter disambiguation ----------

    [Fact]
    public void EnumCtor_UserNameCollision_AppendsUnderscoreToSynthetic()
    {
        const string java = """
            public enum OctahedralGroup {
                IDENTITY(new boolean[] { false, false, false });

                private final boolean[] flag;

                OctahedralGroup(String name, boolean[] flag) {
                    this.flag = flag;
                }
            }
            """;

        var result = Convert(java);

        // The synthetic parameter must not collide with the user's own `name` parameter,
        // and must be a legal C# identifier (no `$`-prefixed identifiers).
        Assert.DoesNotContain("string name, int ordinal, string name", result);
        Assert.Contains("string name_, int ordinal, string name", result);
        Assert.DoesNotContain("$name", result);
        AssertParseable(result);
    }

    [Fact]
    public void EnumCtor_UserParamShadowsBothSuffixes_DisambiguatesIteratively()
    {
        const string java = """
            public enum Weird {
                ONLY(1);

                private final int v;

                Weird(String name, String name_, int v) {
                    this.v = v;
                }
            }
            """;

        var result = Convert(java);

        // Both `name` and `name_` are taken by user parameters, so the synthetic steps to `name__`.
        Assert.Contains("string name__, int ordinal, string name, string name_", result);
        AssertParseable(result);
    }

    [Fact]
    public void EnumCtor_NoCollision_KeepsCleanNames()
    {
        const string java = """
            public enum Color {
                RED(0xFF0000);

                private final int rgb;

                Color(int rgb) {
                    this.rgb = rgb;
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("string name, int ordinal, int rgb", result);
    }

    // ---------- Declared-type argument conversion ----------

    [Fact]
    public void DeclaredType_ArgumentsConvert_InsideDeclaringFile()
    {
        const string java = """
            public class Codec<A> {
                public static Codec<Integer> intRange() { return null; }
            }
            """;

        var result = Convert(java);

        Assert.Contains("Codec<int>", result);
        Assert.DoesNotContain("Codec<Integer>", result);
    }

    // ---------- Protected nested types ----------

    [Fact]
    public void ProtectedNestedType_PromotesToPublic()
    {
        const string java = """
            public class Tab {
                protected interface Output { void append(); }
            }
            """;

        var result = Convert(java);

        Assert.Contains("public interface Output", result);
        Assert.DoesNotContain("protected interface Output", result);
    }
}

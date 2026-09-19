using JavaToCSharp;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Xunit;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the 4.0.23 fixes:
/// - hoisted anonymous classes get plain-identifier names (no synthesized <c>&lt;...&gt;</c> in the
///   identifier, no counter spliced after <c>&gt;</c>), with the full typed base list on the base clause;
/// - anonymous classes in static member contexts (static fields, static methods, interface constants)
///   no longer receive a parent field/constructor or a <c>(this)</c> argument;
/// - anonymous classes created inside interfaces are nested in the interface (the pending-type queue
///   is drained there) instead of leaking into the next converted type of the file;
/// - Java interface fields (implicitly public static final) convert to static get-only properties,
///   eliminating CS0525 for interface constants.
/// </summary>
public class Round423FixTests
{
    private static string Convert(string java, Action<JavaConversionOptions>? configure = null)
    {
        var options = new JavaConversionOptions
        {
            IncludeNamespace = false,
            IncludeComments = false,
        };
        configure?.Invoke(options);
        return JavaToCSharpConverter.ConvertText(java, options).ConvertedText ?? "";
    }

    private static void AssertParses(string csharp)
    {
        var tree = CSharpSyntaxTree.ParseText(csharp);
        var errors = tree.GetDiagnostics()
            .Where(d => d.Severity == DiagnosticSeverity.Error)
            .Select(d => d.ToString())
            .ToList();

        Assert.True(errors.Count == 0, "Converted output has parse errors:" + Environment.NewLine + string.Join(Environment.NewLine, errors));
    }

    // ---------- Hoisted class identifiers ----------

    [Fact]
    public void AnonymousClass_IdentifierIsPlainName_EvenWhenTypeArityIsKnown()
    {
        const string java = """
            public class Foo {
                private Object make() {
                    return new Predicate<String>() {
                        public boolean test(String value) { return value.isEmpty(); }
                    };
                }
            }
            """;

        var result = Convert(java, o => o.KnownGenericArity = new Dictionary<string, int> { ["Predicate"] = 1 });

        // The identifier must be a valid C# identifier; the typed base list carries the args.
        Assert.Contains("class AnonymousPredicate", result);
        Assert.DoesNotContain("AnonymousPredicate<", result);
        AssertParses(result);
    }

    [Fact]
    public void AnonymousClass_CounterSuffixes_AreUniqueAndPlain()
    {
        const string java = """
            public class Foo {
                private final Runnable first = new Runnable() { public void run() { } };
                private final Runnable second = new Runnable() { public void run() { } };
            }
            """;

        var result = Convert(java);

        Assert.Contains("class AnonymousRunnable :", result);
        Assert.Contains("class AnonymousRunnable1 :", result);
        AssertParses(result);
    }

    // ---------- Parent capture vs static context ----------

    [Fact]
    public void AnonymousClass_InInstanceMethod_CapturesParent()
    {
        const string java = """
            public class Foo {
                public Runnable make() {
                    return new Runnable() { public void run() { } };
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("AnonymousRunnable(Foo parent)", result);
        Assert.Contains("new AnonymousRunnable(this)", result);
        AssertParses(result);
    }

    [Fact]
    public void AnonymousClass_InStaticField_HasNoParentCapture()
    {
        const string java = """
            public class Foo {
                private static final Runnable R = new Runnable() { public void run() { } };
            }
            """;

        var result = Convert(java);

        Assert.DoesNotContain("new AnonymousRunnable(this)", result);
        Assert.DoesNotContain("parent", result);
        AssertParses(result);
    }

    [Fact]
    public void AnonymousClass_InStaticMethod_HasNoParentCapture()
    {
        const string java = """
            public class Foo {
                public static Runnable make() {
                    return new Runnable() { public void run() { } };
                }
            }
            """;

        var result = Convert(java);

        Assert.DoesNotContain("new AnonymousRunnable(this)", result);
        Assert.DoesNotContain("AnonymousRunnable(Foo parent)", result);
        AssertParses(result);
    }

    // ---------- Anonymous classes inside interfaces ----------

    [Fact]
    public void AnonymousClass_InInterfaceConstant_NestsInsideInterface()
    {
        const string java = """
            public interface Codec {
                Codec IDENTITY = new Codec() {
                    public String encode(Object value) { return String.valueOf(value); }
                };

                String encode(Object value);
            }
            """;

        var result = Convert(java);

        // The constant is a static get-only property, the hoisted class nests in the interface,
        // and the static context means no `this` argument.
        Assert.Contains("public static Codec IDENTITY =>", result);
        Assert.Contains("class AnonymousCodec", result);
        Assert.DoesNotContain("new AnonymousCodec(this)", result);
        AssertParses(result);
    }

    // ---------- Interface fields (CS0525) ----------

    [Fact]
    public void InterfaceField_BecomesStaticGetOnlyProperty()
    {
        const string java = """
            public interface Settings {
                int MAX_SIZE = 42;
                String NAME = "default";
            }
            """;

        var result = Convert(java);

        Assert.Contains("public static int MAX_SIZE => 42", result);
        Assert.Contains("public static string NAME => \"default\"", result);
        AssertParses(result);
    }

    [Fact]
    public void InterfaceField_ConstantForwardReference_ParsesViaLazyGetter()
    {
        const string java = """
            public interface Settings {
                int BASE = 10;
                int DERIVED = BASE * 2;
            }
            """;

        var result = Convert(java);

        Assert.Contains("public static int DERIVED => BASE * 2", result);
        AssertParses(result);
    }
}

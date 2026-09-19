using JavaToCSharp;
using Xunit;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the 4.0.24 fixes:
/// - bare wildcards resolve through <see cref="JavaConversionOptions.GenericConstraintIndex"/>
///   (nested generics resolve against their own owner), eliminating CS0311 for constrained
///   generic types used with <c>?&gt;</c>;
/// - <see cref="OverrideMode.AlwaysHide"/> emits a plain virtual declaration for @Override
///   methods (Faithful — the default — keeps emitting <c>override</c>);
/// - Java package-private instance methods promote to <c>internal</c> (C# would default them to
///   private, breaking virtual/override chains — CS0621/CS0115);
/// - top-level package-private types promote to public (CS0050/CS0051/CS0060);
/// - field/method same-name collisions deconflict by renaming the field (CS0102) with
///   declaration, bare, and this.-qualified references rewritten.
/// </summary>
public class Round424FixTests
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

    // ---------- Bare wildcards through the constraint index ----------

    [Fact]
    public void BareWildcard_ResolvesThroughConstraintIndex()
    {
        const string java = """
            public class Inventory {
                private Box<?> box;
            }
            """;

        var result = Convert(java, o => o.GenericConstraintIndex =
            new Dictionary<string, IReadOnlyList<string>> { ["Box"] = ["Widget"] });

        Assert.Contains("Box<Widget>", result);
        Assert.DoesNotContain("Box<object>", result);
    }

    [Fact]
    public void BareWildcard_NestedOwnerResolvesAgainstItsOwnBounds()
    {
        const string java = """
            public class Codec {
                private Pair<Codec<?>, String> pair;
            }
            """;

        var result = Convert(java, o => o.GenericConstraintIndex =
            new Dictionary<string, IReadOnlyList<string>> { ["Codec"] = ["Widget"] });

        // The wildcard belongs to Codec (the nested owner), not to Pair.
        Assert.Contains("Pair<Codec<Widget>, string>", result);
    }

    [Fact]
    public void BareWildcard_WithoutIndex_FallsBackToObject()
    {
        const string java = """
            public class Inventory {
                private Box<?> box;
            }
            """;

        var result = Convert(java);

        Assert.Contains("Box<object>", result);
    }

    // ---------- OverrideMode ----------

    [Fact]
    public void OverrideMode_Faithful_EmitsOverrideKeyword()
    {
        const string java = """
            public class Child extends Base {
                @Override
                public void run() { }
            }
            """;

        var result = Convert(java);

        Assert.Contains("public override void run()", result);
    }

    [Fact]
    public void OverrideMode_AlwaysHide_EmitsPlainVirtual()
    {
        const string java = """
            public class Child extends Base {
                @Override
                public void run() { }
            }
            """;

        var result = Convert(java, o => o.OverrideMode = OverrideMode.AlwaysHide);

        Assert.Contains("public virtual void run()", result);
        Assert.DoesNotContain("override void run()", result);
    }

    // ---------- Access promotion ----------

    [Fact]
    public void PackagePrivateMethod_PromotesToPublic()
    {
        const string java = """
            public class Foo {
                void bar() { }
            }
            """;

        // 4.0.31: promoted to public (was internal). Cross-file override chains need base and
        // derived declarations to agree on the same access level deterministically; an internal
        // base vs public derived pair is CS0507.
        var result = Convert(java);

        Assert.Contains("public virtual void bar()", result);
    }


    [Fact]
    public void PackagePrivateTopLevelClass_PromotesToPublic()
    {
        const string java = """
            class Helper {
                public int size() { return 1; }
            }
            """;

        var result = Convert(java);

        Assert.Contains("public class Helper", result);
    }

    // ---------- Field/method deconfliction (CS0102) ----------

    [Fact]
    public void FieldMethodCollision_FieldRenamed_ReferencesRewritten()
    {
        const string java = """
            public class GpuBuffer {
                private int usage;

                public int usage() {
                    return this.usage;
                }

                public int readUsage() {
                    return usage;
                }
            }
            """;

        var result = Convert(java);

        // The field is renamed everywhere; the method keeps its name.
        Assert.Contains("int usageField", result);
        Assert.Contains("return this.usageField", result);
        Assert.Contains("return usageField", result);
        Assert.Contains("int usage()", result);
        // No member with the colliding original name remains as a field.
        Assert.DoesNotContain("int usage;", result);
    }
}

using JavaToCSharp.TypeMapping;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the post-4.0.16 (4.0.17) fixes:
/// - Java local classes/records are hoisted out of method bodies into private nested types
///   (C# has no local classes), with captured locals routed through constructor parameters and
///   enclosing instance fields routed through an injected <c>parent</c> reference;
/// - method references emit the correct lambda shape: unbound instance refs become receiver
///   lambdas, bound refs become zero-arg (or arity-matching) lambdas, and constructor refs
///   forward the constructor's parameters;
/// - keywords used as member names are escaped in method-reference call sites;
/// - <c>!(a == b)</c> is inverted to <c>a != b</c> so template-mapped expressions like
///   <c>isEmpty()</c> → <c>Count == 0</c> stay valid under negation;
/// - a lambda under a cast is parenthesized even in simple (single-parameter) form.
/// </summary>
public class Round417FixTests
{
    private static JavaConversionOptions Options() => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
    };

    private static string Convert(string java)
        => JavaToCSharpConverter.ConvertText(java, Options()).ConvertedText ?? "";

    // ---------- Local class hoisting ----------

    [Fact]
    public void LocalClass_IsHoistedToNestedType()
    {
        const string java = """
            public class Outer {
                public int run() {
                    class Helper {
                        public int value() { return 42; }
                    }
                    Helper helper = new Helper();
                    return helper.value();
                }
            }
            """;

        var result = Convert(java);

        // The nested type must be emitted as a class member, not inside the method body.
        Assert.Contains("class Helper", result);
        Assert.DoesNotContain("\n            class Helper", result);
        Assert.Contains("new Helper()", result);
    }

    [Fact]
    public void LocalClass_CapturesLocalVariableViaConstructor()
    {
        const string java = """
            public class Outer {
                public int run() {
                    int baseCount = 5;
                    class Adder {
                        public int add(int value) { return baseCount + value; }
                    }
                    return new Adder().add(3);
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("new Adder(baseCount)", result);
        Assert.Contains("private readonly int baseCount;", result);
        Assert.Contains("this.baseCount = baseCount;", result);
        Assert.Contains("return this.baseCount + value;", result);
    }

    [Fact]
    public void LocalClass_ReferencesOuterInstanceFieldViaParent()
    {
        const string java = """
            public class Outer {
                private int seed;

                public int run() {
                    class Helper {
                        public int get() { return seed + 1; }
                    }
                    return new Helper().get();
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("Outer parent", result);
        Assert.Contains("new Helper(this)", result);
        Assert.Contains("return parent.seed + 1;", result);
    }

    // ---------- Method references ----------

    [Fact]
    public void UnboundMethodReference_BecomesReceiverLambda()
    {
        const string java = """
            import java.util.function.Function;

            public class Foo {
                public String name() { return ""; }

                public Function<Foo, String> getter() {
                    return Foo::name;
                }
            }
            """;

        var result = Convert(java);

        // The lambda receives the object and invokes the instance method on it.
        Assert.Contains("=> mrefArg", result);
        Assert.Contains(".name()", result);
        Assert.DoesNotContain("Foo.name()", result);
    }

    [Fact]
    public void BoundMethodReference_InvokesScope()
    {
        const string java = """
            import java.util.function.Supplier;

            public class Foo {
                public String name() { return ""; }

                public Supplier<String> getter() {
                    return this::name;
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("() => this.name()", result);
    }

    [Fact]
    public void ConstructorReference_ForwardsConstructorParameters()
    {
        const string java = """
            import java.util.function.BiFunction;

            public class Point {
                public Point(int x, int y) { }

                public static BiFunction<Integer, Integer, Point> factory() {
                    return Point::new;
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("=> new Point(", result);
        Assert.DoesNotContain("() => new Point()", result);
    }

    [Fact]
    public void KeywordMethodReference_IsEscaped()
    {
        const string java = """
            import java.util.function.Function;

            public class Foo {
                public String bool() { return ""; }

                public Function<Foo, String> getter() {
                    return Foo::bool;
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains(".@bool()", result);
        Assert.DoesNotContain(".bool()", result);
    }

    // ---------- Unary / cast fixes ----------

    [Fact]
    public void LogicalNot_OnEquality_IsInverted()
    {
        const string java = """
            public class Foo {
                public boolean check(int count) {
                    if (!(count == 0)) {
                        return true;
                    }
                    return !(count != 1);
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains("count != 0", result);
        Assert.Contains("count == 1", result);
    }

    [Fact]
    public void SimpleLambda_UnderCast_IsParenthesized()
    {
        const string java = """
            import java.util.function.Function;

            public class Foo {
                public Function<Integer, Integer> identity() {
                    return (Function<Integer, Integer>) s -> s;
                }
            }
            """;

        var result = Convert(java);

        Assert.Contains(")((", result);
        Assert.Matches(@"\(Func<int, int>\)\(\(s\) => s\)", result);
    }
}

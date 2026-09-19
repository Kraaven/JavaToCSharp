using JavaToCSharp;

namespace JavaToCSharp.Tests;

/// <summary>
/// Coverage for the 4.0.41 fixes:
/// A — explicit interface implementations carry no access modifiers and use the ORIGINAL
///     case of the base interface name (a lowercased specifier is CS0538, a public modifier
///     on an explicit impl is CS0106);
/// B — delegate base stand-ins (Predicate/Consumer/... shaped interfaces) are registered as
///     placeholder types owned one level OUTSIDE the deriving type: a C# class's base clause
///     cannot see its own nested types (CS0246 there breaks the base chain and cascades
///     CS0115 on every object override);
/// C — base-list stand-ins owned by TOP-LEVEL types drain at namespace level instead of
///     never being emitted.
/// </summary>
public class Round441FixTests
{
    private const string ExplicitImplJava = """
        package com.example;

        public class Handler implements GuiListener {
            public void onClick() { }
        }
        """;

    private const string DelegateBaseJava = """
        package com.example;

        import java.util.function.Predicate;

        public class StringFilter implements Predicate<String> {
            public boolean test(String value) { return value.length() > 0; }
        }
        """;

    private static JavaConversionOptions Options() => new()
    {
        IncludeNamespace = false,
        IncludeComments = false,
        InterfaceExtendsClosures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["guilistener"] = ["BaseListener"],
        },
        InterfaceMemberSignatures = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
        {
            ["baselistener"] = ["|void|onBase|"],
        },
        InterfaceDefaultMemberNames = new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["baselistener"] = new HashSet<string>(StringComparer.Ordinal),
        },
    };

    private static string Convert(string java, JavaConversionOptions? options = null)
        => JavaToCSharpConverter.ConvertText(java, options ?? Options()).ConvertedText ?? "";

    // ---------- A: explicit interface implementations ----------

    [Fact]
    public void ExplicitImpl_HasNoAccessModifier()
    {
        var csharp = Convert(ExplicitImplJava);

        Assert.Contains("void BaseListener.onBase()", csharp);

        var line = csharp
            .Split('\n')
            .First(l => l.Contains("BaseListener.onBase", StringComparison.Ordinal));

        Assert.DoesNotContain("public", line);
        Assert.DoesNotContain("private", line);
        Assert.DoesNotContain("internal", line);
    }

    [Fact]
    public void ExplicitImpl_UsesOriginalCaseSpecifier()
    {
        var csharp = Convert(ExplicitImplJava);

        Assert.Contains("BaseListener.onBase()", csharp);
        Assert.DoesNotContain("baselistener.onBase", csharp);
    }

    // ---------- B/C: base-list stand-in placement ----------

    [Fact]
    public void DelegateBaseStandIn_IsNotNestedInsideDerivingClass()
    {
        var csharp = Convert(DelegateBaseJava);

        Assert.Contains("abstract class", csharp);
        Assert.Contains("StringFilter", csharp);

        var classStart = csharp.IndexOf("class StringFilter", System.StringComparison.Ordinal);
        Assert.True(classStart >= 0, "StringFilter class not found in output");

        var bodyStart = csharp.IndexOf('{', classStart);
        var depth = 0;
        var bodyEnd = -1;

        for (var i = bodyStart; i < csharp.Length; i++)
        {
            if (csharp[i] == '{')
            {
                depth++;
            }
            else if (csharp[i] == '}' && --depth == 0)
            {
                bodyEnd = i;
                break;
            }
        }

        Assert.True(bodyEnd > bodyStart, "could not brace-match StringFilter body");

        var classBody = csharp[bodyStart..bodyEnd];

        Assert.DoesNotContain("abstract class", classBody);
    }
}
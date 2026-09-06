using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis.CSharp;

namespace JavaToCSharp.TypeMapping;

/// <summary>
/// Rewrites raw Java expression text so Roslyn's tolerant parser can translate it into valid C#.
/// Applied before <c>SyntaxFactory.ParseExpression</c> in <see cref="TypeMappingResolver"/> for
/// unsupported-member fragments. Java encodes explicit method type arguments between the dot and
/// the method name (<c>X.&lt;T&gt;m(...)</c>) while C# puts them after the method name
/// (<c>X.m&lt;T&gt;(...)</c>), and Java identifiers may be C# keywords in member position
/// (<c>.in(...)</c>). Both are rewritten here so the reparsed output compiles.
/// </summary>
internal static partial class JavaFragmentNormalizer
{
    /// <summary>
    /// Java explicit type witness: <c>X.&lt;A,B&gt;m(args)</c> → <c>X.m&lt;A,B&gt;(args)</c>.
    /// Type arguments never contain parentheses, so a single non-greedy scan handles nested
    /// generics (<c>Objects.&lt;List&lt;String&gt;&gt;requireNonNullElse(...)</c>) and arrays.
    /// Only rewrites when a method name and an invocation follow, so comparisons and null-safe
    /// accesses are never touched.
    /// </summary>
    [GeneratedRegex(@"\.\s*<\s*(?<args>[^()]*?)\s*>\s*(?<member>[A-Za-z_$][A-Za-z0-9_$]*)(?<paren>\s*\()", RegexOptions.CultureInvariant)]
    private static partial Regex ExplicitTypeWitnessRegex();

    private static readonly Lazy<Regex> KeywordMemberRegex =
        new(() => new Regex(
            $@"\.(?<kw>{string.Join("|", BuildKeywordList().Select(Regex.Escape))})\b",
            RegexOptions.CultureInvariant | RegexOptions.Compiled));

    /// <summary>
    /// Java lambda arrow: <c>x -&gt; expr</c> → <c>x =&gt; expr</c>. Plain (non-throwing) arrows are
    /// always lambda arrows in expression text; the throwing form <c>x -&gt;&gt; expr</c> never
    /// survives decompilation into a fragment.
    /// </summary>
    private static readonly Regex LambdaArrowRegex = new(
        @"\s*-\>\s*",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Java bounded wildcards inside type arguments: <c>? extends X</c> / <c>? super X</c> → <c>X</c>
    /// (erasure). Star wildcards (<c>? extends X[]</c>, nested bounds) are not fully addressed here;
    /// the overwhelmingly common shape is a single simple bound.
    /// </summary>
    private static readonly Regex BoundedWildcardRegex = new(
        @"\?\s*(?:extends|super)\s+([A-Za-z_$][A-Za-z0-9_$.\[\]]*)",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Java type-test with optional binding: <c>x instanceof Foo</c> / <c>x instanceof Foo f</c> use
    /// the identical token shape as C# pattern forms <c>x is Foo</c> / <c>x is Foo f</c>, so only the
    /// operator keyword needs rewriting.
    /// </summary>
    private static readonly Regex InstanceOfRegex = new(
        @"\binstanceof\b",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Leaked Java type-use annotations (<c>FrameGraphBuilder.@Nullable Pass</c>) embedded in a
    /// fragment. Same rule as <see cref="TypeHelper.StripTypeUseAnnotations"/>.
    /// </summary>
    private static readonly Regex TypeUseAnnotationRegex = new(
        @"(?<dot>\.)?\s*@[A-Z][A-Za-z0-9_]*\s+",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static string Normalize(string javaFragment)
    {
        var rewritten = ExplicitTypeWitnessRegex().Replace(javaFragment, ".${member}<${args}>${paren}");
        rewritten = KeywordMemberRegex.Value.Replace(rewritten, ".@${kw}");
        rewritten = LambdaArrowRegex.Replace(rewritten, " => ");
        rewritten = BoundedWildcardRegex.Replace(rewritten, "$1");
        rewritten = InstanceOfRegex.Replace(rewritten, "is");
        rewritten = TypeUseAnnotationRegex.Replace(rewritten, static m => m.Groups["dot"].Success ? "." : " ");
        return rewritten;
    }

    /// <summary>
    /// C# keywords that are legal Java identifiers but invalid as a member name in Roslyn
    /// (<c>x.in(...)</c> fails to parse). Every reserved keyword is included, plus the
    /// contextual <c>as/in/is/out/ref</c>, which are rejected specifically in member position.
    /// Other contextual keywords (<c>value</c>, <c>var</c>, <c>select</c>, …) are valid there
    /// and must remain untouched.
    /// </summary>
    private static IReadOnlyList<string> BuildKeywordList()
    {
        var reserved = new List<string>();
        var contextual = new List<string>();
        foreach (var kw in AllKeywords)
        {
            var kind = SyntaxFacts.GetKeywordKind(kw);
            if (kind == SyntaxKind.None)
            {
                continue;
            }

            if (SyntaxFacts.IsReservedKeyword(kind))
            {
                reserved.Add(kw);
            }
            else
            {
                contextual.Add(kw);
            }
        }

        // All reserved keywords fail as member names; from the contextual set only these do.
        const string RiskyContextualKeywords = "as in is out ref";
        reserved.AddRange(RiskyContextualKeywords.Split(' '));
        return reserved;
    }

    private static readonly IReadOnlyList<string> AllKeywords =
    [
        "abstract", "as", "base", "bool", "break", "byte", "case", "catch", "char", "checked",
        "class", "const", "continue", "decimal", "default", "delegate", "do", "double", "else",
        "enum", "event", "explicit", "extern", "false", "finally", "fixed", "float", "for",
        "foreach", "goto", "if", "implicit", "in", "int", "interface", "internal", "is", "lock",
        "long", "namespace", "new", "null", "object", "operator", "out", "override", "params",
        "private", "protected", "public", "readonly", "ref", "return", "sbyte", "sealed", "short",
        "sizeof", "stackalloc", "static", "string", "struct", "switch", "this", "throw", "true",
        "try", "typeof", "uint", "ulong", "unchecked", "unsafe", "ushort", "using", "virtual",
        "void", "volatile", "while",
    ];
}
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.type;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ast = com.github.javaparser.ast;
using Type = com.github.javaparser.ast.type.Type;

namespace JavaToCSharp;

public static class TypeHelper
{
    private static readonly Dictionary<string, string> _typeNameConversions = new()
    {
        // Simple types
        ["boolean"] = "bool",
        ["Boolean"] = "bool",
        ["ICloseable"] = "IDisposable",
        ["Integer"] = "int",
        ["Long"] = "long",
        ["Float"] = "float",
        ["String"] = "string",
        ["Object"] = "object",
        ["AutoCloseable"] = "IDisposable",

        // Generic types
        ["ArrayList"] = "List",
        ["List"] = "IList",
        ["Map"] = "Dictionary",
        ["Set"] = "HashSet",
        ["Iterator"] = "IEnumerator",
        ["Comparable"] = "IComparable",

        // Functional interfaces
        ["Function"] = "Func",
        ["BiFunction"] = "Func",
        ["Consumer"] = "Action",
        ["BiConsumer"] = "Action",
        ["Supplier"] = "Func",
        // java.util.function.Predicate<T> maps to System.Predicate<T> (same arity). The previous
        // "Func<bool>" substitution injected a stray type argument ahead of the original ones,
        // producing invalid C# such as "Func<bool> < string >" for "Predicate<String>".
        ["Predicate"] = "Predicate",
        // BiPredicate<T,U> re-aris to Func<T,U,bool>; the table cannot express the appended
        // result argument, so RewriteFunctionalInterfaceTypes handles it on the full string.
        ["Callable"] = "Func",

        // Exceptions
        ["AlreadyClosedException"] = "ObjectDisposedException",
        ["Error"] = "Exception",
        ["IllegalArgumentException"] = "ArgumentException",
        ["IllegalStateException"] = "InvalidOperationException",
        ["UnsupportedOperationException"] = "NotSupportedException",
        ["RuntimeException"] = "Exception",
        ["AccessDeniedException"] = "UnauthorizedAccessException",
        ["AssertionError"] = "InvalidOperationException",
        ["NullPointerException"] = "NullReferenceException",
        ["UncheckedIOException"] = "IOException",
        ["EOFException"] = "EndOfStreamException",
        ["NoSuchFileException"] = "FileNotFoundException",
    };

    /// <summary>
    /// JavaParser's <c>toString()</c> on type nodes pretty-prints with whitespace around every
    /// token (<c>Object2IntMap &lt; GossipType &gt;</c>, <c>FrameRenderPass . Draw &lt; X [ ] &gt;</c>).
    /// The mapping and template machinery matches names, argument groups and <c>$N</c>
    /// placeholders by exact adjacency, so type strings are compacted at every AST-to-string
    /// entry point. Whitespace separating a wildcard bound from its bound type
    /// (<c>? extends Foo</c>) is preserved - the wildcard marker is not part of the
    /// punctuation set.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex PunctuationSpacingRegex = new(
        @"\s*([<>.,\[\]$])\s*",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string CompactTypeString(string typeString)
        => PunctuationSpacingRegex.Replace(typeString, "$1");

    public static string ConvertTypeOf(Ast.nodeTypes.NodeWithType typedNode)
    {
        return ConvertType(CompactTypeString(typedNode.getType().toString()));
    }

    public static string ConvertType(Type type)
    {
        return ConvertType(CompactTypeString(type.toString()));
    }

    /// <summary>
    /// Converts a Java wildcard type string to a C# type.
    /// Handles: "? extends X" -> "X", "? super X" -> "X", "?" -> "object".
    /// When a conversion context with a <see cref="JavaConversionOptions.GenericConstraintIndex"/>
    /// is supplied, a bare wildcard is first resolved against the declaring generic type's own
    /// type-parameter bounds (<c>Criterion&lt;?&gt;</c> where
    /// <c>class Criterion&lt;T extends Instance&gt;</c> yields <c>Criterion&lt;Instance&gt;</c>),
    /// because C# rejects <c>object</c> as a type argument that must satisfy a class/interface
    /// constraint (CS0311).
    /// </summary>
    public static string ConvertWildcardType(string typeName)
        => ConvertWildcardType(context: null, typeName);

    public static string ConvertWildcardType(ConversionContext? context, string typeName, string? genericOwnerSimpleName = null, int argumentPosition = -1)
    {
        var trimmed = typeName.Trim();
        if (!trimmed.StartsWith('?'))
        {
            return typeName;
        }

        if (trimmed == "?")
        {
            if (context is not null && genericOwnerSimpleName is not null && argumentPosition >= 0)
            {
                var bound = TryGetConstraintBound(context, genericOwnerSimpleName, argumentPosition);

                if (!string.IsNullOrWhiteSpace(bound))
                {
                    return SatisfyBound(context, bound, genericOwnerSimpleName);
                }
            }

            return "object";
        }

        // "? extends X" or "? super X" -> convert the bound type
        var spaceIndex = trimmed.IndexOf(' ');
        if (spaceIndex > 0 && spaceIndex + 1 < trimmed.Length)
        {
            var bound = trimmed[(spaceIndex + 1)..];
            return ConvertType(bound);
        }

        return "object";
    }

    private static string? TryGetConstraintBound(ConversionContext context, string genericOwnerSimpleName, int argumentPosition)
    {
        if (context.Options.GenericConstraintIndex is not { Count: > 0 } index)
        {
            return null;
        }

        // Nested owners (LootItemConditionalFunction.Builder) resolve against their dotted key
        // first — a bare leaf key ("Builder") is likely conflicted tree-wide and omitted — then
        // fall back to the leaf for hosts that only register top-level types. A bare leaf owner
        // (already leaf-extracted at the call site) also probes the "Root.Nested" shape: the
        // reference may have arrived with its qualifier stripped (ConvertWildcardType callers
        // pass a mapped/normalized owner), and nested rows are keyed by the dotted name.
        if (index.TryGetValue(genericOwnerSimpleName, out var bounds)
            || genericOwnerSimpleName.IndexOf('.') >= 0
                && index.TryGetValue(genericOwnerSimpleName.Split('.')[^1], out bounds))
        {
            if (argumentPosition < bounds.Count)
            {
                var bound = bounds[argumentPosition];
                return string.IsNullOrWhiteSpace(bound) ? null : bound;
            }
        }

        if (genericOwnerSimpleName.IndexOf('.') < 0)
        {
            foreach (var key in index.Keys)
            {
                if (key.IndexOf('.') >= 0
                    && string.Equals(key[(key.LastIndexOf('.') + 1)..], genericOwnerSimpleName, StringComparison.Ordinal)
                    && argumentPosition < index[key].Count)
                {
                    var bound = index[key][argumentPosition];
                    return string.IsNullOrWhiteSpace(bound) ? null : bound;
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Rewrites bare wildcards (<c>?</c>) inside a type-name string through the
    /// <see cref="JavaConversionOptions.GenericConstraintIndex"/>, using the generic type that
    /// directly owns the argument list (nested generics resolve against their own owner, so in
    /// <c>Pair&lt;Codec&lt;?&gt;, Integer&gt;</c> the wildcard belongs to <c>Codec</c>).
    /// Bounded wildcards are left for <see cref="ConvertWildcardType"/>, which resolves them to
    /// their bound.
    /// </summary>
    private static string ResolveBareWildcardsThroughConstraints(ConversionContext? context, string typeName)
    {
        if (context?.Options.GenericConstraintIndex is not { Count: > 0 } index)
        {
            return typeName;
        }

        if (!typeName.Contains('?'))
        {
            return typeName;
        }

        return ResolveWildcardNode(context, index, typeName.Trim(), ExtractSimpleTypeName(typeName) ?? typeName);

        static string ResolveWildcardNode(ConversionContext context, IReadOnlyDictionary<string, IReadOnlyList<string>> index, string node, string owner)
        {
            int open = node.IndexOf('<');

            if (open < 0 || !TryFindMatchingAngleBracket(node, open, out var close))
            {
                return node;
            }

            var root = ExtractSimpleTypeName(node[..open].Trim()) ?? owner;
            var inner = node.Substring(open + 1, close - open - 1);
            var args = SplitTopLevelTypeArguments(inner);
            var resolvedArgs = new List<string>(args.Count);

            // The owner's DOTTED spelling (before leaf extraction) — nested-type constraint rows
            // are keyed "Root.Nested", and a bare-leaf probe would miss them.
            var dottedRoot = node[..open].Trim();

            for (int i = 0; i < args.Count; i++)
            {
                var arg = args[i].Trim();

                if (arg == "?")
                {
                    // Nested owners resolve through the SAME dotted-aware lookup as
                    // ConvertWildcardType (dotted key first, leaf fallback) — a bare leaf lookup
                    // here would miss nested rows like "HangRegression.Nested".
                    var bound = TryGetConstraintBound(context,
                        dottedRoot.Contains('.') ? dottedRoot : root, i);

                    if (!string.IsNullOrWhiteSpace(bound))
                    {
                        // Constraint-satisfied bound: self-referential bounds resolve to the
                        // enclosing type parameter or a synthesized stand-in; BCL shapes to a
                        // satisfying type (IComparable → int).
                        resolvedArgs.Add(SatisfyBound(context, bound, root));
                    }
                    else
                    {
                        resolvedArgs.Add(arg);
                    }

                    continue;
                }

                resolvedArgs.Add(ResolveWildcardNode(context, index, arg, ExtractSimpleTypeName(arg) ?? arg));
            }

            return node[..(open + 1)] + string.Join(", ", resolvedArgs) + node[close..];
        }
    }

    /// <summary>
    /// Rewrites Java <c>BiPredicate&lt;T, U&gt;</c> to the C# <c>Func&lt;T, U, bool&gt;</c> shape.
    /// The simple-name conversion table cannot express the re-arity (the bool result argument is
    /// appended after the original ones, not substituted for the name), so the rewrite runs on the
    /// full type string with balanced angle-bracket scanning. Arguments are rewritten recursively,
    /// so nested occurrences are handled too.
    /// </summary>
    private static string RewriteFunctionalInterfaceTypes(string typeName)
    {
        if (typeName.IndexOf("BiPredicate", StringComparison.Ordinal) < 0)
        {
            return typeName;
        }

        var sb = new System.Text.StringBuilder(typeName.Length);
        var i = 0;

        while (i < typeName.Length)
        {
            if (IsBiPredicateTokenAt(typeName, i))
            {
                var bracketIndex = i + "BiPredicate".Length;
                while (bracketIndex < typeName.Length && char.IsWhiteSpace(typeName[bracketIndex]))
                {
                    bracketIndex++;
                }

                if (bracketIndex < typeName.Length
                    && typeName[bracketIndex] == '<'
                    && TryFindMatchingAngleBracket(typeName, bracketIndex, out var closeIndex))
                {
                    var args = SplitTopLevelTypeArguments(typeName.Substring(bracketIndex + 1, closeIndex - bracketIndex - 1))
                        .Select(RewriteFunctionalInterfaceTypes);
                    sb.Append("Func<").Append(string.Join(", ", args)).Append(", bool>");
                    i = closeIndex + 1;
                    continue;
                }
            }

            sb.Append(typeName[i]);
            i++;
        }

        return sb.ToString();
    }

    /// <summary>
    /// Returns true when the identifier <c>BiPredicate</c> starts at <paramref name="index"/> and
    /// is a standalone token (not part of a longer identifier such as <c>BiPredicateX</c>).
    /// </summary>
    private static bool IsBiPredicateTokenAt(string typeName, int index)
    {
        const string token = "BiPredicate";

        if (index + token.Length > typeName.Length
            || string.CompareOrdinal(typeName, index, token, 0, token.Length) != 0)
        {
            return false;
        }

        if (index > 0 && IsTypeNameChar(typeName[index - 1]))
        {
            return false;
        }

        return index + token.Length >= typeName.Length || !IsTypeNameChar(typeName[index + token.Length]);
    }

    private static bool IsTypeNameChar(char c)
        => char.IsLetterOrDigit(c) || c == '_' || c == '$' || c == '.';

    /// <summary>
    /// Finds the angle bracket matching the one at <paramref name="openIndex"/>, accounting for
    /// nesting. Returns false when unbalanced.
    /// </summary>
    internal static bool TryFindMatchingAngleBracket(string text, int openIndex, out int closeIndex)
    {
        closeIndex = -1;
        var depth = 0;

        for (var i = openIndex; i < text.Length; i++)
        {
            if (text[i] == '<')
            {
                depth++;
            }
            else if (text[i] == '>')
            {
                depth--;
                if (depth == 0)
                {
                    closeIndex = i;
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Splits a generic type-argument list on commas that sit at bracket depth zero.
    /// </summary>
    internal static List<string> SplitTopLevelTypeArguments(string args)
    {
        var result = new List<string>();
        var depth = 0;
        var start = 0;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case ',' when depth == 0:
                    result.Add(args[start..i].Trim());
                    start = i + 1;
                    break;
            }
        }

        result.Add(args[start..].Trim());
        return result;
    }

    /// <summary>
    /// Returns true if the given type string represents a Java wildcard (?, ? extends X, ? super X).
    /// </summary>
    public static bool IsWildcardType(Type type)
    {
        if (type is WildcardType)
            return true;

        var str = type.toString().Trim();
        return str.StartsWith('?');
    }

    /// <summary>
    /// Erases Java wildcards embedded anywhere in a type string. The <see cref="TypeNameParser"/>
    /// only recognizes simple (non-dotted) generic names, so dotted qualified types such as
    /// <c>FrameGraphBuilder.InternalVirtualResource&lt;?&gt;</c> fall through unchanged. This pass
    /// erases those remaining wildcards: <c>? extends X</c> / <c>? super X</c> become <c>X</c> and
    /// a bare <c>?</c> becomes <c>object</c>. Nesting is handled iteratively.
    /// </summary>
    public static string EraseWildcards(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName) || typeName.IndexOf('?') < 0)
        {
            return typeName ?? string.Empty;
        }

        // Iteratively erase "? extends X" / "? super X" (bound becomes X) from the inside out.
        var changed = true;
        while (changed)
        {
            changed = false;
            var next = BoundedWildcardRegex.Replace(typeName, static m => m.Groups[1].Value);
            if (!string.Equals(next, typeName, StringComparison.Ordinal))
            {
                typeName = next;
                changed = true;
            }
        }

        // Any remaining '?' is a bare wildcard; erase to object.
        return typeName.Replace("?", "object");
    }

    private static readonly System.Text.RegularExpressions.Regex BoundedWildcardRegex = new(
        @"\?\s*(?:extends|super)\s+([A-Za-z_$][A-Za-z0-9_$.]*)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Java type-use annotations that VineFlower inlines into the type position
    /// (<c>FrameGraphBuilder.@Nullable Pass</c>). An at-sign followed by an identifier can only
    /// ever be a leaked Java annotation — C# verbatim identifiers always prefix a keyword
    /// (<c>@base</c>) or a name that needs no escaping — so the whole <c>@Name</c> token (with
    /// optional qualified segments and argument list, e.g.
    /// <c>@org.jspecify.annotations.Nullable</c> or <c>@SuppressWarnings("x")</c>) is removed.
    /// No trailing whitespace is required: JavaParser renders TYPE-USE annotations on array
    /// types without a separator (<c>String @Nullable[]</c>), and stripping must still succeed
    /// there (<c>String[]</c>). A leading dot is preserved so the enclosing type name re-joins
    /// the member type (<c>FrameGraphBuilder.Pass</c>).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TypeUseAnnotationRegex = new(
        @"(?<dot>\.)?\s*@(?!(?:base|params|event|object|string|var|void|delegate|operator|ref|out|is|as)(?![A-Za-z0-9_]))[A-Za-z_][A-Za-z0-9_]*(?:\.[A-Za-z_][A-Za-z0-9_]*)*(?:\([^()]*\))?",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    public static string StripTypeUseAnnotations(string typeName)
    {
        if (typeName.IndexOf('@') < 0)
        {
            return typeName;
        }

        return TypeUseAnnotationRegex.Replace(typeName, static m => m.Groups["dot"].Success ? "." : " ");
    }

    public static string ConvertType(string typeName)
    {
        typeName = CompactTypeString(StripTypeUseAnnotations(typeName));

        if (typeName.TrimStart().StartsWith('?'))
        {
            return ConvertWildcardType(typeName);
        }

        typeName = RewriteFunctionalInterfaceTypes(typeName);
        var parsed = TypeNameParser.ParseTypeName(typeName, s => _typeNameConversions.TryGetValue(s, out string? converted) ? converted : s);
        return NormalizeTypeName(EraseWildcards(parsed));
    }

    /// <summary>
    /// Converts a type name, first rewriting mapped names through <see cref="TypeMappingIndex"/>
    /// (when configured) and then applying the built-in dictionary fallback. Mapped names override
    /// the dictionary (e.g. <c>Map</c> → <c>IDictionary</c> instead of <c>Dictionary</c>).
    /// </summary>
    public static string ConvertType(ConversionContext context, string typeName, bool synthesizeRawArguments = true)
    {
        typeName = CompactTypeString(StripTypeUseAnnotations(typeName));
        typeName = ResolveBareWildcardsThroughConstraints(context, typeName);

        // A simple name that refers to a type declared in this file (nested/local class, record,
        // enum) must not go through built-in or mapped conversions — a nested record named
        // "Double" is a type, not the C# "double" keyword. The type's ARGUMENTS still convert:
        // Codec<Integer> inside Codec.java must become Codec<int>. In-scope type parameters
        // (class and method level, e.g. <Value>) are protected the same way: a mapping row whose
        // leaf name matches a type-parameter name must not hijack the reference.
        // The anchor is the reference's FIRST segment, not its leaf: a fully-qualified
        // com.google...ImmutableList.Builder inside a file that declares its own Builder<T>
        // must still resolve through the mapping index (leaf-matching would emit the reference
        // verbatim, CS0246 com.google...).
        var declaredName = ExtractFirstSegmentName(typeName);

        // Register the declared namespace of tree-known types as a using: context-dependent
        // conversion (wildcard resolution through constraint bounds) can inject a type from
        // another file into this file's signatures without any import statement, and the
        // emitted using would otherwise be missing (CS0246). Harmless for locally declared
        // types (own-namespace usings are legal) and deduplicated at emission.
        CollectKnownTypeNamespaceUsing(context, declaredName);

        if (declaredName is not null
            && (context.DeclaredTypeNames.Contains(declaredName)
                || context.TypeParameterNames.Contains(declaredName)))
        {
            var converted = NormalizeTypeName(EraseWildcards(ConvertDeclaredTypeArguments(context, typeName)));

            // A qualified reference to a nested type of a GENERIC declared outer
            // (OptionInstance.TooltipSupplier<T> inside OptionInstance.java) is CS0305 in C#:
            // nested types of an unbound generic cannot be accessed by the qualified name.
            // The bare nested name resolves through the enclosing-scope lookup instead.
            converted = AdjustNestedGenericReference(context, converted);

            return SynthesizeRawGenericTypeArguments(
                context, converted, allowBclDefaults: false,
                synthesize: synthesizeRawArguments);
        }

        if (context.Options.MappingIndex is not null)
        {
            TypeMapping.TypeMappingResolver.TryResolveTypeName(context, typeName, out var mapped);
            return SynthesizeRawGenericTypeArguments(
                context, NormalizeTypeName(EraseWildcards(RewriteNamespaceQualifiedReference(context, mapped))), synthesize: synthesizeRawArguments);
        }

        return SynthesizeRawGenericTypeArguments(
            context, ConvertType(typeName), synthesize: synthesizeRawArguments);
    }

    /// <summary>
    /// Rewrites a surviving dotted Java FQN reference whose PACKAGE prefix carries a namespace
    /// row ("net.minecraft.world.entity.Entity" → "Net.Minecraft.World.Entities.Entity"). The
    /// dictionary fallback capitalizes each segment but knows nothing about the tree's package
    /// RENAMES (world.entity → world.entities), leaving references pointing at a namespace that
    /// no declared type lives in (CS0246 on the first renamed segment). The longest matching
    /// java-package prefix is replaced by its mapped C# namespace; the trailing segments (the
    /// type and its nesting) keep their capitalized spelling. Undotted names pass through.
    /// </summary>
    private static string RewriteNamespaceQualifiedReference(ConversionContext context, string typeName)
    {
        if (typeName.IndexOf('.') < 0
            || context.Options.MappingIndex is not { } index
            || index.NamespaceMappings.Count == 0)
        {
            return typeName;
        }

        var genericIndex = typeName.IndexOf('<');
        var beforeGenerics = genericIndex >= 0 ? typeName[..genericIndex] : typeName;
        var suffix = genericIndex >= 0 ? typeName[genericIndex..] : "";

        var segments = beforeGenerics.Split('.');

        // Longest java-package prefix wins; the prefix must not swallow the type segment.
        for (var take = segments.Length - 1; take >= 1; take--)
        {
            var javaPackage = string.Join('.', segments[..take]);

            if (!index.NamespaceMappings.TryGetValue(javaPackage, out var csharpPackages)
                || csharpPackages.Length == 0
                || string.IsNullOrWhiteSpace(csharpPackages[0]))
            {
                continue;
            }

            return csharpPackages[0]
                + "." + string.Join('.', segments[take..].Select(TypeHelper.Capitalize))
                + suffix;
        }

        return typeName;
    }

    /// <summary>
    /// Applies a mapped class target that carries <c>$N</c> placeholders
    /// (<c>Dictionary&lt;long, $1&gt;</c>) against the Java type arguments of the reference.
    /// Substitution rules:
    /// - <c>$N</c> is positional (1-based Nth Java type argument);
    /// - the <em>last placeholder-bearing token</em> of the template absorbs its own argument
    ///   and every remaining one, so <c>Dictionary&lt;$1&gt;</c> over <c>Map&lt;K, V&gt;</c>
    ///   yields <c>Dictionary&lt;K, V&gt;</c> and <c>Func&lt;$1, bool&gt;</c> over
    ///   <c>BiPredicate&lt;A, B&gt;</c> yields <c>Func&lt;A, B, bool&gt;</c>;
    /// - a placeholder with no corresponding argument becomes <c>object</c> (raw usage);
    /// - tokens without placeholders are literal text.
    /// The mapped name may be dotted with its own leading arguments
    /// (<c>Holder&lt;object&gt;.Reference</c>) — the template group is the first
    /// angle-bracket group containing a placeholder.
    /// Returns <paramref name="mappedName"/> unchanged when it carries no placeholders.
    /// </summary>
    internal static string ApplyClassTargetTemplate(string mappedName, IReadOnlyList<string> javaArgs)
    {
        if (!mappedName.Contains('$'))
        {
            return mappedName;
        }

        var open = mappedName.IndexOf('<');

        while (open >= 0)
        {
            if (!TryFindMatchingAngleBracket(mappedName, open, out var close))
            {
                return mappedName;
            }

            var candidate = mappedName.Substring(open + 1, close - open - 1);

            if (candidate.Contains('$'))
            {
                var before = mappedName[..open];
                var after = mappedName[(close + 1)..];
                var expanded = ExpandTemplateArguments(candidate, javaArgs);
                return before + "<" + expanded + ">" + after;
            }

            open = mappedName.IndexOf('<', close + 1);
        }

        return mappedName;
    }

    private static string ExpandTemplateArguments(string templateArgsText, IReadOnlyList<string> javaArgs)
    {
        var tokens = SplitTopLevelTypeArguments(templateArgsText);
        var lastPlaceholderToken = -1;

        for (var i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Contains('$'))
            {
                lastPlaceholderToken = i;
            }
        }

        var expanded = new List<string>(tokens.Count);

        for (var i = 0; i < tokens.Count; i++)
        {
            var token = tokens[i].Trim();
            var positional = System.Text.RegularExpressions.Regex.Match(token, @"^\$(\d+)$");

            if (positional.Success)
            {
                var n = int.Parse(positional.Groups[1].Value);

                if (i == lastPlaceholderToken)
                {
                    var absorbed = javaArgs.Skip(n - 1).ToList();
                    expanded.Add(absorbed.Count > 0 ? string.Join(", ", absorbed) : "object");
                }
                else
                {
                    expanded.Add(n >= 1 && n <= javaArgs.Count ? javaArgs[n - 1] : "object");
                }
            }
            else if (token.Contains('$'))
            {
                expanded.Add(System.Text.RegularExpressions.Regex.Replace(token, @"\$(\d+)", match =>
                {
                    var n = int.Parse(match.Groups[1].Value);
                    return n >= 1 && n <= javaArgs.Count ? javaArgs[n - 1] : "object";
                }));
            }
            else
            {
                expanded.Add(token);
            }
        }

        return string.Join(", ", expanded);
    }

    /// <summary>
    /// Registers the C# namespace of a tree-known type as a mapping using. The host supplies
    /// <see cref="JavaConversionOptions.KnownTypeNamespaces"/> (simple name, lowercased →
    /// namespace); conflicted simple names are omitted there and instead carry ALL their
    /// candidate namespaces in <see cref="JavaConversionOptions.KnownTypeNamespaceCandidates"/>
    /// (every candidate is injected — a conflicted-name reference with no using is CS0246,
    /// while the candidate usings are harmless unless the file really needs disambiguation).
    /// Usings are deduplicated at emission, so repeated registrations are cheap.
    /// </summary>
    private static void CollectKnownTypeNamespaceUsing(ConversionContext context, string? simpleName)
    {
        if (string.IsNullOrEmpty(simpleName))
        {
            return;
        }

        if (context.Options.KnownTypeNamespaces is { Count: > 0 } known
            && known.TryGetValue(simpleName.ToLowerInvariant(), out var ns)
            && !string.IsNullOrWhiteSpace(ns))
        {
            context.MappingUsings.Add(ns);
        }

        if (context.Options.KnownTypeNamespaceCandidates is { Count: > 0 } candidates
            && candidates.TryGetValue(simpleName.ToLowerInvariant(), out var namespaces))
        {
            // Skip injection when the file's OWN imports already resolve the simple name:
            // the file has a definite candidate, and injecting the others would turn a
            // resolvable reference into an ambiguity (CS0104 — PlayerInfo, NameAndId, Unit).
            if (context.FileMappingContext is { } fileMapping
                && fileMapping.TryResolveClassName(simpleName, out _))
            {
                return;
            }

            foreach (var candidate in namespaces)
            {
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    context.MappingUsings.Add(candidate);
                }
            }
        }
    }

    /// <summary>
    /// Converts the type arguments of a reference to a type declared in the current file while
    /// keeping the declared root name untouched (<c>Codec&lt;Integer&gt;</c> keeps
    /// <c>Codec</c> but converts <c>Integer</c> → <c>int</c>). Arguments convert recursively,
    /// so nested generics convert too.
    /// </summary>
    private static string ConvertDeclaredTypeArguments(ConversionContext context, string typeName)
    {
        var open = typeName.IndexOf('<');

        if (open < 0 || !TryFindMatchingAngleBracket(typeName, open, out var close))
        {
            return typeName;
        }

        var inner = typeName.Substring(open + 1, close - open - 1);
        var args = SplitTopLevelTypeArguments(inner)
            .Select(arg => ConvertType(context, arg.Trim()))
            .ToList();

        return typeName[..(open + 1)] + string.Join(", ", args) + typeName[close..];
    }

    /// <summary>
    /// C# default type-argument lists for well-known BCL generics that Java code uses raw (raw
    /// types are illegal in C#). Keyed by the converted C# simple name; used when the
    /// <see cref="JavaConversionOptions.KnownGenericArity"/> index has no entry.
    /// </summary>
    private static readonly Dictionary<string, string> BclRawTypeArguments = new(StringComparer.Ordinal)
    {
        // Collections & core generic interfaces (targets of the java.util conversions).
        ["IList"] = "object",
        ["ICollection"] = "object",
        ["IEnumerable"] = "object",
        ["IEnumerator"] = "object",
        ["IComparer"] = "object",
        ["IComparable"] = "object",
        ["IEqualityComparer"] = "object",
        ["IProducerConsumerCollection"] = "object",
        ["List"] = "object",
        ["LinkedList"] = "object",
        ["HashSet"] = "object",
        ["SortedSet"] = "object",
        ["Queue"] = "object",
        ["Stack"] = "object",
        ["IDictionary"] = "object, object",
        ["Dictionary"] = "object, object",
        ["SortedDictionary"] = "object, object",
        ["SortedList"] = "object, object",
        ["KeyValuePair"] = "object, object",
        ["SynchronizedCollection"] = "object",

        // Delegates.
        ["Func"] = "object, object",
        ["Action"] = "object",
        ["Predicate"] = "object",
        ["Comparison"] = "object",

        // Misc.
        ["Task"] = "object",
        ["Lazy"] = "object",
        ["WeakReference"] = "object",
        ["Span"] = "byte",
        ["ReadOnlySpan"] = "byte",
        ["Memory"] = "byte",
        ["ReadOnlyMemory"] = "byte",
    };

    /// <summary>
    /// Java raw types (a generic type used without type arguments, legal in Java via erasure) are
    /// illegal in C# (CS0305). When the converted type has no type arguments and its simple name
    /// is a known generic — from the host-provided <see cref="JavaConversionOptions.KnownGenericArity"/>
    /// index or the built-in BCL defaults — synthesize <c>object</c> arguments. Array suffixes are
    /// preserved.
    /// </summary>
    private static string SynthesizeRawGenericTypeArguments(
        ConversionContext context, string typeName, bool allowBclDefaults = true, bool synthesize = true)
    {
        if (!synthesize || typeName.IndexOf('<') >= 0)
        {
            return typeName;
        }

        var core = typeName;
        var suffix = "";
        var bracketIndex = typeName.IndexOf('[');
        if (bracketIndex >= 0)
        {
            core = typeName[..bracketIndex];
            suffix = typeName[bracketIndex..];
        }

        var segments = core.Split('.');
        string? arguments = null;
        bool dottedKeyRegistered = false;

        if (context.Options.KnownGenericArity is { } arityIndex)
        {
            // Dotted (nested) references resolve against the full dotted name first: simple names
            // are ambiguous across a tree (a generic record named Schema does not make the
            // non-generic com.mojang...Schema generic). A registered arity of 0 means the host
            // scan explicitly saw a non-generic type with this name — leave the reference raw,
            // and do NOT fall through to the BCL leaf default: Action/Predicate leaf names are
            // common nested names, and `RealmsWorldSlotButton.Action` is non-generic even though
            // the BCL delegate `Action` is not.
            if (segments.Length > 1 && arityIndex.TryGetValue(core, out var dottedArity))
            {
                dottedKeyRegistered = true;

                if (dottedArity > 0)
                {
                    arguments = BuildRawArguments(context, segments[^1], dottedArity);
                }
            }
            else if (arityIndex.TryGetValue(segments[^1], out var arity) && arity > 0)
            {
                arguments = BuildRawArguments(context, segments[^1], arity);
            }
        }

        if (arguments is null && !dottedKeyRegistered && allowBclDefaults && BclRawTypeArguments.TryGetValue(segments[^1], out var bclArguments))
        {
            arguments = bclArguments;
        }

        if (arguments is null)
        {
            return typeName;
        }

        return core + "<" + arguments + ">" + suffix;
    }

    /// <summary>
    /// Adjusts a mapped NESTED-type reference ("Outer.Inner") when the outer type is generic —
    /// C# forbids accessing a nested type of an UNBOUND generic type (CS0305). Two resolutions:
    /// when the file itself declares the outer type, emit the bare nested name (C# name lookup
    /// walks outward through enclosing scopes); otherwise close the outer type with arguments
    /// satisfied from the constraint index (outer arguments are irrelevant for nested-type
    /// access, but the constraint on them still binds the closing form).
    /// The rewrite is IDEMPOTENT: dynamic nested-type rows carry PRE-CLOSED targets
    /// ("Holder&lt;object&gt;.Reference"), so a root that already carries a type-argument group is
    /// never re-closed. Splitting the name at the first '&lt;' would strand the root's own
    /// arguments in the "suffix" and re-close the root, producing the invalid
    /// "Holder&lt;object&gt;.&lt;object&gt;.Reference" (whose trailing tail the parser then swallows,
    /// scattering parse errors through the consuming signatures).
    /// For a pre-closed root, plain <c>object</c> arguments are re-satisfied against the
    /// constraint index: a row's pre-closed &lt;object&gt; violates every non-empty bound at that
    /// position (CS0311), and the closing form chosen here is exactly what the unclosed path
    /// would have synthesized.
    /// </summary>
    internal static string AdjustNestedGenericReference(ConversionContext context, string csharpName)
    {
        if (!csharpName.Contains('.'))
        {
            return csharpName;
        }

        if (context.Options.KnownGenericArity is not { } arityIndex)
        {
            return csharpName;
        }

        // Angle-bracket-aware top-level split: the segments of
        // "MapDecoder<object>.Implementation<A>" are "MapDecoder<object>" and "Implementation<A>".
        var depth = 0;
        var rootEnd = -1;
        string rootSegment;
        string rest;

        for (var i = 0; i < csharpName.Length; i++)
        {
            switch (csharpName[i])
            {
                case '<':
                    depth++;
                    break;
                case '>':
                    depth--;
                    break;
                case '.' when depth == 0:
                    rootEnd = i;
                    break;
            }

            if (rootEnd >= 0)
            {
                break;
            }
        }

        if (rootEnd < 0)
        {
            // Dotted name with no top-level dot (all dots live inside argument groups): nothing
            // to adjust at the root.
            return csharpName;
        }

        rootSegment = csharpName[..rootEnd];
        rest = csharpName[(rootEnd + 1)..];

        var genericIndex = rootSegment.IndexOf('<');
        var bareRoot = genericIndex >= 0 ? rootSegment[..genericIndex] : rootSegment;

        if (!arityIndex.TryGetValue(bareRoot, out var arity) || arity <= 0)
        {
            return csharpName;
        }

        // The file declares the outer type: the bare nested name resolves through the
        // enclosing-scope lookup (sibling/inner references alike) — for a pre-closed root too,
        // where it also sidesteps any constraint re-satisfaction.
        if (context.DeclaredTypeNames.Contains(bareRoot))
        {
            return rest;
        }

        if (genericIndex < 0)
        {
            // Unclosed root: close it with constraint-satisfied arguments.
            return bareRoot
                + "<" + BuildRawArguments(context, bareRoot, arity) + ">"
                + "." + rest;
        }

        // Pre-closed root (dynamic nested-type row): never re-close. Re-satisfy plain object
        // arguments against the constraint index so the closed form obeys the same bounds the
        // unclosed path honors. Template placeholders ($N) are consumed by the template
        // expander and are left untouched.
        var argumentText = rootSegment.Substring(genericIndex + 1, rootSegment.Length - genericIndex - 2);

        if (argumentText.Contains('$'))
        {
            return csharpName;
        }

        var args = SplitTopLevelTypeArguments(argumentText);

        if (args.Count != arity)
        {
            // Arg count mismatch with the registered arity: leave the pre-closed form alone.
            return csharpName;
        }

        string?[] satisfied = new string?[args.Count];
        var changed = false;

        for (var i = 0; i < args.Count; i++)
        {
            var arg = args[i].Trim();

            if (!string.Equals(arg, "object", StringComparison.Ordinal))
            {
                continue;
            }

            var bound = TryGetConstraintBound(context, bareRoot, i);

            if (string.IsNullOrWhiteSpace(bound))
            {
                continue;
            }

            satisfied[i] = SatisfyBound(context, bound, bareRoot);
            changed = true;
        }

        if (!changed)
        {
            return csharpName;
        }

        var rebuilt = new List<string>(args.Count);

        for (var i = 0; i < args.Count; i++)
        {
            rebuilt.Add(satisfied[i] ?? args[i].Trim());
        }

        return bareRoot + "<" + string.Join(", ", rebuilt) + ">" + "." + rest;
    }

    /// <summary>
    /// Exposes the raw-reference argument fill (per-position constraint satisfaction) for
    /// nested-of-generic-outer closing.
    /// </summary>
    internal static string BuildSatisfiedTypeArguments(ConversionContext context, string ownerSimpleName, int arity)
        => BuildRawArguments(context, ownerSimpleName, arity);

    /// <summary>
    /// Fills a raw generic reference's type arguments position by position, CONSISTENTLY.
    /// Pass 1 satisfies each position's bound independently (the historical behavior). Pass 2
    /// re-satisfies positions whose bound references SIBLING positions (the driver stores them
    /// as <c>@P{j}@</c> tokens) against the arguments already chosen for those positions —
    /// independent satisfaction made <c>ConfiguredFeature&lt;FC, F&gt;</c> close FC to a stand-in
    /// while F's bound <c>Feature&lt;FC&gt;</c> closed to a different instantiation, and C# rejects
    /// the disagreement (CS0311). Tokens for positions not (yet) resolved — self and forward
    /// references — are left for the stand-in machinery, which degrades them to <c>object</c>.
    /// </summary>
    private static string BuildRawArguments(ConversionContext context, string ownerSimpleName, int arity)
    {
        var bounds = new string?[arity];
        var args = new string[arity];

        for (var i = 0; i < arity; i++)
        {
            bounds[i] = TryGetConstraintBound(context, ownerSimpleName, i);
            args[i] = string.IsNullOrWhiteSpace(bounds[i])
                ? "object"
                : SatisfyBound(context, bounds[i]!, ownerSimpleName);
        }

        for (var i = 0; i < arity; i++)
        {
            var bound = bounds[i];

            if (string.IsNullOrWhiteSpace(bound) || !SiblingPositionTokenRegex.IsMatch(bound))
            {
                continue;
            }

            // Substitute back-referenced positions (j != i, arg known and not a stand-in token)
            // with the chosen args; leave @SELF@ and forward/stand-in tokens for the stand-in
            // machinery, which degrades them to the CRTP fixpoint name / object.
            var substituted = SiblingPositionTokenRegex.Replace(
                bound,
                m =>
                {
                    if (!int.TryParse(m.Groups[1].Value, out var index)
                        || index >= arity
                        || index == i
                        || args[index].StartsWith('@'))
                    {
                        return m.Value;
                    }

                    return args[index];
                });

            if (!string.Equals(substituted, bound, StringComparison.Ordinal))
            {
                args[i] = SatisfyBound(context, substituted, ownerSimpleName);
            }
        }

        // Synthesized arguments are emitted verbatim into this file: a bare tree-type name
        // (e.g. a constraint bound "Entity" chosen for a raw reference) must be resolvable,
        // so register its known namespace as a mapping using. Non-identifier args (stand-in
        // tokens, generic text, primitives) are skipped — registration is a no-op for them.
        foreach (var arg in args)
        {
            if (arg.Length > 0 && arg.All(c => char.IsLetterOrDigit(c) || c is '_' or '$'))
            {
                CollectKnownTypeNamespaceUsing(context, arg);
            }
        }

        return string.Join(", ", args);
    }

    /// <summary>
    /// C# types that satisfy the standard self-referential constraint shapes the tree scan
    /// stores (the generic shape <c>T : IComparable&lt;T&gt;</c> is stored as the bare bound
    /// "IComparable"; <c>int</c> implements <c>IComparable&lt;int&gt;</c> and is a safe stand-in).
    /// </summary>
    private static readonly Dictionary<string, string> BclConstraintSatisfiers = new(StringComparer.Ordinal)
    {
        ["IComparable"] = "int",
    };

    private const string SelfPlaceholderToken = "@SELF@";
    private const int MaxConstraintClosureDepth = 3;

    /// <summary>
    /// Sibling type-parameter position tokens (@P0@, @P1@, ...) that the driver pre-substitutes
    /// into stored constraint bounds; the raw-argument closure replaces them with the argument
    /// chosen for that position so all positions of one instantiation agree.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex SiblingPositionTokenRegex =
        new(@"@P(\d+)@", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Produces a C# type argument that satisfies the constraint bound registered for the
    /// owner's type parameter at <paramref name="position"/>. Java's erasure lets a wildcard
    /// stand for "some type obeying the bound"; C# demands a concrete type. Resolution order:
    /// 1) self-referential bounds prefer the enclosing declaration's own matching type
    /// parameter; 2) known BCL satisfiers; 3) a synthesized nested stand-in class that
    /// implements the bound's own constraint closure.
    /// </summary>
    private static string SatisfyConstraintBound(ConversionContext context, string ownerSimpleName, int position)
    {
        var bound = TryGetConstraintBound(context, ownerSimpleName, position);

        if (string.IsNullOrWhiteSpace(bound))
        {
            return "object";
        }

        return SatisfyBound(context, bound, ownerSimpleName);
    }

    private static string SatisfyBound(ConversionContext context, string bound, string ownerSimpleName)
        => SatisfyBound(context, bound, ownerSimpleName, null);

    private static string SatisfyBound(ConversionContext context, string bound, string ownerSimpleName, HashSet<int>? resolvingTokens)
    {
        var boundLeaf = ExtractSimpleTypeName(bound) ?? bound;
        var ownerLeaf = ExtractSimpleTypeName(ownerSimpleName) ?? ownerSimpleName;

        // Self-referential CRTP (class Owner<S, T extends Owner<S, T>>): inside the owner's own
        // declaration the owner's own parameter whose bound names the owner satisfies the rule
        // (T : Owner<S, T> is satisfied by T itself). Outside the owner, no closed form exists
        // in the BCL — synthesize a stand-in.
        if (string.Equals(boundLeaf, ownerLeaf, StringComparison.Ordinal))
        {
            var enclosing = FindSelfBoundTypeParameter(context, ownerLeaf);

            if (enclosing is not null)
            {
                return enclosing;
            }

            return CreateConstraintPlaceholder(context, boundLeaf);
        }

        if (BclConstraintSatisfiers.TryGetValue(boundLeaf, out var satisfier))
        {
            return satisfier;
        }

        // A non-sealed CLASS bound satisfies its own constraint (a type implements itself);
        // close its generic shape. The FULL bound text is closed — closing only the leaf
        // discarded the bound's own arguments and re-derived them from the bound type's own
        // declared bounds, disagreeing with the sibling positions' chosen arguments (e.g. a
        // wildcard FC closed to a stand-in while F's bound Feature<@P0@> re-closed to
        // Feature<FeatureConfiguration> — CS0311). Sibling position tokens resolve against the
        // owner's other positions, with a cycle guard.
        var closed = CloseConstraintBound(context, bound, depth: 0, ownerSimpleName, resolvingTokens ?? []);

        if (!closed.Contains(SelfPlaceholderToken)
            && !IsKnownInterfaceBound(context, boundLeaf))
        {
            return closed;
        }

        return CreateConstraintPlaceholder(context, boundLeaf);
    }

    /// <summary>
    /// Resolves a sibling position token (@Pj@) against the owner's other positions. The
    /// stand-in deduplication (per closed template) guarantees the same argument value the
    /// sibling position itself received. A cycle (mutual token references) degrades to object.
    /// </summary>
    private static string ResolveSiblingToken(ConversionContext context, string ownerSimpleName, int position, HashSet<int> resolvingTokens)
    {
        if (ownerSimpleName is null || position < 0 || !resolvingTokens.Add(position))
        {
            return "object";
        }

        try
        {
            var bound = TryGetConstraintBound(context, ownerSimpleName, position);

            return string.IsNullOrWhiteSpace(bound)
                ? "object"
                : SatisfyBound(context, bound, ownerSimpleName, resolvingTokens);
        }
        finally
        {
            resolvingTokens.Remove(position);
        }
    }

    private static bool IsKnownInterfaceBound(ConversionContext context, string boundLeaf)
    {
        if (context.Options.KnownInterfaceNames is not { Count: > 0 } known)
        {
            return false;
        }

        return known.Contains(boundLeaf) || known.Contains(boundLeaf.ToLowerInvariant());
    }

    /// <summary>
    /// True when a bare bound argument is neither in scope, nor a tree-declared type, nor a
    /// BCL-primitive-shaped name — i.e. it is a dangling type parameter of the bound's own
    /// declaration that no call site can reproduce.
    /// </summary>
    private static bool IsUnknownBareIdentifier(ConversionContext context, string identifier)
    {
        // Synthesized stand-in names (__SatisfyN) produced by this same closure: re-satisfying a
        // tokenized bound whose sibling arg is an existing stand-in must keep the stand-in, not
        // degrade it to object (which would re-violate the constraint).
        if (identifier.StartsWith("__Satisfy", StringComparison.Ordinal))
        {
            return false;
        }

        if (char.IsLower(identifier[0]) && identifier.Length == 1)
        {
            // Single-letter identifiers are almost always type parameters.
            return true;
        }

        if (context.Options.KnownTypeNames is { Count: > 0 } known
            && (known.Contains(identifier) || known.Contains(identifier.ToLowerInvariant())))
        {
            return false;
        }

        // Primitive/BCL-shaped names keep their spelling.
        return !char.IsUpper(identifier[0]);
    }

    /// <summary>
    /// Finds an in-scope type parameter whose bound names <paramref name="ownerLeaf"/> — the
    /// enclosing class's own self-referential parameter (T in class Owner&lt;S, T extends
    /// Owner&lt;S, T&gt;&gt;), which satisfies the owner's constraint by construction.
    /// </summary>
    private static string? FindSelfBoundTypeParameter(ConversionContext context, string ownerLeaf)
    {
        foreach (var node in context.TypeParameterNodes)
        {
            foreach (var bound in node.getTypeBound().ToList<com.github.javaparser.ast.type.ClassOrInterfaceType>() ?? [])
            {
                var boundLeaf = ExtractSimpleTypeName(bound.getNameAsString()) ?? bound.getNameAsString();

                if (string.Equals(boundLeaf, ownerLeaf, StringComparison.Ordinal))
                {
                    return node.getNameAsString();
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Builds the closed form of a constraint bound, recursively filling the bound's own type
    /// arguments. The driver stores the FULL bound text with the declaring parameter's own
    /// occurrences pre-substituted by <see cref="SelfPlaceholderToken"/> (e.g.
    /// <c>ArgumentBuilder&lt;S, @SELF@&gt;</c>): the token propagates through recursion and
    /// becomes the stand-in's own name (the CRTP fixpoint), sibling positions resolve as
    /// <c>@P{j}@</c> tokens against the owner's other positions (when an owner is known —
    /// stand-in generation passes none and degrades them to <c>object</c>), wildcard arguments
    /// close to <c>object</c>, and deeper bounds close recursively up to
    /// <see cref="MaxConstraintClosureDepth"/>.
    /// </summary>
    private static string CloseConstraintBound(ConversionContext context, string boundText, int depth, string? ownerSimpleName = null, HashSet<int>? resolvingTokens = null)
    {
        if (depth > MaxConstraintClosureDepth)
        {
            return boundText;
        }

        // A bound that is JUST a sibling position token (`T extends U`): with an owner, the
        // sibling resolves against that owner's other positions (consistent closure); inside a
        // stand-in's own base list (no owner) the neutral fill applies.
        if (SiblingPositionTokenRegex.IsMatch(boundText)
            && boundText.Trim().StartsWith("@P")
            && boundText.Trim().EndsWith('@')
            && !boundText.Contains('<'))
        {
            if (ownerSimpleName is not null
                && int.TryParse(SiblingPositionTokenRegex.Match(boundText).Groups[1].Value, out var tokenPosition))
            {
                return ResolveSiblingToken(context, ownerSimpleName, tokenPosition, resolvingTokens ?? []);
            }

            return "object";
        }

        if (TrySplitBoundText(boundText, out var boundLeaf, out var argumentText))
        {
            var args = SplitTopLevelTypeArguments(argumentText);
            var closed = new List<string>(args.Count);

            foreach (var arg in args)
            {
                var trimmed = arg.Trim();

                // Java wildcard arguments (`Comparable<? super T>`) carry no concrete type;
                // any closed instantiation works, and object is the neutral choice.
                if (trimmed.Contains('?'))
                {
                    closed.Add("object");
                    continue;
                }

                if (trimmed == SelfPlaceholderToken)
                {
                    closed.Add(SelfPlaceholderToken);
                    continue;
                }

                // Sibling position tokens (@P0@, ...): resolve against the owner's other
                // positions when the owner is known; degrade to object inside a stand-in's own
                // base list (no owner context).
                if (SiblingPositionTokenRegex.IsMatch(trimmed) && !trimmed.Contains('<') && !trimmed.Contains('.'))
                {
                    if (ownerSimpleName is not null
                        && int.TryParse(SiblingPositionTokenRegex.Match(trimmed).Groups[1].Value, out var siblingPosition))
                    {
                        closed.Add(ResolveSiblingToken(context, ownerSimpleName, siblingPosition, resolvingTokens ?? []));
                    }
                    else
                    {
                        closed.Add("object");
                    }

                    continue;
                }

                var argLeaf = ExtractSimpleTypeName(trimmed) ?? trimmed;

                // Sibling/owner parameter names resolve in scope at the reference site — keep
                // them verbatim (the CRTP fixpoint the Java source encoded).
                if (!trimmed.Contains('<') && !trimmed.Contains('.')
                    && context.TypeParameterNames.Contains(trimmed))
                {
                    closed.Add(trimmed);
                    continue;
                }

                // A bare identifier that is neither in scope nor a tree-known type is a
                // dangling bound parameter of the bound's own declaration — no closed form
                // exists; object is the neutral fill.
                if (!trimmed.Contains('<') && !trimmed.Contains('.')
                    && IsUnknownBareIdentifier(context, trimmed))
                {
                    closed.Add("object");
                    continue;
                }

                closed.Add(CloseConstraintBound(context, trimmed, depth + 1, ownerSimpleName, resolvingTokens));
            }

            return boundLeaf + "<" + string.Join(", ", closed) + ">";
        }

        // No argument list (bare bound): close the bound's own type arguments position by
        // position. The constraint index stores the bound TEXTS of the owner's parameters
        // (self occurrences pre-substituted). A self-referential bound IS the correct closure
        // shape (`CommandBuilder<S, @SELF@>` — its sibling positions carry the enclosing
        // declaration's parameters, which resolve in scope at the reference site), so it is
        // used directly rather than reconstructing arguments from per-parameter bounds.
        if (context.Options.GenericConstraintIndex is { Count: > 0 } boundIndex
            && boundIndex.TryGetValue(boundText, out var boundBounds))
        {
            var leaf = ExtractSimpleTypeName(boundText) ?? boundText;

            foreach (var bound in boundBounds)
            {
                if (string.IsNullOrWhiteSpace(bound))
                {
                    continue;
                }

                var boundRoot = ExtractFirstSegmentName(bound) ?? bound;

                if (bound.Contains(SelfPlaceholderToken)
                    && string.Equals(boundRoot, leaf, StringComparison.Ordinal))
                {
                    // CRTP: the owner's own instantiation shape comes from the bound itself.
                    return CloseConstraintBound(context, bound, depth + 1, ownerSimpleName, resolvingTokens);
                }
            }

            var own = new List<string>(boundBounds.Count);

            foreach (var bound in boundBounds)
            {
                if (string.IsNullOrWhiteSpace(bound))
                {
                    own.Add("object");
                    continue;
                }

                own.Add(CloseConstraintBound(context, bound, depth + 1, ownerSimpleName, resolvingTokens));
            }

            return leaf + "<" + string.Join(", ", own) + ">";
        }

        var arity = 0;

        if (context.Options.KnownGenericArity is { } arityIndex)
        {
            if (!arityIndex.TryGetValue(boundText, out arity)
                && BclRawTypeArguments.TryGetValue(boundText, out var bclArguments))
            {
                arity = bclArguments.Split(',').Length;
            }
        }

        return arity > 0
            ? boundText + "<" + string.Join(", ", Enumerable.Repeat("object", arity)) + ">"
            : boundText;
    }

    /// <summary>
    /// Splits a (possibly generic) bound into its leaf name and raw argument text, e.g.
    /// <c>ArgumentBuilder&lt;S, @SELF@&gt;</c> → ("ArgumentBuilder", "S, @SELF@"). Returns false
    /// when the bound carries no argument list.
    /// </summary>
    private static bool TrySplitBoundText(string boundText, out string leaf, out string argumentText)
    {
        leaf = argumentText = "";
        var open = boundText.IndexOf('<');

        if (open < 0)
        {
            leaf = boundText;
            return false;
        }

        if (!TryFindMatchingAngleBracket(boundText, open, out var close))
        {
            leaf = boundText;
            return false;
        }

        leaf = boundText[..open].Trim();
        argumentText = boundText.Substring(open + 1, close - open - 1);
        return true;
    }

    /// <summary>
    /// Synthesizes (once per closed signature) a nested stand-in class whose base list closes
    /// the bound's constraint shape, e.g.
    /// <c>public abstract class __Satisfy1 : ArgumentBuilder&lt;object, __Satisfy1&gt;</c>.
    /// The class is abstract: it only ever appears as a type argument (never instantiated), so
    /// unimplemented interface/abstract members are harmless and need no stubs. Returns the
    /// stand-in's name.
    /// </summary>
    private static string CreateConstraintPlaceholder(ConversionContext context, string boundLeaf)
    {
        var closedTemplate = CloseConstraintBound(context, boundLeaf, depth: 0);

        return context.RegisterPlaceholderType(closedTemplate, name =>
        {
            var closed = closedTemplate.Replace(SelfPlaceholderToken, name);
            var declaration = SyntaxFactory.ClassDeclaration(name)
                .AddModifiers(
                    SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                    SyntaxFactory.Token(SyntaxKind.AbstractKeyword))
                .AddBaseListTypes(SyntaxFactory.SimpleBaseType(SyntaxFactory.ParseTypeName(closed)));

            // A tree-INTERFACE bound requires the stand-in to re-declare every interface member
            // abstract (`abstract class C : I { }` is CS0535 — C# grants abstract classes no
            // exemption). Reshaped interfaces are abstract classes with virtual stubs and need
            // no re-declaration. Signature parts come from the host's tree scan, pipe-separated:
            // "typeParams|returnType|name|params".
            if (context.Options.InterfaceMemberSignatures is { Count: > 0 } signaturesByName
                && signaturesByName.TryGetValue(boundLeaf.ToLowerInvariant(), out var signatures)
                && signatures.Count > 0
                && context.Options.ReshapeableInterfaceNames is { } reshapeable
                && !reshapeable.Contains(boundLeaf.ToLowerInvariant()))
            {
                foreach (var signature in signatures)
                {
                    var member = BuildAbstractStandInMember(context, signature, boundLeaf, name);

                    if (member is not null)
                    {
                        declaration = declaration.AddMembers(member);
                    }
                }
            }

            return declaration.WithLeadingTrivia(
                SyntaxFactory.Comment(
                    $"/* REVIEW(java-to-csharp): synthesized stand-in for constraint bound '{closedTemplate.Replace(SelfPlaceholderToken, "T")}' */"));
        });
    }

    /// <summary>
    /// Builds an abstract member re-declaration for a stand-in class from a pipe-separated
    /// signature part ("typeParams|returnType|name|params" in Java spelling). Unparseable or
    /// exotic headers (generic method parameters whose bound text drifted, etc.) are skipped —
    /// a missing abstract member re-surfaces as CS0535 on that member instead of blocking the
    /// whole stand-in.
    /// </summary>
    private static MethodDeclarationSyntax? BuildAbstractStandInMember(
        ConversionContext context, string signaturePart, string boundLeaf, string standInName)
    {
        if (!TryBuildSignatureMethod(context, signaturePart, out var method))
        {
            return null;
        }

        return method
            .AddModifiers(
                SyntaxFactory.Token(SyntaxKind.PublicKeyword),
                SyntaxFactory.Token(SyntaxKind.AbstractKeyword))
            .WithSemicolonToken(SyntaxFactory.Token(SyntaxKind.SemicolonToken));
    }

    /// <summary>
    /// Parses a pipe-separated signature part ("typeParams|returnType|name|params" in Java
    /// spelling) into a bare method declaration (no modifiers, no body). Returns false when
    /// the header is unparseable, the parts are statement text rather than a type (signatures
    /// are captured from tree scans and can pick up method-body invocations), or the type
    /// conversion failed.
    /// </summary>
    internal static bool TryBuildSignatureMethod(
        ConversionContext context, string signaturePart, out MethodDeclarationSyntax method)
    {
        method = null!;
        var parts = signaturePart.Split('|');

        if (parts.Length != 4)
        {
            return false;
        }

        var typeParams = parts[0];
        var returnTypeText = parts[1];
        var methodName = parts[2];
        var paramsText = parts[3];

        if (string.IsNullOrWhiteSpace(returnTypeText) || string.IsNullOrWhiteSpace(methodName))
        {
            return false;
        }

        // Dotted names are never method identifiers — a capture with a dotted "name" is garbage.
        if (methodName.Contains('.'))
        {
            return false;
        }

        if (!IsValidTypeText(returnTypeText))
        {
            return false;
        }

        try
        {
            var returnSyntax = SyntaxFactory.ParseTypeName(ConvertType(context, returnTypeText).Trim());

            var parameterSyntaxes = new List<ParameterSyntax>();

            if (paramsText.Length > 0)
            {
                foreach (var raw in SplitTopLevelTypeArguments(paramsText))
                {
                    var trimmed = raw.Trim();

                    if (trimmed.Length == 0)
                    {
                        continue;
                    }

                    // "Type name" — the last whitespace-separated token is the parameter name;
                    // everything before it (kept together, since types may contain spaces in
                    // wildcards/bounds) is the type.
                    var lastSpace = trimmed.LastIndexOf(' ');

                    if (lastSpace < 0)
                    {
                        continue;
                    }

                    var rawParamName = trimmed[(lastSpace + 1)..].Trim();

                    // Garbage captures (statement text with eaten string literals) produce
                    // parameter names like "+key" — reject anything not identifier-shaped.
                    if (rawParamName.Length == 0
                        || !(char.IsLetterOrDigit(rawParamName[0]) || rawParamName[0] is '_' or '$'))
                    {
                        continue;
                    }

                    var typeText = StripParamTypePrefixes(trimmed[..lastSpace]);

                    // Java varargs ("Type... name") become C# params arrays.
                    var isVarArgs = typeText.EndsWith("...", StringComparison.Ordinal);

                    if (isVarArgs)
                    {
                        typeText = typeText[..^3].TrimEnd();
                    }

                    if (typeText.Length == 0 || !IsValidTypeText(typeText))
                    {
                        continue;
                    }

                    var modifiers = SyntaxFactory.TokenList();

                    if (isVarArgs)
                    {
                        modifiers = modifiers.Add(SyntaxFactory.Token(SyntaxKind.ParamsKeyword));
                    }

                    var convertedType = ConvertType(context, typeText).Trim();

                    if (isVarArgs)
                    {
                        convertedType += "[]";
                    }

                    parameterSyntaxes.Add(SyntaxFactory.Parameter(
                        attributeLists: default,
                        modifiers: modifiers,
                        type: SyntaxFactory.ParseTypeName(convertedType),
                        identifier: SyntaxFactory.Identifier(EscapeIdentifier(rawParamName)),
                        @default: null));
                }
            }

            // Non-empty parameter text that produced no valid parameters is capture garbage:
            // emitting an accidental wrong-arity member could collide with a real overload.
            if (parameterSyntaxes.Count == 0 && paramsText.Trim().Length > 0)
            {
                return false;
            }

            method = SyntaxFactory.MethodDeclaration(returnSyntax, methodName)
                .WithParameterList(SyntaxFactory.ParameterList(
                    SyntaxFactory.SeparatedList(parameterSyntaxes)));

            if (typeParams.StartsWith('<') && typeParams.EndsWith('>') && typeParams.Length > 2)
            {
                var names = typeParams[1..^1]
                    .Split(',')
                    .Select(t => t.Trim().Split(' ')[0].Trim())
                    .Where(t => t.Length > 0)
                    .Select(t => SyntaxFactory.TypeParameter(t))
                    .ToArray();

                if (names.Length > 0)
                {
                    method = method.AddTypeParameterListParameters(names);
                }
            }

            return true;
        }
        catch (Exception)
        {
            // Signature conversion failure: skip this member (CS0535 on that member only).
            return false;
        }
    }

    /// <summary>
    /// Statement keywords that can start a captured "signature" line when the capture regexes
    /// match method-body content (e.g. `throw new IllegalStateException(...)` captured with a
    /// return type of "throw new"). A type never starts with one of these.
    /// </summary>
    private static readonly string[] StatementKeywords =
    [
        "throw", "new", "return", "if", "else", "for", "while", "do", "switch", "case",
        "break", "continue", "try", "catch", "finally", "assert", "synchronized", "this", "super"
    ];

    /// <summary>
    /// True when the text is plausibly a Java type spelling: letters/digits and the type
    /// punctuation (dots, generics, arrays, wildcard commas). Parentheses, plus signs and
    /// annotation markers — the residue of method-body statements with eaten string literals —
    /// are rejected outright, as are statement keywords in leading position.
    /// </summary>
    private static bool IsValidTypeText(string text)
    {
        var trimmed = text.Trim();

        if (trimmed.Length == 0
            || trimmed.EndsWith('.')
            || !trimmed.All(c => char.IsLetterOrDigit(c)
                || c is '_' or '$' or '.' or '<' or '>' or ',' or '?' or '[' or ']' or ' '))
        {
            return false;
        }

        var firstWord = trimmed
            .Split(new[] { ' ', '<' }, StringSplitOptions.RemoveEmptyEntries)[0]
            .TrimEnd('.');

        return !StatementKeywords.Contains(firstWord, StringComparer.Ordinal);
    }

    /// <summary>
    /// Strips Java parameter prefixes that are not part of the type: annotations
    /// (<c>@Nullable</c>, <c>@Word(...)</c>) and the <c>final</c> modifier.
    /// </summary>
    private static string StripParamTypePrefixes(string typeText)
    {
        var text = typeText.Trim();

        while (text.Length > 0)
        {
            if (text.StartsWith('@'))
            {
                var nameEnd = 1;

                while (nameEnd < text.Length
                    && (char.IsLetterOrDigit(text[nameEnd]) || text[nameEnd] is '_' or '$'))
                {
                    nameEnd++;
                }

                if (nameEnd < text.Length && text[nameEnd] == '(')
                {
                    var close = text.IndexOf(')', nameEnd);

                    if (close < 0)
                    {
                        // Unbalanced capture garbage: drop the rest.
                        return string.Empty;
                    }

                    text = text[(close + 1)..].TrimStart();
                }
                else
                {
                    text = text[nameEnd..].TrimStart();
                }

                continue;
            }

            if (text.StartsWith("final ", StringComparison.Ordinal))
            {
                text = text[6..].TrimStart();
                continue;
            }

            break;
        }

        return text;
    }

    /// <summary>
    /// Returns the first segment of a (possibly dotted, possibly generic) type-name string,
    /// e.g. "Outer" for "Outer.Inner&lt;X&gt;" and "com" for "com.example.Foo". Returns null when
    /// the string is empty. Used as the declared-type anchor: only a reference that STARTS with a
    /// locally declared type keeps its spelling; dotted foreign references must resolve through
    /// the mapping index even when their leaf matches a local name.
    /// </summary>
    private static string? ExtractFirstSegmentName(string typeName)
    {
        var genericIndex = typeName.IndexOf('<');
        var beforeGenerics = genericIndex >= 0 ? typeName[..genericIndex] : typeName;
        var firstSegment = beforeGenerics.Trim().Split('.')[0].Trim();

        return firstSegment.Length > 0 ? firstSegment : null;
    }

    /// <summary>
    /// Returns the simple (unqualified, non-generic) name portion of a type string,
    /// e.g. "Double" for "Double&lt;S&gt;" and "Map.Entry" for "java.util.Map.Entry".
    /// Returns null when the string is empty.
    /// </summary>
    private static string? ExtractSimpleTypeName(string typeName)
    {
        var genericIndex = typeName.IndexOf('<');
        var baseName = genericIndex >= 0 ? typeName[..genericIndex] : typeName;
        baseName = baseName.Trim().Split('.').Last().Trim();

        return baseName.Length > 0 ? baseName : null;
    }

    public static string ConvertType(ConversionContext context, Type type)
        => ConvertType(context, CompactTypeString(type.toString()));

    public static string ConvertTypeOf(ConversionContext context, Ast.nodeTypes.NodeWithType typedNode)
        => ConvertType(context, CompactTypeString(typedNode.getType().toString()));

    public static TypeSyntax ConvertTypeSyntax(ConversionContext context, Type type, int arrayRank)
    {
        if (type is ArrayType arrayType)
        {
            if (arrayRank > 0 && arrayType.getArrayLevel() != arrayRank)
            {
                throw new ArgumentException("Given array rank does not match the array level of the type", nameof(arrayRank));
            }

            arrayRank = arrayType.getArrayLevel();
            Type elementType;

            while ((elementType = arrayType.getElementType()) is ArrayType nestedArrayType)
            {
                arrayType = nestedArrayType;
            }

            return ConvertTypeSyntax(context, elementType, arrayRank);
        }

        // Generic class/interface types must convert STRUCTURALLY (name mapped, then each
        // argument converted separately): mapping the whole rendered string as one blob breaks
        // nested mapped generics - a fastutil row like Object2IntMap<K> -> Dictionary<$1, int>
        // inside Map<UUID, Object2IntMap<GossipType>> would have its placeholders expanded with
        // raw fallbacks while the argument group is re-emitted as a separate bracket group.
        if (arrayRank == 0 && type is ClassOrInterfaceType classOrInterfaceType)
        {
            return GetSyntaxFromType(context, classOrInterfaceType, synthesizeRawArguments: true);
        }

        return arrayRank == 0
            ? SyntaxFactory.ParseTypeName(ConvertType(context, type))
            : ConvertArrayTypeSyntax(context, type, arrayRank);
    }

    public static ArrayTypeSyntax ConvertArrayTypeSyntax(ConversionContext context, Type type, int arrayRank)
    {
        var rankSpecifiers = SyntaxFactory.ArrayRankSpecifier(
            SyntaxFactory.SeparatedList<ExpressionSyntax>(
                Enumerable.Repeat(SyntaxFactory.OmittedArraySizeExpression(), arrayRank)
            ));

        return SyntaxFactory.ArrayType(ConvertTypeSyntax(context, type, 0))
            .WithRankSpecifiers(SyntaxFactory.SingletonList(rankSpecifiers));
    }

    public static TypeSyntax ConvertTypeSyntax(Type type, int arrayRank)
    {
        if (type is ArrayType arrayType)
        {
            if (arrayRank > 0 && arrayType.getArrayLevel() != arrayRank)
            {
                throw new ArgumentException("Given array rank does not match the array level of the type", nameof(arrayRank));
            }

            arrayRank = arrayType.getArrayLevel();
            Type elementType;

            while ((elementType = arrayType.getElementType()) is ArrayType nestedArrayType)
            {
                arrayType = nestedArrayType;
            }

            return ConvertTypeSyntax(elementType, arrayRank);
        }

        return arrayRank == 0
            ? SyntaxFactory.ParseTypeName(ConvertType(type))
            : ConvertArrayTypeSyntax(type, arrayRank);
    }

    public static ArrayTypeSyntax ConvertArrayTypeSyntax(Type type, int arrayRank)
    {
        var rankSpecifiers = SyntaxFactory.ArrayRankSpecifier(
            SyntaxFactory.SeparatedList<ExpressionSyntax>(
                Enumerable.Repeat(SyntaxFactory.OmittedArraySizeExpression(), arrayRank)
            ));

        return SyntaxFactory.ArrayType(ConvertTypeSyntax(type, 0))
            .WithRankSpecifiers(SyntaxFactory.SingletonList(rankSpecifiers));
    }

    public static string Capitalize(string name)
    {
        var parts = name.Split('.');

        var joined = string.Join(".", parts.Select(i =>
        {
            if (i.Length == 1)
                return i.ToUpper();
            else
                return i[0].ToString().ToUpper() + i[1..];
        }));

        return joined;
    }

    public static string EscapeIdentifier(string name)
    {
        // @ (C# Reference): https://docs.microsoft.com/en-us/dotnet/csharp/language-reference/tokens/verbatim
        // Only reserved keywords need escaping; contextual keywords (type, var, value, …) are valid identifiers.
        var kind = SyntaxFacts.GetKeywordKind(name);
        if (kind != SyntaxKind.None && SyntaxFacts.IsReservedKeyword(kind))
        {
            return "@" + name;
        }

        // as/in/is/out/ref appear in the C# reserved-word table but Roslyn reports them as contextual,
        // so IsReservedKeyword above misses them. They are not legal identifiers in any position
        // (foreach (String in in list), using (Stream out = …), fields/parameters named "is", …).
        // Match the treatment in EscapeMemberName so all identifier positions stay consistent.
        if (name is "as" or "in" or "is" or "out" or "ref")
        {
            return "@" + name;
        }

        // Java class-file names are the bytecode binary names (Outer$Inner); VineFlower also emits
        // synthetic lambda parameters such as x$0, rec$. '$' is not legal in a C# identifier, so it
        // must be sanitized. '$' cannot occur in a C# keyword, so this must be applied unconditionally.
        if (name.Contains('$'))
        {
            return name.Replace('$', '_');
        }

        return name;
    }

    /// <summary>
    /// Escapes a name used as a member access target (the right-hand side of a dot) when the
    /// raw Java name is a C# keyword. Roslyn rejects reserved keywords (<c>event</c>, <c>params</c>,
    /// …) and the contextual keywords <c>as</c>/<c>in</c>/<c>is</c>/<c>out</c>/<c>ref</c> in member
    /// position (<c>x.in(...)</c> is a parse error), while other contextual keywords (<c>value</c>,
    /// <c>var</c>, <c>select</c>, …) are valid identifiers there and stay untouched.
    /// </summary>
    public static string EscapeMemberName(string name)
    {
        var kind = SyntaxFacts.GetKeywordKind(name);
        if (kind == SyntaxKind.None)
        {
            // Java binary names / JNA generated members (sType$Default, x$0) may carry '$'.
            return name.Contains('$') ? name.Replace('$', '_') : name;
        }

        if (SyntaxFacts.IsReservedKeyword(kind) || name is "as" or "in" or "is" or "out" or "ref")
        {
            return "@" + name;
        }

        return name;
    }

    /// <summary>
    /// Replaces well-known Java method names with their C# equivalents
    /// (e.g. <c>hashCode</c> → <c>GetHashCode</c>, <c>equals</c> → <c>Equals</c>).
    /// This is core translation behavior, not a mapping operation.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex ObjectGenericRegex =
        new(@"\bobject\s*<", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex EraseBareTypeBoundsRegex =
        new(@"\s*\?\s*(?:extends|super)\s+|(?<=[\s<,]|^)\s*(?:extends|super)\s+", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Removes a balanced generic argument list that follows a mapped <c>object</c> root.
    /// A non-generic class such as <see cref="object"/> can never carry type arguments in C#, so
    /// <c>object&lt;...&gt;</c> output (a byproduct of Java classes with <tt>generic: true</tt>
    /// mappings to <c>object</c>, e.g. <c>java.util.stream.Collector</c>) is always invalid and
    /// must be collapsed back to <c>object</c>.
    /// </summary>
    public static string StripGenericsAfterObject(string typeName)
    {
        var match = ObjectGenericRegex.Match(typeName);
        if (!match.Success)
        {
            return typeName;
        }

        int open = typeName.IndexOf('<', match.Index);
        int depth = 1;
        int i = open + 1;
        while (i < typeName.Length && depth > 0)
        {
            if (typeName[i] == '<')
            {
                depth++;
            }
            else if (typeName[i] == '>')
            {
                depth--;
            }

            i++;
        }

        if (depth != 0)
        {
            return typeName;
        }

        return typeName[..match.Index] + "object" + typeName[i..];
    }

    /// <summary>
    /// Escapes C# keyword segments inside dotted type names.
    /// Roslyn cannot parse <c>com.google.common.base.Function</c> because <c>base</c> is a reserved
    /// keyword; escaping yields the valid <c>com.google.common.@base.Function</c>. Generic arguments
    /// and array rank specifiers carried by a segment are preserved verbatim.
    /// </summary>
    public static string EscapeDottedTypeNameSegments(string typeName)
    {
        if (typeName.IndexOf('.') < 0)
        {
            return typeName;
        }

        var segments = typeName.Split('.');
        for (int i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            int generics = segment.IndexOf('<');
            int arrayBrackets = segment.IndexOf('[');
            int cut = generics < 0 ? arrayBrackets : arrayBrackets < 0 ? generics : Math.Min(generics, arrayBrackets);

            string head = cut < 0 ? segment : segment[..cut];
            string tail = cut < 0 ? string.Empty : segment[cut..];

            string escaped = EscapeMemberName(head);
            segments[i] = escaped == head ? segment : escaped + tail;
        }

        return string.Join(".", segments);
    }

    /// <summary>
    /// Post-processes a converted type name: escapes keyword segments in dotted names, collapses
    /// <c>object&lt;...&gt;</c> generics, and erases bare <c>extends</c>/<c>super</c> bound markers
    /// that survive without a leading <c>?</c> (e.g. <c>Registry&lt;extends T&gt;</c> how built by
    /// constraint/type-argument emission). Kept separate from the conversion itself so all
    /// <c>ConvertType</c> overloads share the same shape guarantees.
    /// </summary>
    private static string NormalizeTypeName(string typeName)
    {
        typeName = EraseBareTypeBoundsRegex.Replace(typeName, "");
        typeName = EscapeDottedTypeNameSegments(StripGenericsAfterObject(typeName));
        return typeName;
    }

    public static string ReplaceCommonMethodNames(string name)
    {
        return name.ToLower() switch {
            "hashcode" => "GetHashCode",
            "getclass" => "GetType",
            "close" => "Dispose",
            "touppercase" => "ToUpper",
            "tolowercase" => "ToLower",
            "equals" => "Equals",
            "tostring" => "ToString",
            "trim" => "Trim",
            "readline" => "ReadLine",
            "read" => "Read",
            "ready" => "EndOfStream",
            "getmessage" => "Message",
            "abs" => "Abs",
            "compareto" => "CompareTo",
            _ => name,
        };
    }

    /// <summary>
    /// Normalizes common Java field names to their C# equivalents
    /// (e.g. <c>length</c> → <c>Length</c>).
    /// This is core translation behavior, not a mapping operation.
    /// </summary>
    public static string ReplaceCommonFieldNames(string name)
    {
        return name.ToLower() switch {
            "length" => "Length",
            _ => name,
        };
    }

    /// <summary>
    /// Replaces Java bare wildcards (<c>?</c>) and legacy <c>TWildcardTodo</c> placeholders with
    /// the current type's type parameter names when available. Applied to a raw Java type string
    /// <em>before</em> conversion so pattern matches such as <c>instanceof Person&lt;?&gt;</c> become
    /// <c>Person&lt;T&gt;</c> instead of <c>Person&lt;object&gt;</c>. Bounded wildcards
    /// (<c>? extends X</c> / <c>? super X</c>) are left untouched for the normal erasure path.
    /// </summary>
    public static string ReplaceWildcardsWithTypeParameters(string typeName, IList<string> typeParameterNames)
    {
        if (typeParameterNames.Count == 0)
        {
            return typeName;
        }

        int paramIndex = 0;
        while (paramIndex < typeParameterNames.Count)
        {
            if (typeName.Contains("TWildcardTodo", StringComparison.Ordinal))
            {
                typeName = ReplaceFirst(typeName, "TWildcardTodo", typeParameterNames[paramIndex]);
            }
            else
            {
                var match = BareWildcardRegex.Match(typeName);
                if (!match.Success)
                {
                    break;
                }

                typeName = string.Concat(typeName.AsSpan(0, match.Index), typeParameterNames[paramIndex], typeName.AsSpan(match.Index + match.Length));
            }

            paramIndex++;
        }

        return typeName;
    }

    private static readonly System.Text.RegularExpressions.Regex BareWildcardRegex = new(
        @"\?(?!\s*(?:extends|super)\b)",
        System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>
    /// Java functional interfaces that are mapped to BCL DELEGATES. A class that DERIVES one
    /// (Java <c>new Runnable() {...}</c>, <c>class X implements Predicate&lt;T&gt;</c>) cannot
    /// carry the delegate as a C# base — delegates are sealed (CS0509). For these bases the
    /// converter synthesizes a stand-in abstract class carrying the interface's method as an
    /// abstract member plus an implicit conversion to the mapped delegate, so subclasses
    /// compile AND remain assignable wherever the delegate is expected.
    /// Method shape: (java method name, return type — "void", a C# type, or "$k" for the k-th
    /// type argument, parameter count). Params/lambda args are positional T0..Tn-1 / arg0...
    /// </summary>
    private static readonly Dictionary<string, (string MethodName, string ReturnType, int ParameterCount, string? ExplicitParams)> DelegateInterfaceShapes =
        new(StringComparer.Ordinal)
        {
            ["Runnable"] = ("run", "void", 0, null),
            ["Predicate"] = ("test", "bool", 1, null),
            ["Function"] = ("apply", "$1", 1, null),
            ["Supplier"] = ("get", "$0", 0, null),
            ["Consumer"] = ("accept", "void", 1, null),
            ["Comparator"] = ("compare", "int", 2, null),
            ["UnaryOperator"] = ("apply", "$0", 1, null),
            ["BiFunction"] = ("apply", "$2", 2, null),
            ["BiConsumer"] = ("accept", "void", 2, null),
            ["BiPredicate"] = ("test", "bool", 2, null),
            ["UncaughtExceptionHandler"] = ("uncaughtException", "void", 2, "Thread arg0, Exception arg1"),
        };

    /// <summary>
    /// Attempts to replace a base type that maps to a BCL delegate with a synthesized stand-in
    /// abstract class (see <see cref="DelegateInterfaceShapes"/>). The stand-in is registered as
    /// a placeholder type owned by the type ENCLOSING the deriving type: C# name lookup walks
    /// outward through enclosing scopes, but a class's own base list can NOT see the class's own
    /// nested types (CS0246 there breaks the base chain and cascades CS0115 on every object
    /// override). The returned base entry references the stand-in with the reference's own type
    /// arguments.
    /// </summary>
    internal static bool TryCreateDelegateBaseReplacement(
        ConversionContext context,
        com.github.javaparser.ast.type.ClassOrInterfaceType javaType,
        out Microsoft.CodeAnalysis.CSharp.Syntax.BaseTypeSyntax baseType)
    {
        baseType = null!;
        var simpleName = javaType.getNameAsString();

        if (!DelegateInterfaceShapes.TryGetValue(simpleName, out var shape))
        {
            return false;
        }

        var typeArgs = javaType.getTypeArguments().FromOptional<com.github.javaparser.ast.NodeList>()?.ToList<com.github.javaparser.ast.type.Type>();
        var arity = typeArgs?.Count ?? 0;
        var standInParameterList = arity > 0 ? "<" + string.Join(", ", Enumerable.Range(0, arity).Select(i => "T" + i)) + ">" : "";
        var returnType = shape.ReturnType.StartsWith('$') ? "T" + shape.ReturnType[1..] : shape.ReturnType;
        var methodParams = shape.ExplicitParams
            ?? string.Join(", ", Enumerable.Range(0, shape.ParameterCount).Select(i => $"T{i} arg{i}"));
        var lambdaArgs = string.Join(", ", Enumerable.Range(0, shape.ParameterCount).Select(i => $"arg{i}"));
        var delegateTarget = ConvertType(context, javaType.toString()).Trim();

        // Two shapes: void-returning delegates wrap the call in a lambda; value-returning ones
        // forward the invocation directly.
        var operatorBody = shape.ReturnType == "void"
            ? $"() => instance.{shape.MethodName}({lambdaArgs})"
            : $"instance.{shape.MethodName}({lambdaArgs})";

        // Deduplicated per (owner, delegate shape): two references in one owner share a stand-in.
        var standInName = context.RegisterPlaceholderType(
            "delegate|" + simpleName + "|" + arity + "|" + delegateTarget,
            generated =>
            {
                var declaration =
                    "public abstract class " + generated + standInParameterList + "\n" +
                    "{\n" +
                    "    public abstract " + returnType + " " + shape.MethodName + "(" + methodParams + ");\n\n" +
                    "    public static implicit operator " + delegateTarget + "(" + generated + standInParameterList + " instance)\n" +
                    "    {\n" +
                    "        return " + operatorBody + ";\n" +
                    "    }\n" +
                    "}\n";

                var parsed = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseCompilationUnit(declaration)
                    .Members[0] as Microsoft.CodeAnalysis.CSharp.Syntax.ClassDeclarationSyntax;

                return parsed
                    ?? throw new InvalidOperationException(
                        "delegate stand-in declaration did not parse into a class");
            });

        var baseTypeText = arity > 0
            ? standInName + "<" + string.Join(", ", typeArgs!.Select(arg => ConvertTypeSyntax(context, arg, 0).ToString())) + ">"
            : standInName;

        baseType = Microsoft.CodeAnalysis.CSharp.SyntaxFactory.SimpleBaseType(
            Microsoft.CodeAnalysis.CSharp.SyntaxFactory.ParseTypeName(baseTypeText));
        return true;
    }

    private static string ReplaceFirst(string text, string search, string replace)
    {
        int index = text.IndexOf(search, StringComparison.Ordinal);
        return index < 0 ? text : string.Concat(text.AsSpan(0, index), replace, text.AsSpan(index + search.Length));
    }

    public static TypeSyntax GetSyntaxFromType(ClassOrInterfaceType type)
    {
        string typeName = ConvertType(type.getNameAsString());
        var typeArgs = type.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();

        TypeSyntax typeSyntax;

        if (typeArgs is { Count: > 0 })
        {
            // Rebuild the full generic form and route it through NormalizeTypeName so that roots
            // mapped to a non-generic C# type (e.g. object via java.util.stream.Collector) cannot
            // smuggle invalid arguments past the conversion (object<String> → object).
            var args = string.Join(", ", typeArgs.Select(t =>
                IsWildcardType(t) ? ConvertWildcardType(t.toString()) : ConvertType(t)));
            typeSyntax = SyntaxFactory.ParseTypeName(NormalizeTypeName(typeName + "<" + args + ">"));
        }
        else
        {
            typeSyntax = SyntaxFactory.ParseTypeName(typeName);
        }

        return typeSyntax;
    }

    /// <summary>
    /// Builds the dotted name of a (possibly nested) Java class/interface type. The scope chain is
    /// kept only while its segments refer to TYPES (a type declared in the file, a hoisted local
    /// class, or a mapped class) — <c>DSL.TypeReference</c> keeps its qualifier because the nested
    /// type is declared inside DSL, while <c>java.util.List</c> loses the package qualifier so the
    /// mapping machinery can resolve <c>List</c>. Emitting a bare leaf name for genuine nested
    /// types breaks the reference (CS0246) because the qualifier carries the declaring type.
    /// </summary>
    public static string GetFullTypeName(ConversionContext context, ClassOrInterfaceType type)
    {
        var parts = new List<string> { type.getNameAsString() };
        var current = (ClassOrInterfaceType?)type.getScope().FromOptional<ClassOrInterfaceType>();

        while (current is not null)
        {
            var scopeName = current.getNameAsString();

            if (!ScopeRefersToType(context, scopeName))
            {
                break;
            }

            parts.Add(scopeName);
            current = (ClassOrInterfaceType?)current.getScope().FromOptional<ClassOrInterfaceType>();
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    /// <summary>
    /// Builds the fully dotted name of a Java class/interface type WITHOUT the scope filtering
    /// of <see cref="GetFullTypeName"/> - package qualifiers are kept. Used as the fallback
    /// spelling for unmapped types so dictionary conversion, keyword-segment escaping
    /// (<c>com.google.common.base.Function</c>) and fully-qualified references keep working.
    /// </summary>
    private static string GetFullDottedNameIncludingPackages(ClassOrInterfaceType type)
    {
        var parts = new List<string> { type.getNameAsString() };
        var current = (ClassOrInterfaceType?)type.getScope().FromOptional<ClassOrInterfaceType>();

        while (current is not null)
        {
            parts.Add(current.getNameAsString());
            current = (ClassOrInterfaceType?)current.getScope().FromOptional<ClassOrInterfaceType>();
        }

        parts.Reverse();
        return string.Join(".", parts);
    }

    private static bool ScopeRefersToType(ConversionContext context, string scopeName)
    {
        if (context.DeclaredTypeNames.Contains(scopeName)
            || context.FindHoistedLocalClass(scopeName) is not null)
        {
            return true;
        }

        var index = context.Options.MappingIndex;
        if (index is not null
            && index.TryGetBySimpleName(scopeName, out _, out var ambiguous)
            && !ambiguous)
        {
            return true;
        }

        // A scope name that exists as a type elsewhere in the host's tree must keep its
        // qualifier: the reference is a nested-type access (DebugRenderer.SimpleDebugRenderer,
        // DSL.TypeReference) whose outer segment is declared in another file. Dropping the
        // qualifier would emit a bare nested name that cannot resolve. The host may feed the
        // set in original or lowercased casing, so both are probed.
        if (context.Options.KnownTypeNames is { Count: > 0 } known
            && (known.Contains(scopeName) || known.Contains(scopeName.ToLowerInvariant())))
        {
            return true;
        }

        return false;
    }

    /// <summary>
    /// Builds a C# type syntax from a Java class/interface type, resolving the type name (and its
    /// generic arguments) through the mapping index when configured.
    /// </summary>
    public static TypeSyntax GetSyntaxFromType(ConversionContext context, ClassOrInterfaceType type)
        => GetSyntaxFromType(context, type, synthesizeRawArguments: false);

    public static TypeSyntax GetSyntaxFromType(ConversionContext context, ClassOrInterfaceType type, bool synthesizeRawArguments)
    {
        var typeArgs = type.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();

        // No raw-generic synthesis here: with absent type arguments this may be a diamond
        // ("new ArrayList<>()") whose arguments the caller infers from context, and injected
        // arguments would change the syntax shape the caller matches on (GenericNameSyntax).
        // Callers that emit a plain declaration (fields, parameters) pass
        // synthesizeRawArguments: true so raw references still gain their arity defaults.
        // The leaf name resolves through the mapping index VERBATIM (placeholders intact) so
        // template rows can consume the reference's own arguments below; the string-level
        // conversion would have already expanded them with raw fallbacks. When nothing maps,
        // the full dotted spelling (package qualifiers kept) goes through the normal string
        // conversion, preserving dictionary fallbacks and keyword-segment escaping.
        var strippedName = GetFullTypeName(context, type);
        var dottedName = GetFullDottedNameIncludingPackages(type);

        // A leaf that names a type declared in this file (or an in-scope type parameter) must
        // bypass mapping resolution entirely: a mapping row sharing the leaf's simple name would
        // otherwise hijack the reference (e.g. a type parameter named Value colliding with a
        // SomeType.Value nested-class row).
        var leafName = strippedName.Substring(strippedName.LastIndexOf('.') + 1);
        var isLocalLeaf = context.DeclaredTypeNames.Contains(leafName)
            || context.TypeParameterNames.Contains(leafName)
            || context.FindHoistedLocalClass(leafName) is not null;

        // Same namespace-using registration as ConvertType — the structural type path is where
        // field/parameter/base-list references flow through.
        CollectKnownTypeNamespaceUsing(context, leafName);

        string typeName = !isLocalLeaf && (
            TypeMapping.TypeMappingResolver.TryMapTypeLeafName(context, strippedName, out var mappedLeaf)
            || TypeMapping.TypeMappingResolver.TryMapTypeLeafName(context, dottedName, out mappedLeaf))
            ? mappedLeaf
            : ConvertType(context, dottedName, synthesizeRawArguments: false);

        TypeSyntax typeSyntax;

        if (typeArgs is { Count: > 0 })
        {
            // Rebuild the full generic form and route it through NormalizeTypeName so that roots
            // mapped to a non-generic C# type (e.g. object via java.util.stream.Collector) cannot
            // smuggle invalid arguments past the conversion (object<String> → object).
            // Bare wildcards resolve through the constraint index using the argument position.
            var argList = new List<string>(typeArgs.Count);

            for (int i = 0; i < typeArgs.Count; i++)
            {
                var t = typeArgs[i];
                argList.Add(IsWildcardType(t)
                    ? ConvertWildcardType(context, t.toString(), typeName, i)
                    : ConvertTypeSyntax(context, t, 0).ToString());
            }

            // A mapped class target with $N placeholders consumes the Java arguments itself.
            if (typeName.Contains('$'))
            {
                typeSyntax = SyntaxFactory.ParseTypeName(NormalizeTypeName(ApplyClassTargetTemplate(typeName, argList)));
            }
            else
            {
                typeSyntax = SyntaxFactory.ParseTypeName(NormalizeTypeName(typeName + "<" + string.Join(", ", argList) + ">"));
            }
        }
        else if (typeName.Contains('$'))
        {
            // Raw usage of a placeholder-mapped type: unfilled positions become object.
            typeSyntax = SyntaxFactory.ParseTypeName(NormalizeTypeName(ApplyClassTargetTemplate(typeName, [])));
        }
        else if (synthesizeRawArguments)
        {
            // Raw usage of an ordinary generic: fill positions from the arity index / BCL defaults.
            typeSyntax = SyntaxFactory.ParseTypeName(
                SynthesizeRawGenericTypeArguments(context, NormalizeTypeName(typeName), synthesize: true));
        }
        else
        {
            typeSyntax = SyntaxFactory.ParseTypeName(typeName);
        }

        return typeSyntax;
    }

    public static ArgumentListSyntax GetSyntaxFromArguments(ConversionContext context, java.util.List args)
    {
        return SyntaxFactory.ArgumentList(GetSeparatedListFromArguments(context, args));
    }

    private static SeparatedSyntaxList<ArgumentSyntax> GetSeparatedListFromArguments(ConversionContext context, java.util.List args)
    {
        return GetSeparatedListFromArguments(context, args.OfType<Expression>());
    }

    private static SeparatedSyntaxList<ArgumentSyntax> GetSeparatedListFromArguments(ConversionContext context, IEnumerable<Expression> args)
    {
        var argSyntaxes = new List<ArgumentSyntax>();

        foreach (var arg in args)
        {
            var argSyntax = ExpressionVisitor.VisitExpression(context, arg);
            if (argSyntax is null)
            {
                continue;
            }

            argSyntaxes.Add(SyntaxFactory.Argument(argSyntax));
        }

        var separators = argSyntaxes.Count == 0
            ? Enumerable.Empty<SyntaxToken>()
            : Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken), argSyntaxes.Count - 1);
        return SyntaxFactory.SeparatedList(argSyntaxes, separators);
    }

    /// <summary>
    /// Returns the list of C# type parameter constraints that must be added to a class syntax
    /// to convert the java bounded type parameters of a class declaration.
    /// e.g. to convert <![CDATA[ <T extends Clazz<T>> ]]> into <![CDATA[ <T> where T : Clazz<T> ]]>
    /// </summary>
    public static IEnumerable<TypeParameterConstraintClauseSyntax> GetTypeParameterListConstraints(ConversionContext context, List<TypeParameter> typeParams)
    {
        var typeParameterConstraints = new List<TypeParameterConstraintClauseSyntax>();

        foreach (TypeParameter typeParam in typeParams)
        {
            if (typeParam.getTypeBound().size() > 0)
            {
                var constraintTypes = new List<TypeSyntax>();

                foreach (ClassOrInterfaceType bound in typeParam.getTypeBound())
                {
                    // Constraint clauses have no diamond inference, so raw generic bounds must
                    // gain their arity arguments here (CS0305).
                    var constraintSyntax = GetSyntaxFromType(context, bound, synthesizeRawArguments: true);

                    // A bound that converts to bare `object` (mapped-away types) is meaningless
                    // as a C# constraint — `T : object` is CS0702.
                    if (constraintSyntax.ToString().Trim() == "object")
                    {
                        continue;
                    }

                    constraintTypes.Add(constraintSyntax);
                }

                // C# requires a CLASS-type constraint to precede interface constraints (CS0406).
                // Java's bound order is declaration order, and mapped/reshaped bounds can flip
                // which one is the class: sort class-like bounds first. Classification: a bound
                // whose leaf is a tree-declared interface or a BCL-style `I`-prefixed interface
                // is an interface; everything else is class-like.
                constraintTypes.Sort((a, b) =>
                    IsInterfaceLikeConstraint(context, a.ToString())
                        .CompareTo(IsInterfaceLikeConstraint(context, b.ToString())));

                var typeConstraintsSyntax = new SeparatedSyntaxList<TypeParameterConstraintSyntax>();

                foreach (var constraintType in constraintTypes)
                {
                    typeConstraintsSyntax = typeConstraintsSyntax.Add(SyntaxFactory.TypeConstraint(constraintType));
                }

                // Every bound may filter away (java.lang.Enum bounds convert to bare `object`,
                // which CS0702 forbids) — emitting the skeleton `where T :` with an empty list is
                // CS1031. No surviving constraints means no clause at all.
                if (typeConstraintsSyntax.Count == 0)
                {
                    continue;
                }

                var typeIdentifier = SyntaxFactory.IdentifierName(typeParam.getName().asString());
                var parameterConstraintClauseSyntax = SyntaxFactory.TypeParameterConstraintClause(typeIdentifier, typeConstraintsSyntax);

                typeParameterConstraints.Add(parameterConstraintClauseSyntax);
            }
        }

        return typeParameterConstraints;
    }

    private static bool IsInterfaceLikeConstraint(ConversionContext context, string constraintText)
    {
        var leaf = constraintText.Split('<')[0].Trim().Split('.')[^1].Trim();

        if (context.Options.KnownInterfaceNames is { Count: > 0 } known
            && (known.Contains(leaf) || known.Contains(leaf.ToLowerInvariant())))
        {
            return true;
        }

        // BCL conventions: mapped interface targets are I-prefixed (IComparable, IEnumerable,
        // IEquatable, IFormattable...). A single-letter-ish or lowercase leaf is a type
        // parameter — treat it as an interface-neutral entry (stable order).
        return leaf.Length > 1
            && leaf[0] == 'I'
            && char.IsUpper(leaf[1]);
    }
}


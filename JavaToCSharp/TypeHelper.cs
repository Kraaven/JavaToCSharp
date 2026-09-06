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
        ["Predicate"] = "Func<bool>",
        ["BiPredicate"] = "Func<bool>",
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

    public static string ConvertTypeOf(Ast.nodeTypes.NodeWithType typedNode)
    {
        return ConvertType(typedNode.getType().toString());
    }

    public static string ConvertType(Type type)
    {
        return ConvertType(type.toString());
    }

    /// <summary>
    /// Converts a Java wildcard type string to a C# type.
    /// Handles: "? extends X" -> "X", "? super X" -> "X", "?" -> "object".
    /// </summary>
    public static string ConvertWildcardType(string typeName)
    {
        var trimmed = typeName.Trim();
        if (!trimmed.StartsWith('?'))
        {
            return typeName;
        }

        if (trimmed == "?")
        {
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
    /// (<c>FrameGraphBuilder.@Nullable Pass</c>). An at-sign followed by an uppercase letter can
    /// only ever be a leaked Java annotation — C# verbatim identifiers always prefix a keyword
    /// (<c>@base</c>) or a name that needs no escaping — so the whole <c>@Name</c> token and its
    /// trailing whitespace are removed. A leading dot is preserved so the enclosing type name
    /// re-joins the member type (<c>FrameGraphBuilder.Pass</c>).
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex TypeUseAnnotationRegex = new(
        @"(?<dot>\.)?\s*@[A-Z][A-Za-z0-9_]*\s+",
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
        typeName = StripTypeUseAnnotations(typeName);

        if (typeName.TrimStart().StartsWith('?'))
        {
            return ConvertWildcardType(typeName);
        }

        var parsed = TypeNameParser.ParseTypeName(typeName, s => _typeNameConversions.TryGetValue(s, out string? converted) ? converted : s);
        return NormalizeTypeName(EraseWildcards(parsed));
    }

    /// <summary>
    /// Converts a type name, first rewriting mapped names through <see cref="TypeMappingIndex"/>
    /// (when configured) and then applying the built-in dictionary fallback. Mapped names override
    /// the dictionary (e.g. <c>Map</c> → <c>IDictionary</c> instead of <c>Dictionary</c>).
    /// </summary>
    public static string ConvertType(ConversionContext context, string typeName)
    {
        typeName = StripTypeUseAnnotations(typeName);

        // A simple name that refers to a type declared in this file (nested/local class, record,
        // enum) must not go through built-in or mapped conversions — a nested record named
        // "Double" is a type, not the C# "double" keyword.
        var declaredName = ExtractSimpleTypeName(typeName);
        if (declaredName is not null && context.DeclaredTypeNames.Contains(declaredName))
        {
            return NormalizeTypeName(EraseWildcards(typeName));
        }

        if (context.Options.MappingIndex is not null)
        {
            TypeMapping.TypeMappingResolver.TryResolveTypeName(context, typeName, out var mapped);
            return NormalizeTypeName(EraseWildcards(mapped));
        }

        return ConvertType(typeName);
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
        => ConvertType(context, type.toString());

    public static string ConvertTypeOf(ConversionContext context, Ast.nodeTypes.NodeWithType typedNode)
        => ConvertType(context, typedNode.getType().toString());

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
    /// Builds a C# type syntax from a Java class/interface type, resolving the type name (and its
    /// generic arguments) through the mapping index when configured.
    /// </summary>
    public static TypeSyntax GetSyntaxFromType(ConversionContext context, ClassOrInterfaceType type)
    {
        string typeName = ConvertType(context, type.getNameAsString());
        var typeArgs = type.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();

        TypeSyntax typeSyntax;

        if (typeArgs is { Count: > 0 })
        {
            // Rebuild the full generic form and route it through NormalizeTypeName so that roots
            // mapped to a non-generic C# type (e.g. object via java.util.stream.Collector) cannot
            // smuggle invalid arguments past the conversion (object<String> → object).
            var args = string.Join(", ", typeArgs.Select(t =>
                IsWildcardType(t) ? ConvertWildcardType(t.toString()) : ConvertType(context, t)));
            typeSyntax = SyntaxFactory.ParseTypeName(NormalizeTypeName(typeName + "<" + args + ">"));
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
                var typeConstraintsSyntax = new SeparatedSyntaxList<TypeParameterConstraintSyntax>();

                foreach (ClassOrInterfaceType bound in typeParam.getTypeBound())
                {
                    typeConstraintsSyntax = typeConstraintsSyntax.Add(SyntaxFactory.TypeConstraint(GetSyntaxFromType(context, bound)));
                }

                var typeIdentifier = SyntaxFactory.IdentifierName(typeParam.getName().asString());
                var parameterConstraintClauseSyntax = SyntaxFactory.TypeParameterConstraintClause(typeIdentifier, typeConstraintsSyntax);

                typeParameterConstraints.Add(parameterConstraintClauseSyntax);
            }
        }

        return typeParameterConstraints;
    }
}

using System.Text.RegularExpressions;
using com.github.javaparser.ast;
using com.github.javaparser.ast.expr;
using com.github.javaparser.ast.type;
using JavaToCSharp.Expressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ast = com.github.javaparser.ast;
using Type = com.github.javaparser.ast.type.Type;

namespace JavaToCSharp.TypeMapping;

/// <summary>Result of attempting to resolve a member access through the mapping set.</summary>
public enum MappingResolutionStatus
{
    /// <summary>No mapping applied; the caller should convert the node with the default logic.</summary>
    Unresolved,

    /// <summary>A mapping produced the output expression.</summary>
    Resolved,

    /// <summary>The mapping set says this member has no safe mapping; the output contains the
    /// original Java fragment annotated with a <c>TODO(java-to-csharp)</c> comment.</summary>
    Unsupported,
}

/// <summary>
/// Resolves Java member access chains (e.g. <c>java.lang.Math.abs(x)</c> or
/// <c>System.out.println(x)</c>) against a <see cref="TypeMappingIndex"/>. Implements the
/// chain resolution, lookup, unsupported/ambiguous fallback and REVIEW comment rules documented
/// in MAPPING_FILES.md.
/// </summary>
public static class TypeMappingResolver
{
    private static readonly Regex TemplatePlaceholderRegex = new(@"\$(\d+|rest)", RegexOptions.Compiled);

    private static readonly Regex QualifiedTypeNameRegex = new(
        @"[A-Za-z_$][A-Za-z0-9_$]*(?:\.[A-Za-z_$][A-Za-z0-9_$]*)*",
        RegexOptions.Compiled);

    private enum RootMatchResult
    {
        NotFound,
        Found,
        Ambiguous,
    }

    public static MappingResolutionStatus TryResolveMethodCall(
        ConversionContext context,
        MethodCallExpr call,
        out ExpressionSyntax? output)
    {
        output = null;
        var index = context.Options.MappingIndex;
        if (index is null)
        {
            return MappingResolutionStatus.Unresolved;
        }

        var chain = ExtractChain(call, isMethodCall: true);
        if (chain is null)
        {
            return MappingResolutionStatus.Unresolved;
        }

        var typeArgs = call.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();
        var status = ResolveChain(context, index, chain, call.getArguments(), isMethodCall: true, typeArgs, call, out output);
        if (status != MappingResolutionStatus.Unresolved)
        {
            return status;
        }

        return TryResolveVariableReceiver(context, index, chain, call.getArguments(), isMethodCall: true, typeArgs, call, out output);
    }

    public static MappingResolutionStatus TryResolveFieldAccess(
        ConversionContext context,
        FieldAccessExpr field,
        out ExpressionSyntax? output)
    {
        output = null;
        var index = context.Options.MappingIndex;
        if (index is null)
        {
            return MappingResolutionStatus.Unresolved;
        }

        var chain = ExtractChain(field, isMethodCall: false);
        if (chain is null)
        {
            return MappingResolutionStatus.Unresolved;
        }

        var status = ResolveChain(context, index, chain, null, isMethodCall: false, null, field, out output);
        if (status != MappingResolutionStatus.Unresolved)
        {
            return status;
        }

        return TryResolveVariableReceiver(context, index, chain, null, isMethodCall: false, null, field, out output);
    }

    /// <summary>
    /// Flattens a member access chain into an ordered list of names (outermost first).
    /// Only chains built from <see cref="NameExpr"/> and <see cref="FieldAccessExpr"/> scopes
    /// are considered; a method call is only allowed as the outermost node (intermediate
    /// method calls carry arguments the chain cannot represent, so they are not resolvable).
    /// </summary>
    private static List<string>? ExtractChain(Expression node, bool isMethodCall)
    {
        var names = new List<string>();
        var current = node;
        var first = true;

        while (true)
        {
            switch (current)
            {
                case MethodCallExpr methodCall when first && isMethodCall:
                    names.Add(methodCall.getNameAsString());
                    var callScope = methodCall.getScope().FromOptional<Expression>();
                    if (callScope is null)
                    {
                        names.Reverse();
                        return names;
                    }

                    current = callScope;
                    break;

                case FieldAccessExpr fieldAccess:
                    names.Add(fieldAccess.getNameAsString());
                    current = fieldAccess.getScope();
                    break;

                case NameExpr nameExpr:
                    names.Add(nameExpr.getNameAsString());
                    names.Reverse();
                    return names;

                default:
                    return null;
            }

            first = false;
        }
    }

    private static MappingResolutionStatus ResolveChain(
        ConversionContext context,
        TypeMappingIndex index,
        List<string> segments,
        java.util.List? args,
        bool isMethodCall,
        List<Type>? typeArgs,
        Node node,
        out ExpressionSyntax? output)
    {
        output = null;

        var (rootResult, root, consumed) = FindRoot(index, segments);
        if (rootResult == RootMatchResult.Ambiguous)
        {
            output = EmitUnsupported(null, segments[0], "", node.toString());
            return MappingResolutionStatus.Unsupported;
        }

        if (rootResult == RootMatchResult.NotFound || root is null)
        {
            return MappingResolutionStatus.Unresolved;
        }

        return ResolveFromRoot(context, index, segments, consumed, root, null, args, isMethodCall, typeArgs, node, out output);
    }

    /// <summary>
    /// Resolves the remaining chain segments against a known root class. When <paramref name="receiver"/>
    /// is provided (a variable whose declared type maps to <paramref name="root"/>), it is used as the
    /// receiver expression instead of the mapped C# class name.
    /// </summary>
    private static MappingResolutionStatus ResolveFromRoot(
        ConversionContext context,
        TypeMappingIndex index,
        List<string> segments,
        int consumed,
        ClassMapping root,
        ExpressionSyntax? receiver,
        java.util.List? args,
        bool isMethodCall,
        List<Type>? typeArgs,
        Node node,
        out ExpressionSyntax? output)
    {
        output = null;
        var line = node.getBegin().FromOptional<com.github.javaparser.Position>()?.line ?? -1;

        var remaining = segments.Skip(consumed).ToList();
        var current = root;
        var traversed = new List<ClassMapping> { current };

        for (var i = 0; i < remaining.Count; i++)
        {
            var isLast = i == remaining.Count - 1;
            var entry = FindMember(current, remaining[i], isLast, args, isMethodCall, out var ambiguous);
            if (ambiguous)
            {
                output = EmitUnsupported(null, current.JavaClass, remaining[i], node.toString());
                return MappingResolutionStatus.Unsupported;
            }

            if (entry is null)
            {
                return MappingResolutionStatus.Unresolved;
            }

            if (entry.Kind == MemberMappingKind.Unsupported)
            {
                output = EmitUnsupported(entry, current.JavaClass, remaining[i], node.toString());
                return MappingResolutionStatus.Unsupported;
            }

            if (entry.ClassMap.Count > 0)
            {
                if (isLast)
                {
                    if (entry.CSharpMember is null)
                    {
                        output = EmitUnsupported(entry, current.JavaClass, remaining[i], node.toString());
                        return MappingResolutionStatus.Unsupported;
                    }

                    var routedScope = receiver ?? SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(current.CSharpClass));
                    output = ApplyReview(
                        MemberAccess(routedScope, entry.CSharpMember),
                        entry);
                    CollectUsings(context, traversed, line);
                    return MappingResolutionStatus.Resolved;
                }

                // Routing nodes are elided mid-chain: descend into the nested class that
                // owns the next segment without emitting anything for this segment.
                var nextIsLast = i + 1 == remaining.Count - 1;
                var owner = FindNestedOwner(entry.ClassMap, remaining[i + 1], nextIsLast, args);
                if (owner is not null)
                {
                    current = owner;
                    traversed.Add(current);
                    continue;
                }

                // No nested class owns the next segment: if the routing node has a member
                // name, treat it as a leaf and convert the remaining tail normally.
                if (entry.CSharpMember is null)
                {
                    return MappingResolutionStatus.Unresolved;
                }
            }

            // Leaf: emit the mapped member, converting any remaining tail segments normally.
            var scope = receiver ?? SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(current.CSharpClass));
            if (!isLast)
            {
                var leafScope = EmitLeaf(context, entry, scope, null, false, null);
                var tail = remaining.Skip(i + 1).ToList();
                output = ApplyReview(ConvertTail(context, tail, args, isMethodCall, leafScope), entry);
            }
            else
            {
                output = ApplyReview(EmitLeaf(context, entry, scope, args, isMethodCall, typeArgs), entry);
            }

            CollectUsings(context, traversed, line);
            return MappingResolutionStatus.Resolved;
        }

        return MappingResolutionStatus.Unresolved;
    }

    /// <summary>
    /// Fallback for member access on a plain variable whose declared Java type is mapped
    /// (e.g. <c>sb.append(x)</c> where <c>sb</c> is a StringBuilder). Resolves the call against
    /// the mapped class of the variable's type, emitting the variable name as the receiver.
    /// </summary>
    private static MappingResolutionStatus TryResolveVariableReceiver(
        ConversionContext context,
        TypeMappingIndex index,
        List<string> segments,
        java.util.List? args,
        bool isMethodCall,
        List<Type>? typeArgs,
        Node node,
        out ExpressionSyntax? output)
    {
        output = null;

        if (segments.Count < 2 || !context.TryGetVariableType(segments[0], out var declared))
        {
            return MappingResolutionStatus.Unresolved;
        }

        var rawClass = StripGenericsAndArrays(declared);
        if (rawClass.Length == 0 || !index.TryGetByDottedName(rawClass, out var cls, out var ambiguous))
        {
            return MappingResolutionStatus.Unresolved;
        }

        if (ambiguous)
        {
            output = EmitUnsupported(null, segments[0], "", node.toString());
            return MappingResolutionStatus.Unsupported;
        }

        var receiver = SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(segments[0]));
        return ResolveFromRoot(context, index, segments, 1, cls!, receiver, args, isMethodCall, typeArgs, node, out output);
    }

    internal static string StripGenericsAndArrays(string javaType)
    {
        int lt = javaType.IndexOf('<');
        if (lt >= 0)
        {
            javaType = javaType[..lt];
        }

        while (javaType.EndsWith("[]", StringComparison.Ordinal))
        {
            javaType = javaType[..^2];
        }

        return javaType.Trim();
    }

    /// <summary>
    /// Rewrites the dotted type names inside a raw Java type string (e.g. <c>Map.Entry&lt;String, Integer&gt;</c>)
    /// through the mapping index, substituting each matched class with its C# name and collecting the
    /// required usings. Names without a mapping (user types, primitives, wildcards) are left untouched.
    /// Returns true when at least one substitution was applied.
    /// </summary>
    public static bool TryResolveTypeName(ConversionContext context, string javaType, out string converted)
    {
        var index = context.Options.MappingIndex;
        if (index is null)
        {
            converted = javaType;
            return false;
        }

        var applied = false;
        converted = QualifiedTypeNameRegex.Replace(javaType, match =>
        {
            var name = match.Value;
            if (!index.TryGetByDottedName(name, out var cls, out var ambiguous) || cls is null || ambiguous)
            {
                return TypeHelper.ConvertType(name);
            }

            CollectUsings(context, [cls], 0);
            applied = true;
            return cls.CSharpClass;
        });

        return applied;
    }

    private static (RootMatchResult Result, ClassMapping? Root, int Consumed) FindRoot(TypeMappingIndex index, List<string> segments)
    {
        // Longest exact qualified-name prefix (e.g. java.lang.Math in java.lang.Math.abs).
        for (var length = segments.Count - 1; length >= 1; length--)
        {
            var candidate = string.Join(".", segments.Take(length));
            if (index.TryGetByQualifiedName(candidate, out var cls))
            {
                return (RootMatchResult.Found, cls, length);
            }
        }

        // The first segment registered directly as a (root-level) java_class.
        if (index.TryGetByQualifiedName(segments[0], out var direct))
        {
            return (RootMatchResult.Found, direct, 1);
        }

        // Unambiguous simple-name fallback.
        if (index.TryGetBySimpleName(segments[0], out var simple, out var ambiguous))
        {
            return (RootMatchResult.Found, simple, 1);
        }

        if (ambiguous)
        {
            return (RootMatchResult.Ambiguous, null, 0);
        }

        return (RootMatchResult.NotFound, null, 0);
    }

    internal static MemberMapping? FindMemberPublic(
        ClassMapping cls,
        string member,
        int argCount,
        java.util.List? args,
        bool isMethodCall)
    {
        return FindMember(cls, member, isLast: true, args, isMethodCall, out _);
    }

    private static MemberMapping? FindMember(
        ClassMapping cls,
        string member,
        bool isLast,
        java.util.List? args,
        bool isMethodCall,
        out bool ambiguous)
    {
        ambiguous = false;

        var callArgs = isMethodCall && isLast ? args : null;
        List<MemberMapping> candidates;
        if (callArgs is not null)
        {
            var argCount = callArgs.size();
            candidates = cls.Entries
                .Where(e => e.JavaMember == member
                    && e.MemberType == MemberMappingType.Method
                    && (e.IsVariadic || e.ArgCount == argCount))
                .ToList();
        }
        else
        {
            candidates = cls.Entries
                .Where(e => e.JavaMember == member && e.MemberType == MemberMappingType.Field)
                .ToList();
        }

        if (candidates.Count == 0)
        {
            return null;
        }

        if (candidates.Count == 1)
        {
            return candidates[0];
        }

        // Multiple entries share (java_member, arg_count): prefer a type_hint match when the
        // argument types are known, then fall back to the single entry without a type_hint.
        if (callArgs is not null)
        {
            var inferred = callArgs.OfType<Expression>().Select(InferArgType).ToList();
            var matched = candidates
                .Where(c => c.TypeHint is not null && TypeHintMatches(c.TypeHint, inferred))
                .ToList();
            if (matched.Count == 1)
            {
                return matched[0];
            }
        }

        var withoutHint = candidates.Where(c => c.TypeHint is null).ToList();
        if (withoutHint.Count == 1)
        {
            return withoutHint[0];
        }

        ambiguous = true;
        return null;
    }

    private static ClassMapping? FindNestedOwner(IReadOnlyList<ClassMapping> nestedClasses, string member, bool isLast, java.util.List? args)
    {
        ClassMapping? owner = null;
        foreach (var nested in nestedClasses)
        {
            if (HasMember(nested, member, isLast, args))
            {
                if (owner is not null)
                {
                    return null;
                }

                owner = nested;
            }
        }

        return owner;
    }

    private static bool HasMember(ClassMapping cls, string member, bool isLast, java.util.List? args)
    {
        if (isLast && args is not null)
        {
            var argCount = args.size();
            return cls.Entries.Any(e => e.JavaMember == member
                && e.MemberType == MemberMappingType.Method
                && (e.IsVariadic || e.ArgCount == argCount));
        }

        return cls.Entries.Any(e => e.JavaMember == member && e.MemberType == MemberMappingType.Field);
    }

    private static ExpressionSyntax EmitLeaf(
        ConversionContext context,
        MemberMapping entry,
        ExpressionSyntax scope,
        java.util.List? args,
        bool isMethodCall,
        List<Type>? typeArgs)
    {
        switch (entry.Kind)
        {
            case MemberMappingKind.Rename:
                if (isMethodCall && args is not null)
                {
                    var methodName = CreateMethodName(entry.CSharpMember ?? entry.JavaMember, typeArgs);
                    var memberAccess = SyntaxFactory.MemberAccessExpression(
                        SyntaxKind.SimpleMemberAccessExpression, scope, methodName);
                    return SyntaxFactory.InvocationExpression(memberAccess, TypeHelper.GetSyntaxFromArguments(context, args));
                }

                return MemberAccess(scope, entry.CSharpMember ?? entry.JavaMember);

            case MemberMappingKind.Property:
                return MemberAccess(scope, entry.CSharpMember ?? entry.JavaMember);

            case MemberMappingKind.Template:
                if (TryFillTemplate(context, scope.NormalizeWhitespace().ToFullString(), entry.Expression ?? "", args, out var templateSyntax))
                {
                    return templateSyntax;
                }

                throw new InvalidOperationException(
                    $"Mapping template '{entry.Expression}' for {entry.OwnerClass}.{entry.JavaMember} is not a valid C# expression.");

            default:
                throw new InvalidOperationException($"Unexpected mapping kind '{entry.Kind}'.");
        }
    }

    private static ExpressionSyntax ConvertTail(
        ConversionContext context,
        List<string> tail,
        java.util.List? args,
        bool isMethodCall,
        ExpressionSyntax scope)
    {
        var result = scope;
        for (var i = 0; i < tail.Count; i++)
        {
            var name = TypeHelper.EscapeIdentifier(DefaultMemberName(context, tail[i]));
            if (i == tail.Count - 1 && isMethodCall && args is not null)
            {
                var memberAccess = SyntaxFactory.MemberAccessExpression(
                    SyntaxKind.SimpleMemberAccessExpression, result, SyntaxFactory.IdentifierName(name));
                return SyntaxFactory.InvocationExpression(memberAccess, TypeHelper.GetSyntaxFromArguments(context, args));
            }

            result = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression, result, SyntaxFactory.IdentifierName(name));
        }

        return result;
    }

    private static string DefaultMemberName(ConversionContext context, string name)
    {
        var result = context.PreserveJavaMethodNames ? name : TypeHelper.Capitalize(name);
        return context.PreserveJavaMethodNames ? result : TypeHelper.ReplaceCommonMethodNames(result);
    }

    private static SimpleNameSyntax CreateMethodName(string name, List<Type>? typeArgs)
    {
        var escaped = TypeHelper.EscapeIdentifier(name);
        if (typeArgs is not { Count: > 0 })
        {
            return SyntaxFactory.IdentifierName(escaped);
        }

        var typeArgSyntaxes = typeArgs
            .Select(t => SyntaxFactory.ParseTypeName(TypeHelper.ConvertType(t.toString())))
            .ToArray();
        return SyntaxFactory.GenericName(escaped).AddTypeArgumentListArguments(typeArgSyntaxes);
    }

    private static bool TryFillTemplate(
        ConversionContext context,
        string receiver,
        string template,
        java.util.List? args,
        out ExpressionSyntax syntax)
    {
        var argSyntaxes = new List<ExpressionSyntax>();
        if (args is not null)
        {
            foreach (var arg in args.OfType<Expression>())
            {
                var argSyntax = ExpressionVisitor.VisitExpression(context, arg);
                if (argSyntax is not null)
                {
                    argSyntaxes.Add(argSyntax);
                }
            }
        }

        var referencedIndexes = new List<int>();
        foreach (Match match in TemplatePlaceholderRegex.Matches(template))
        {
            var value = match.Groups[1].Value;
            if (value != "rest" && int.TryParse(value, out var index))
            {
                referencedIndexes.Add(index);
            }
        }

        var maxIndex = referencedIndexes.Count == 0 ? 0 : referencedIndexes.Max();

        string Fill(Match match)
        {
            var value = match.Groups[1].Value;
            if (value == "rest")
            {
                return string.Join(", ", argSyntaxes.Skip(maxIndex).Select(a => a.NormalizeWhitespace().ToFullString()));
            }

            if (value == "0")
            {
                return receiver;
            }

            var argIndex = int.Parse(value) - 1;
            return argIndex < argSyntaxes.Count
                ? argSyntaxes[argIndex].NormalizeWhitespace().ToFullString()
                : match.Value;
        }

        var filled = TemplatePlaceholderRegex.Replace(template, Fill);
        try
        {
            syntax = SyntaxFactory.ParseExpression(filled, consumeFullText: true);
            return true;
        }
        catch (Exception)
        {
            syntax = SyntaxFactory.IdentifierName(filled);
            return false;
        }
    }

    private static string? InferArgType(Expression arg) => arg switch
    {
        IntegerLiteralExpr => "int",
        LongLiteralExpr => "long",
        DoubleLiteralExpr => "double",
        BooleanLiteralExpr => "boolean",
        CharLiteralExpr => "char",
        StringLiteralExpr => "String",
        NullLiteralExpr => null,
        NameExpr name => name.getNameAsString(),
        ObjectCreationExpr creation => creation.getType().getNameAsString(),
        CastExpr cast => cast.getType().toString(),
        UnaryExpr unary => InferArgType(unary.getExpression()),
        _ => null,
    };

    private static bool TypeHintMatches(IReadOnlyList<string>? hint, List<string?> inferred)
    {
        if (hint is null || hint.Count != inferred.Count)
        {
            return false;
        }

        for (var i = 0; i < hint.Count; i++)
        {
            var actual = inferred[i];
            if (actual is null)
            {
                continue;
            }

            if (!TypeNamesEqual(hint[i], actual))
            {
                return false;
            }
        }

        return true;
    }

    private static bool TypeNamesEqual(string a, string b)
    {
        if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var aSimple = a.Contains('.') ? a[(a.LastIndexOf('.') + 1)..] : a;
        var bSimple = b.Contains('.') ? b[(b.LastIndexOf('.') + 1)..] : b;
        return string.Equals(aSimple, bSimple, StringComparison.OrdinalIgnoreCase);
    }

    private static MemberAccessExpressionSyntax MemberAccess(ExpressionSyntax scope, string member)
        => SyntaxFactory.MemberAccessExpression(
            SyntaxKind.SimpleMemberAccessExpression,
            scope,
            SyntaxFactory.IdentifierName(TypeHelper.EscapeIdentifier(member)));

    private static void CollectUsings(ConversionContext context, List<ClassMapping> classes, int line)
    {
        var index = context.Options.MappingIndex;
        if (index is null)
        {
            return;
        }

        foreach (var cls in classes)
        {
            var package = PackageOf(cls.JavaClass);
            if (package.Length == 0)
            {
                continue;
            }

            if (!index.TryGetCSharpPackages(package, out var csharpPackages))
            {
                context.Warning(
                    $"No namespace mapping found for Java package '{package}' (used by '{cls.JavaClass}').",
                    line);
                continue;
            }

            context.MappingUsings.Add(csharpPackages[0]);
        }
    }

    private static string PackageOf(string javaClass)
    {
        var lastDot = javaClass.LastIndexOf('.');
        return lastDot < 0 ? "" : javaClass[..lastDot];
    }

    private static ExpressionSyntax ApplyReview(ExpressionSyntax syntax, MemberMapping entry)
    {
        if (entry.Confidence == MappingConfidence.High)
        {
            return syntax;
        }

        var confidence = entry.Confidence == MappingConfidence.Medium ? "medium" : "low";
        var note = string.IsNullOrWhiteSpace(entry.Notes)
            ? $"mapping for {entry.OwnerClass}.{entry.JavaMember} applied with {confidence} confidence"
            : entry.Notes;
        var comment = $"// REVIEW(java-to-csharp): {confidence} confidence — {note}{Environment.NewLine}";
        var trailing = syntax.GetTrailingTrivia();
        return syntax.WithTrailingTrivia(SyntaxFactory.TriviaList(SyntaxFactory.Comment(comment)).AddRange(trailing));
    }

    private static ExpressionSyntax EmitUnsupported(MemberMapping? entry, string ownerClass, string member, string fragment)
    {
        string message;
        if (entry is not null)
        {
            message = string.IsNullOrWhiteSpace(entry.Notes)
                ? $"no safe mapping for {ownerClass}.{member}"
                : $"no safe mapping for {ownerClass}.{member} — {entry.Notes}";
        }
        else
        {
            message = $"no safe mapping found for '{fragment}'";
        }

        var commentLines = message
            .Split(Environment.NewLine)
            .Select(l => (SyntaxTrivia)SyntaxFactory.Comment($"// TODO(java-to-csharp): {l.Trim()}{Environment.NewLine}"));
        var trivia = SyntaxFactory.TriviaList(commentLines);

        ExpressionSyntax fragmentSyntax;
        try
        {
            fragmentSyntax = SyntaxFactory.ParseExpression(fragment, consumeFullText: true);
        }
        catch (Exception)
        {
            fragmentSyntax = SyntaxFactory.IdentifierName(fragment);
        }

        return fragmentSyntax.WithLeadingTrivia(trivia);
    }
}

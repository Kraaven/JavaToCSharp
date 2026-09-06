using com.github.javaparser.ast.expr;
using JavaToCSharp.TypeMapping;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Ast = com.github.javaparser.ast;
using Type = com.github.javaparser.ast.type.Type;

namespace JavaToCSharp.Expressions;

public class MethodCallExpressionVisitor : ExpressionVisitor<MethodCallExpr>
{
    protected override ExpressionSyntax? Visit(ConversionContext context, MethodCallExpr methodCallExpr)
    {
        if (context.Options.MappingIndex is not null)
        {
            var status = TypeMappingResolver.TryResolveMethodCall(context, methodCallExpr, out var mappedSyntax);
            if (status != MappingResolutionStatus.Unresolved)
            {
                return mappedSyntax;
            }
        }

        var scope = methodCallExpr.getScope().FromOptional<Expression>();
        ExpressionSyntax? scopeSyntax = null;

        // Object-method equivalents (equals, hashCode, toString, getClass, …) always map to their
        // C# names, even when preserving Java method names; other names honor the preserve flag.
        var methodName = TypeHelper.ReplaceCommonMethodNames(
            context.PreserveJavaMethodNames
                ? methodCallExpr.getNameAsString()
                : TypeHelper.Capitalize(methodCallExpr.getNameAsString()));

        if (scope is not null)
        {
            scopeSyntax = VisitExpression(context, scope);
        }

        // Chained method casing: when the scope is itself a method call and the mapping
        // resolver couldn't resolve us, walk to the root variable and try to resolve
        // the current call against its mapped type.
        if (context.Options.MappingIndex is not null
            && scope is MethodCallExpr innerCall)
        {
            var rootName = FindRootReceiverName(innerCall);
            if (rootName is not null
                && scopeSyntax is not null)
            {
                var rootVarType = context.TryGetVariableType(rootName, out var vt) ? vt : null;
                var resolved = false;

                // Primary: resolve the current call against the root variable's declared type.
                if (rootVarType is not null
                    && TryResolveChainedCall(context, methodCallExpr, rootVarType, scopeSyntax, out var rootChainedName, out var rootFullExpression))
                {
                    if (rootFullExpression is not null)
                    {
                        return rootFullExpression;
                    }

                    methodName = rootChainedName;
                    resolved = true;
                }

                // Fallback: consult the inner call's returns_type. This covers class-rooted chains
                // (e.g. LocalDate.now().plusSeconds(...)) as well as variable-rooted chains whose
                // call did not resolve against the root variable's type (e.g. map.get(k).getYear()).
                if (!resolved)
                {
                    var declaredType = ResolveReturnType(context, innerCall);
                    if (declaredType == TypeMappingResolver.GenericReturnType)
                    {
                        declaredType = ResolveGenericReceiverType(context, methodCallExpr);
                    }

                    if (declaredType is not null
                        && TryResolveChainedCall(context, methodCallExpr, declaredType, scopeSyntax, out var chainedName, out var fullExpression))
                    {
                        if (fullExpression is not null)
                        {
                            return fullExpression;
                        }

                        methodName = chainedName;
                    }
                }
            }
        }

        SimpleNameSyntax methodSimpleName = CreateMethodName(context, methodCallExpr, methodName);

        ExpressionSyntax methodExpression;

        if (scopeSyntax is null)
        {
            methodExpression = methodSimpleName;
        }
        else
        {
            methodExpression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                scopeSyntax,
                methodSimpleName);
        }

        var args = methodCallExpr.getArguments();

        if (args is null || args.size() == 0)
        {
            return SyntaxFactory.InvocationExpression(methodExpression);
        }

        return SyntaxFactory.InvocationExpression(methodExpression, TypeHelper.GetSyntaxFromArguments(context, args));
    }


    /// <summary>
    /// Walks a scope chain to find the root receiver variable name.
    /// E.g. for <c>sb.append("x").append("y")</c>, walking the scope
    /// of the outer call eventually reaches <c>NameExpr("sb")</c>.
    /// For bare method calls at the root (e.g. <c>getMap().get("key")</c>),
    /// returns the root method name as a synthetic identifier.
    /// </summary>
    private static string? FindRootReceiverName(Expression scope)
    {
        var current = scope;
        while (true)
        {
            switch (current)
            {
                case MethodCallExpr mc:
                    current = mc.getScope().FromOptional<Expression>();
                    if (current is null)
                    {
                        return mc.getNameAsString();
                    }
                    break;
                case FieldAccessExpr fa:
                    current = fa.getScope();
                    break;
                case NameExpr ne:
                    return ne.getNameAsString();
                default:
                    return null;
            }
        }
    }

    /// <summary>
    /// Resolves a chained method call against the mapping index by looking up the
    /// receiver's declared type and finding the matching member entry. When the entry
    /// is a template, fills it and returns the full expression via <paramref name="fullExpression"/>.
    /// </summary>
    private static bool TryResolveChainedCall(
        ConversionContext context,
        MethodCallExpr call,
        string declaredType,
        ExpressionSyntax scope,
        out string resolvedName,
        out ExpressionSyntax? fullExpression)
    {
        resolvedName = call.getNameAsString();
        fullExpression = null;

        var index = context.Options.MappingIndex;
        if (index is null)
        {
            return false;
        }

        var rawClass = TypeMappingResolver.StripGenericsAndArrays(declaredType);
        if (rawClass.Length == 0 || !index.TryGetByDottedName(rawClass, out var cls, out _))
        {
            return false;
        }

        var callName = call.getNameAsString();
        var args = call.getArguments();

        var entry = TypeMappingResolver.FindMemberPublic(cls!, callName, args?.size() ?? 0, args, isMethodCall: true);
        if (entry is null || entry.Kind == MemberMappingKind.Unsupported)
        {
            return false;
        }

        if (entry.Kind == MemberMappingKind.Property)
        {
            fullExpression = SyntaxFactory.MemberAccessExpression(
                SyntaxKind.SimpleMemberAccessExpression,
                scope,
                SyntaxFactory.IdentifierName(entry.CSharpMember!));
            return true;
        }

        if (entry.Kind == MemberMappingKind.Rename && entry.CSharpMember is not null)
        {
            resolvedName = entry.CSharpMember;
            return true;
        }

        if (entry.Kind == MemberMappingKind.Template && entry.Expression is not null)
        {
            var receiverStr = scope.NormalizeWhitespace().ToFullString();
            if (TypeMappingResolver.TryFillTemplate(context, receiverStr, entry.Expression, args, out var templateSyntax))
            {
                fullExpression = templateSyntax;
                return true;
            }
        }

        return false;
    }

    private static SimpleNameSyntax CreateMethodName(ConversionContext context, MethodCallExpr methodCallExpr, string methodName)
    {
        var typeArgs = methodCallExpr.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();
        if (typeArgs is not { Count: > 0 })
        {
            return SyntaxFactory.IdentifierName(TypeHelper.EscapeMemberName(methodName));
        }

        var typeArgSyntaxes = typeArgs
            .Select(t => SyntaxFactory.ParseTypeName(TypeHelper.ConvertType(context, t.toString())))
            .ToArray();

        return SyntaxFactory.GenericName(TypeHelper.EscapeMemberName(methodName))
            .AddTypeArgumentListArguments(typeArgSyntaxes);
    }

    /// <summary>
    /// Attempts to resolve the return type of a method call by looking up its mapping entry.
    /// Walks the scope chain to find the class the method belongs to, then checks if the
    /// mapping entry has a <c>returns_type</c> declaration. Handles intermediate MethodCallExpr
    /// scopes by recursively resolving their return types.
    /// </summary>
    private static string? ResolveReturnType(ConversionContext context, MethodCallExpr call)
    {
        var index = context.Options.MappingIndex;
        if (index is null) return null;

        // Find the class this method belongs to by examining its scope
        var scope = call.getScope().FromOptional<Expression>();
        var className = ResolveReceiverType(context, scope);
        if (className is null) return null;

        var rawClass = TypeMappingResolver.StripGenericsAndArrays(className);
        if (rawClass.Length == 0 || !index.TryGetByDottedName(rawClass, out var cls, out _) || cls is null)
            return null;

        var entry = TypeMappingResolver.FindMemberPublic(cls, call.getNameAsString(), call.getArguments()?.size() ?? 0, call.getArguments(), isMethodCall: true);
        return entry?.ReturnsType;
    }

    /// <summary>
    /// Resolves the Java type of a receiver expression. For NameExpr, looks up the variable type.
    /// For MethodCallExpr, recursively resolves the method's return type via the mapping index.
    /// For FieldAccessExpr, walks up the scope chain.
    /// </summary>
    private static string? ResolveReceiverType(ConversionContext context, Expression? scope)
    {
        if (scope is null) return null;

        var index = context.Options.MappingIndex;
        if (index is null) return null;

        switch (scope)
        {
            case NameExpr ne:
            {
                var name = ne.getNameAsString();
                if (context.TryGetVariableType(name, out var varType))
                    return varType;

                // Might be a class reference like Pattern
                if (index.TryGetByDottedName(name, out var cls, out _))
                    return cls?.JavaClass;
                return null;
            }

            case MethodCallExpr mc:
                return ResolveReturnType(context, mc);

            case FieldAccessExpr fa:
            {
                var baseType = ResolveReceiverType(context, fa.getScope());
                if (baseType is null) return null;
                var rawClass = TypeMappingResolver.StripGenericsAndArrays(baseType);
                if (rawClass.Length == 0 || !index.TryGetByDottedName(rawClass, out var cls, out _) || cls is null)
                    return null;
                var entry = TypeMappingResolver.FindMemberPublic(cls, fa.getNameAsString(), 0, null, isMethodCall: false);
                return entry?.ReturnsType;
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Resolves the <see cref="TypeMappingResolver.GenericReturnType"/> sentinel: when a method's
    /// return type is unknown (any class), infer the concrete class from the members subsequently
    /// called on the result. Looks up the next member name in the reverse member index and, when
    /// exactly one mapped class provides a matching method, uses that class as the receiver type.
    /// </summary>
    private static string? ResolveGenericReceiverType(ConversionContext context, MethodCallExpr nextCall)
    {
        var index = context.Options.MappingIndex;
        if (index is null) return null;

        var memberName = nextCall.getNameAsString();
        var arity = nextCall.getArguments()?.size() ?? 0;

        var candidates = index
            .FindClassesByMember(memberName, context.FileMappingContext?.ActiveJavaPackages)
            .Where(m => m.MemberType == MemberMappingType.Method
                && m.Kind != MemberMappingKind.Unsupported
                && m.ArgCount is { } arityCount
                && (arityCount == arity || m.IsVariadic))
            .GroupBy(m => m.OwnerClass)
            .ToList();

        return candidates.Count == 1 ? candidates[0].Key : null;
    }
}

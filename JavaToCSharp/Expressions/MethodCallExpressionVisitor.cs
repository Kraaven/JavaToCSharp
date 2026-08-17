using com.github.javaparser.ast.expr;
using JavaToCSharp.TypeMapping;
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

        var methodName = context.PreserveJavaMethodNames
            ? methodCallExpr.getNameAsString()
            : TypeHelper.Capitalize(methodCallExpr.getNameAsString());
        methodName = TypeHelper.ReplaceCommonMethodNames(methodName);

        if (scope is not null)
        {
            scopeSyntax = VisitExpression(context, scope);
        }

        // Chained method casing: when the scope is itself a method call and the mapping
        // resolver couldn't resolve us, walk to the root variable and try to resolve
        // the current call against its mapped type.
        if (context.Options.MappingIndex is not null
            && scope is MethodCallExpr innerCall
            && !IsResolvedName(methodName, methodCallExpr.getNameAsString()))
        {
            var rootName = FindRootReceiverName(innerCall);
            if (rootName is not null
                && context.TryGetVariableType(rootName, out var declaredType)
                && TryResolveChainedCall(context, methodCallExpr, declaredType, out var chainedName))
            {
                methodName = chainedName;
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

    private static bool IsResolvedName(string currentName, string javaName)
    {
        return currentName != javaName;
    }

    /// <summary>
    /// Walks a scope chain to find the root receiver variable name.
    /// E.g. for <c>sb.append("x").append("y")</c>, walking the scope
    /// of the outer call eventually reaches <c>NameExpr("sb")</c>.
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
                    if (current is null) return null;
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
    /// receiver's declared type and finding the matching member entry.
    /// </summary>
    private static bool TryResolveChainedCall(
        ConversionContext context,
        MethodCallExpr call,
        string declaredType,
        out string resolvedName)
    {
        resolvedName = call.getNameAsString();

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
            resolvedName = entry.CSharpMember!;
            return true;
        }

        if (entry.Kind == MemberMappingKind.Rename && entry.CSharpMember is not null)
        {
            resolvedName = entry.CSharpMember;
            return true;
        }

        if (entry.Kind == MemberMappingKind.Template && entry.Expression is not null)
        {
            return false;
        }

        return false;
    }

    private static SimpleNameSyntax CreateMethodName(ConversionContext context, MethodCallExpr methodCallExpr, string methodName)
    {
        var typeArgs = methodCallExpr.getTypeArguments().FromOptional<Ast.NodeList>()?.ToList<Type>();
        if (typeArgs is not { Count: > 0 })
        {
            return SyntaxFactory.IdentifierName(methodName);
        }

        var typeArgSyntaxes = typeArgs
            .Select(t => SyntaxFactory.ParseTypeName(TypeHelper.ConvertType(context, t.toString())))
            .ToArray();

        return SyntaxFactory.GenericName(methodName)
            .AddTypeArgumentListArguments(typeArgSyntaxes);
    }
}
